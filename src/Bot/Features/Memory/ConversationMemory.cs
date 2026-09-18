using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Knowledge;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Resources;

namespace Trivozhno.Features.Memory;

public sealed record ConversationContext(IReadOnlyList<AiMessage> Messages, bool HasMood);
public sealed record SourceMetadata(string Type, string Title, int PageStart, int PageEnd, long ChunkId);
public sealed record ConversationAugmentation(IReadOnlyList<AiMessage> Messages, IReadOnlyList<SourceMetadata> Sources);

public interface IConversationMemory
{
    Task<ConversationContext> Build(BotUser user, ChatMessage current, CancellationToken ct);
    Task<ConversationAugmentation> Enrich(ConversationContext context, ChatMessage current, AiTurnDraft draft, CancellationToken ct);
    Task Summarize(Guid userId, long version, CancellationToken ct);
}

public sealed class ConversationMemory(BotDb db, Uk uk, IKnowledgeRetriever knowledge, IAiClient ai, BotOptions options,
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
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning("Knowledge retrieval unavailable: {Category}", e.GetType().Name);
            }
        }

        if (!string.IsNullOrWhiteSpace(draft.StoryQuery) && uk.StoryBank.Count > 0)
        {
            var stories = SelectStories(uk.StoryBank, draft.StoryQuery, current.Id, 3);
            if (stories.Count > 0)
            {
                additions.Add(
                    "Матеріал для історії. Обери один сюжет як основу й природно переказуй українською так, ніби знайомому в чаті. " +
                    "Не говори, що це сталося з тобою, не додавай моралі та не згадуй банк/джерело. Можна змінювати несуттєві деталі, " +
                    "але не вигадувати нову ключову подію:\n\n" +
                    string.Join("\n\n", stories.Select((x, i) => $"Сюжет {i + 1}: {x.Story}")));
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
                chunkId = x.ChunkId
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

    public static IReadOnlyList<StorySeed> SelectStories(IReadOnlyList<StorySeed> stories, string text, long seed, int maxStories = 3)
    {
        if (stories.Count == 0 || maxStories <= 0) return [];

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var query = text.ToLowerInvariant();

        if (ContainsAny(query, "стосунк", "кохан", "побачен", "партнер", "хлопц", "дівчин", "relationship", "date"))
            wanted.UnionWith(["relationships"]);
        if (ContainsAny(query, "сміш", "прикол", "кумед", "крінж", "незруч", "funny", "awkward"))
            wanted.UnionWith(["funny", "awkward"]);
        if (ContainsAny(query, "дивн", "збіг", "випадков", "weird", "coincidence"))
            wanted.UnionWith(["weird", "coincidence"]);
        if (ContainsAny(query, "мил", "тепл", "добру", "приємн", "wholesome"))
            wanted.UnionWith(["wholesome"]);
        if (ContainsAny(query, "кіт", "кот", "собак", "пес", "тварин", "pet"))
            wanted.UnionWith(["pets"]);
        if (ContainsAny(query, "подорож", "літак", "поїзд", "дороз", "travel"))
            wanted.UnionWith(["travel"]);
        if (ContainsAny(query, "сім'", "родин", "батьк", "дитин", "family"))
            wanted.UnionWith(["family"]);
        if (ContainsAny(query, "робот", "офіс", "колег", "work"))
            wanted.UnionWith(["work"]);

        var ranked = stories.Select((story, index) => new
        {
            Story = story,
            Index = index,
            Score = story.Tags.Count(wanted.Contains)
        }).ToArray();

        var topScore = ranked.Max(x => x.Score);
        var pool = (topScore > 0 ? ranked.Where(x => x.Score == topScore) : ranked).ToArray();
        var take = Math.Min(maxStories, pool.Length);
        var start = (int)(Math.Abs(seed % pool.Length));
        var result = new List<StorySeed>(take);
        for (var i = 0; i < take; i++) result.Add(pool[(start + i) % pool.Length].Story);
        return result;
    }

    private static bool ContainsAny(string text, params string[] parts)
        => parts.Any(part => text.Contains(part, StringComparison.OrdinalIgnoreCase));

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
