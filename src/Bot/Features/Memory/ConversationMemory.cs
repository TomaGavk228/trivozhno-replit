using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Knowledge;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Infrastructure.Stories;
using Trivozhno.Resources;

namespace Trivozhno.Features.Memory;

public sealed record ConversationContext(IReadOnlyList<AiMessage> Messages, bool HasMood);
public sealed record SourceMetadata(string Type, string Title, int PageStart = 0, int PageEnd = 0, long ChunkId = 0, string? SourceId = null);
public sealed record ConversationAugmentation(IReadOnlyList<AiMessage> Messages, IReadOnlyList<SourceMetadata> Sources);

public interface IConversationMemory
{
    Task<ConversationContext> Build(BotUser user, ChatMessage current, CancellationToken ct);
    Task<ConversationAugmentation> Enrich(ConversationContext context, ChatMessage current, AiTurnDraft draft, CancellationToken ct);
    Task Summarize(Guid userId, long version, CancellationToken ct);
}

public sealed class ConversationMemory(BotDb db, Uk uk, IKnowledgeRetriever knowledge, IStorySource stories, IAiClient ai, BotOptions options,
    IClock clock, UserLocks locks, ILogger<ConversationMemory> log) : IConversationMemory
{
    public async Task<ConversationContext> Build(BotUser user, ChatMessage current, CancellationToken ct)
    {
        const int chatOutputReserve = 850;
        var budget = Math.Min(options.InputBudget, options.TokensPerMinute - chatOutputReserve);

        var previous = await db.Messages.AsNoTracking().Where(x => x.UserId == user.Id &&
                (x.Role == "user" && x.Id < current.Id || x.Role == "assistant" && x.ReplyToId < current.Id) &&
                (x.Status == "done" || x.Status == "unanswered") && (user.MoodContextEnabled || !x.MoodDerived))
            .OrderByDescending(x => x.ReplyToId ?? x.Id).ThenByDescending(x => x.Role).Take(24).ToListAsync(ct);
        previous = previous.OrderBy(x => x.ReplyToId ?? x.Id).ThenBy(x => x.Role == "assistant" ? 1 : 0).ToList();

        var userMessage = new AiMessage("user", current.Text);
        var core = new AiMessage("system", uk.ChatPrompt);
        if (TokenEstimate.Count([core, userMessage]) > budget) throw new ContextTooLargeException();

        var messages = new List<AiMessage> { core };

        var priorState = ExtractConversationState(previous.LastOrDefault(x => x.Role == "assistant")?.SourcesJson);
        if (!string.IsNullOrWhiteSpace(priorState))
        {
            var stateMessage = new AiMessage("system",
                "Короткий стан попереднього ходу розмови. Це робоча пам'ять, не текст для повторення користувачу:\n" + priorState);
            if (TokenEstimate.Count(messages.Append(stateMessage).Append(userMessage)) <= budget)
                messages.Add(stateMessage);
        }

        if (current.Text.Contains("звідки", StringComparison.OrdinalIgnoreCase) ||
            current.Text.Contains("джерело", StringComparison.OrdinalIgnoreCase))
        {
            var priorMetadata = previous.LastOrDefault(x => x.Role == "assistant")?.SourcesJson;
            if (!string.IsNullOrWhiteSpace(priorMetadata))
            {
                var sourceMessage = new AiMessage("system",
                    "Метадані джерел попередньої відповіді. Використай їх лише якщо людина питає походження інформації:\n" + priorMetadata);
                if (TokenEstimate.Count(messages.Append(sourceMessage).Append(userMessage)) <= budget)
                    messages.Add(sourceMessage);
            }
        }

        var summary = await db.Summaries.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == user.Id, ct);
        if (summary is not null && TokenEstimate.Count(summary.Text) <= 650)
        {
            var memory = new AiMessage("system", "Довготривала пам’ять користувача, лише факти й контекст:\n" + summary.Text);
            if (TokenEstimate.Count(messages.Append(memory).Append(userMessage)) <= budget) messages.Add(memory);
        }

        var hasMood = false;
        if (options.Mood && user.MoodContextEnabled)
        {
            var moods = await db.Moods.AsNoTracking().Where(x => x.UserId == user.Id && x.RecordedAt > clock.UtcNow.AddDays(-7))
                .OrderByDescending(x => x.RecordedAt).ThenByDescending(x => x.Id).Take(5).ToListAsync(ct);
            var moodText = string.Join('\n', moods.Select(x => $"{x.RecordedAt:u}: {x.Value}/5. {RelevantExcerpt(x.Note ?? "", current.Text, 450)}"));
            if (moodText.Length > 0)
            {
                var moodMessage = new AiMessage("system", "Останні нотатки настрою, лише довідкові дані:\n" + moodText);
                if (TokenEstimate.Count(messages.Append(moodMessage).Append(userMessage)) + 30 <= budget)
                {
                    messages.Add(moodMessage);
                    hasMood = true;
                }
            }
        }

        var history = previous.Select(x => new AiMessage(x.Role, x.Text)).ToList();
        while (history.Count > 0 && TokenEstimate.Count(messages.Concat(history).Append(userMessage)) > budget) history.RemoveAt(0);
        while (history.Count > 0 && history[0].Role == "assistant") history.RemoveAt(0);
        messages.AddRange(history);
        messages.Add(userMessage);

        return new(messages, hasMood);
    }

    public async Task<ConversationAugmentation> Enrich(
        ConversationContext context, ChatMessage current, AiTurnDraft draft, CancellationToken ct)
    {
        var messages = context.Messages.ToList();
        var sources = new List<SourceMetadata>();
        var additions = new List<string>();

        if (!string.IsNullOrWhiteSpace(draft.KnowledgeQuery))
        {
            try
            {
                var hits = await knowledge.Search(draft.KnowledgeQuery, ct);
                var sourceTokens = 0;
                foreach (var hit in hits)
                {
                    var data = $"{hit.Title}, PDF-сторінки {hit.PageStart}–{hit.PageEnd}:\n{hit.Text}";
                    var cost = TokenEstimate.Count(data);
                    if (sourceTokens + cost > 1200) continue;
                    additions.Add("Перевірений довідковий фрагмент із психологічної книжки. Використай лише факти/ідеї, які реально допомагають відповісти. " +
                                  "Не цитуй його як підручник і не змінюй голос співрозмовника на психолога:\n" + data);
                    sources.Add(new("book", hit.Title, hit.PageStart, hit.PageEnd, hit.ChunkId));
                    sourceTokens += cost;
                    if (sources.Count == 3) break;
                }
                if (sources.Count == 0)
                    additions.Add("Перевіреного книжкового фрагмента за цим запитом не знайдено. Не вигадуй психологічні факти або назви технік. " +
                                  "Дай просту людську відповідь із того, що вже є в розмові, і не переходь у тон консультанта.");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning("Knowledge retrieval unavailable: {Category}", e.GetType().Name);
                additions.Add("Перевірене книжкове джерело зараз недоступне. Не вигадуй психологічні факти. " +
                              "Відповідай по-дружньому тільки на основі самої розмови.");
            }
        }

        if (!string.IsNullOrWhiteSpace(draft.StoryQuery))
        {
            var material = await stories.Find(draft.StoryQuery, current.Id, ct);
            if (material.Count > 0)
            {
                additions.Add(
                    "Матеріал для історії з відкритого social-dialogue набору AllenAI SODA. Обери один сюжет як основу й природно " +
                    "переказуй українською так, ніби знайомому в чаті. Не говори, що це сталося з тобою, не додавай моралі та не згадуй " +
                    "внутрішній пошук. Можна змінювати несуттєві деталі для природності, але не вигадувати нову ключову подію:\n\n" +
                    string.Join("\n\n", material.Select((x, i) =>
                        $"Сюжет {i + 1} [{x.SourceId}]\nНаратив: {x.Narrative}\nДіалог: {x.Dialogue}")));
                sources.AddRange(material.Select(x => new SourceMetadata("story", x.Source, SourceId: x.SourceId)));
            }
            else
            {
                additions.Add("Матеріал для історії зараз не знайдено. Не вигадуй фальшиву «життєву історію» і не зависай на фразі «є одна». " +
                              "Коротко й природно скажи, що цього разу нормальної історії не підкинуло, або м'яко продовж розмову.");
            }
        }

        if (additions.Count == 0) return new(messages, sources);

        messages.Add(new AiMessage("system",
            "Нижче додатковий матеріал для останньої репліки. Він дає зміст, а не стиль. " +
            "Відповідь усе одно має звучати як та сама жива переписка з другом.\n\n" +
            string.Join("\n\n", additions)));

        return new(messages, sources);
    }

    public static string BuildMetadata(string conversationState, IReadOnlyList<SourceMetadata> sources)
        => JsonSerializer.Serialize(new
        {
            conversation_state = conversationState,
            sources = sources.Select(x => new
            {
                type = x.Type,
                title = x.Title,
                pageStart = x.PageStart,
                pageEnd = x.PageEnd,
                chunkId = x.ChunkId,
                sourceId = x.SourceId
            })
        });

    public static string ExtractConversationState(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return "";
        try
        {
            using var json = JsonDocument.Parse(metadata);
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                   json.RootElement.TryGetProperty("conversation_state", out var state)
                ? state.GetString()?.Trim() ?? ""
                : "";
        }
        catch (JsonException) { return ""; }
    }

    public static string RelevantExcerpt(string text, string query, int limit)
    {
        if (text.Length <= limit) return text;
        var terms = Lexicon.Terms(query).ToHashSet();
        var parts = text.Split(new[] { '\n', '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
        var chosen = parts.OrderByDescending(p => Lexicon.Terms(p).Count(terms.Contains)).FirstOrDefault() ?? text;
        if (chosen.Length <= limit) return chosen;
        return chosen[..(char.IsHighSurrogate(chosen[limit - 1]) ? limit - 1 : limit)];
    }

    public async Task Summarize(Guid userId, long version, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null || user.MemoryVersion != version) return;
        var count = await db.Messages.CountAsync(x => x.UserId == userId && x.Status == "done", ct);
        if (count <= 20) return;
        var older = await db.Messages.AsNoTracking().Where(x => x.UserId == userId && x.Status == "done")
            .OrderBy(x => x.ReplyToId ?? x.Id).ThenBy(x => x.Role == "assistant" ? 1 : 0).Take(Math.Min(count - 10, 40)).ToListAsync(ct);
        var old = await db.Summaries.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
        var facts = older.Where(x => x.Role == "user" && older.Any(a => a.ReplyToId == x.Id)).ToList();
        if (facts.Count == 0) return;
        var messages = new List<AiMessage> { new("system", uk.SummaryPrompt), new("user", "Попередня пам’ять (дані):\n" + old?.Text) };
        var included = new List<long>();
        foreach (var fact in facts)
        {
            var msg = new AiMessage("user", $"{fact.CreatedAt:u}: {fact.Text}");
            if (TokenEstimate.Count(messages.Append(msg)) + 600 > options.TokensPerMinute) break;
            messages.Add(msg); included.Add(fact.Id);
        }
        if (included.Count == 0) return;
        var result = await ai.Complete(messages, true, ct);
        var trimmed = result.Text;
        while (trimmed.Length > 0 && TokenEstimate.Count(trimmed) > 600) trimmed = trimmed[..^1];
        if (trimmed.Length > 0 && char.IsHighSurrogate(trimmed[^1])) trimmed = trimmed[..^1];
        using var guard = await locks.Lock(user.TelegramId, ct);
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var current = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, ct);
        if (current is null || current.MemoryVersion != version) return;
        var summary = await db.Summaries.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if ((summary?.CoveredThroughId ?? 0) != (old?.CoveredThroughId ?? 0)) return;
        if (summary is null) { summary = new() { UserId = userId }; db.Summaries.Add(summary); }
        summary.Text = trimmed; summary.CoveredThroughId = included.Max(); summary.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        await db.Messages.Where(x => x.UserId == userId && (included.Contains(x.Id) || x.ReplyToId != null && included.Contains(x.ReplyToId.Value))).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }
}
