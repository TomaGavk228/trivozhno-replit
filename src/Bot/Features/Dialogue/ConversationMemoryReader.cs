using Trivozhno.Host;
using Microsoft.EntityFrameworkCore;
using Trivozhno.Infrastructure.Persistence;

namespace Trivozhno.Features.Dialogue;

public sealed record MemorySnapshot(string Summary, IReadOnlyList<ChatMessage> History);
public interface IConversationMemoryReader
{
    Task<MemorySnapshot> Read(BotUser user, ChatMessage current, CancellationToken ct);
}
public sealed class ConversationMemoryReader(BotDb db, BotOptions options) : IConversationMemoryReader
{
    public async Task<MemorySnapshot> Read(BotUser user, ChatMessage current, CancellationToken ct)
    {
        var summary = await db.Summaries.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == user.Id, ct);
        var previous = await db.Messages.AsNoTracking().Where(x => x.UserId == user.Id && x.MemoryVersion == user.MemoryVersion &&
            (x.Role == "user" && x.Id < current.Id || x.Role == "assistant" && x.ReplyToId < current.Id) &&
            (x.Status == "done" || x.Status == "unanswered") && (user.MoodContextEnabled && options.Mood || !x.MoodDerived))
            .OrderByDescending(x => x.ReplyToId ?? x.Id).ThenByDescending(x => x.Role).Take(20).ToListAsync(ct);
        return new(summary?.Text ?? "", previous.OrderBy(x => x.ReplyToId ?? x.Id).ThenBy(x => x.Role == "assistant" ? 1 : 0).ToArray());
    }
}
