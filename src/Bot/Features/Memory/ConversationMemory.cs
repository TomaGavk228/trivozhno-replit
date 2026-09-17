using Trivozhno.Features.Dialogue;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Knowledge;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Resources;

namespace Trivozhno.Features.Memory;

public sealed record ConversationContext(IReadOnlyList<AiMessage> Messages, bool HasMood, string SourcesJson)
{
    public ConversationState State { get; init; } = new();
    public int OutputTokens { get; init; } = 900;
}
public interface IConversationMemory
{
    Task<ConversationContext> Build(BotUser user, ChatMessage current, CancellationToken ct);
    Task Summarize(Guid userId, long version, CancellationToken ct);
}
public sealed class ConversationMemory(BotDb db, Uk uk, IConversationContextBuilder contextBuilder, IAiClient ai, BotOptions options,
    IClock clock, UserLocks locks) : IConversationMemory
{
    public Task<ConversationContext> Build(BotUser user, ChatMessage current, CancellationToken ct) =>
        contextBuilder.Build(user, current, ct);
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
        // Only user-authored facts are summarized. Mood-derived AI text cannot leak into memory.
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
