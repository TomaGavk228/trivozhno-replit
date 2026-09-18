using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Knowledge;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Resources;

namespace Trivozhno.Features.Memory;

public sealed record ConversationContext(IReadOnlyList<AiMessage> Messages, bool HasMood, string SourcesJson, TurnMode Mode);
public interface IConversationMemory
{
    Task<ConversationContext> Build(BotUser user, ChatMessage current, CancellationToken ct);
    Task Summarize(Guid userId, long version, CancellationToken ct);
}

public sealed class ConversationMemory(BotDb db, Uk uk, IKnowledgeRetriever knowledge, IAiClient ai, BotOptions options,
    IClock clock, UserLocks locks, ILogger<ConversationMemory> log) : IConversationMemory
{
    public async Task<ConversationContext> Build(BotUser user, ChatMessage current, CancellationToken ct)
    {
        const int chatOutputReserve = 700;
        var budget = Math.Min(options.InputBudget, options.TokensPerMinute - chatOutputReserve);

        var previous = await db.Messages.AsNoTracking().Where(x => x.UserId == user.Id &&
                (x.Role == "user" && x.Id < current.Id || x.Role == "assistant" && x.ReplyToId < current.Id) &&
                (x.Status == "done" || x.Status == "unanswered") && (user.MoodContextEnabled || !x.MoodDerived))
            .OrderByDescending(x => x.ReplyToId ?? x.Id).ThenByDescending(x => x.Role).Take(20).ToListAsync(ct);
        previous = previous.OrderBy(x => x.ReplyToId ?? x.Id).ThenBy(x => x.Role == "assistant" ? 1 : 0).ToList();

        var previousUserTexts = previous.Where(x => x.Role == "user").Select(x => x.Text).ToList();
        var mode = TurnRouter.Classify(current.Text, previousUserTexts);

        var core = new AiMessage("system", uk.ChatPrompt);
        var modeMessage = new AiMessage("system", TurnRouter.Instruction(mode));
        var userMessage = new AiMessage("user", current.Text);
        var required = new List<AiMessage> { core, modeMessage, userMessage };
        if (TokenEstimate.Count(required) > budget) throw new ContextTooLargeException();

        var messages = new List<AiMessage> { core, modeMessage };

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

        if (mode == TurnMode.Story && uk.StoryBank.Count > 0)
        {
            var storyQuery = TurnRouter.ResolveStoryQuery(current.Text, previousUserTexts);
            var stories = SelectStories(uk.StoryBank, storyQuery, current.Id, 3);
            if (stories.Count > 0)
            {
                var storyData =
                    "Готові життєві сюжети для цієї відповіді. Обери ОДИН і коротко переказуй його. " +
                    "Не вигадуй іншу історію, не додавай мораль, джерело, імена чи нові факти.\n\n" +
                    string.Join("\n\n", stories.Select((x, i) => $"Варіант {i + 1}: {x.Story}"));
                var storyMessage = new AiMessage("system", storyData);
                if (TokenEstimate.Count(messages.Append(storyMessage).Append(userMessage)) <= budget)
                    messages.Add(storyMessage);
            }
        }

        var selected = new List<KnowledgeHit>();
        if (TurnRouter.UsesBooks(mode))
        {
            var sources = new List<KnowledgeHit>();
            try
            {
                var query = current.Text;
                if (query.Length < 100 && Regex.IsMatch(query, @"\b(це|цього|цьому|він|вона|вони|його|її|знову|далі)\b", RegexOptions.IgnoreCase))
                    query += " " + previous.LastOrDefault(x => x.Role == "user")?.Text;
                if (Lexicon.Terms(query).Length > 0) sources.AddRange(await knowledge.Search(query, ct));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning("Knowledge retrieval unavailable: {Category}", e.GetType().Name);
            }

            var sourceTokens = 0;
            foreach (var hit in sources)
            {
                var data = $"Довідковий фрагмент для відповіді. {hit.Title}, PDF-сторінки {hit.PageStart}–{hit.PageEnd}:\n{hit.Text}";
                var cost = TokenEstimate.Count(data);
                if (sourceTokens + cost > 1200 || TokenEstimate.Count(messages.Append(new("system", data)).Append(userMessage)) > budget) continue;
                messages.Add(new("system", data));
                selected.Add(hit);
                sourceTokens += cost;
                if (selected.Count == 3) break;
            }
        }

        var history = previous.Select(x => new AiMessage(x.Role, x.Text)).ToList();
        while (history.Count > 0 && TokenEstimate.Count(messages.Concat(history).Append(userMessage)) > budget) history.RemoveAt(0);
        while (history.Count > 0 && history[0].Role == "assistant") history.RemoveAt(0);
        messages.AddRange(history);

        if (current.Text.Contains("звідки", StringComparison.OrdinalIgnoreCase) || current.Text.Contains("джерело", StringComparison.OrdinalIgnoreCase))
        {
            var provenance = previous.LastOrDefault(x => x.Role == "assistant")?.SourcesJson;
            if (provenance is { Length: > 2 })
            {
                var provenanceMessage = new AiMessage("system", "Метадані джерел попередньої відповіді, лише довідка: " + provenance);
                if (TokenEstimate.Count(messages.Append(provenanceMessage).Append(userMessage)) <= budget) messages.Add(provenanceMessage);
            }
        }

        messages.Add(userMessage);

        var provenanceItems = selected.Select(x => (object)new
        {
            type = "book",
            x.Title,
            x.PageStart,
            x.PageEnd,
            x.ChunkId
        }).ToList();

        return new(messages, hasMood, JsonSerializer.Serialize(provenanceItems), mode);
    }

    public static bool ShouldUseKnowledge(string text)
        => TurnRouter.Classify(text) is TurnMode.Advice or TurnMode.Info;

    public static bool ShouldUseStoryBank(string text)
        => TurnRouter.IsStoryRequest(text);

    public static bool ShouldContinueStory(string text, string? previousAssistant)
        => TurnRouter.IsContinuationPhrase(text) &&
           !string.IsNullOrWhiteSpace(previousAssistant) &&
           Regex.IsMatch(previousAssistant, @"\b(історі\w*|одн\w+ (кіт|людин|хлоп|дівчин|чоловік|жінк))\b",
               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<StorySeed> SelectStories(IReadOnlyList<StorySeed> stories, string text, long seed, int maxStories = 3)
    {
        if (stories.Count == 0 || maxStories <= 0) return [];

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var query = text.ToLowerInvariant();

        if (ContainsAny(query, "стосунк", "кохан", "побачен", "партнер", "хлопц", "дівчин"))
            wanted.UnionWith(["relationships"]);
        if (ContainsAny(query, "сміш", "прикол", "кумед", "крінж", "незруч"))
            wanted.UnionWith(["funny", "awkward"]);
        if (ContainsAny(query, "дивн", "збіг", "випадков"))
            wanted.UnionWith(["weird", "coincidence"]);
        if (ContainsAny(query, "мил", "тепл", "добру", "приємн"))
            wanted.UnionWith(["wholesome"]);
        if (ContainsAny(query, "кіт", "кот", "собак", "пес", "тварин"))
            wanted.UnionWith(["pets"]);
        if (ContainsAny(query, "подорож", "літак", "поїзд", "дороз"))
            wanted.UnionWith(["travel"]);
        if (ContainsAny(query, "сім'", "родин", "батьк", "дитин"))
            wanted.UnionWith(["family"]);
        if (ContainsAny(query, "робот", "офіс", "колег"))
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
