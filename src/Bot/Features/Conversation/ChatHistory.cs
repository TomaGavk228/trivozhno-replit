using Microsoft.EntityFrameworkCore;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Persistence;

namespace Trivozhno.Features.Conversation;

public sealed record ChatContext(IReadOnlyList<AiMessage> Messages, ChatConfigurationSnapshot Configuration);

public sealed class ChatHistory(BotDb db, ChatConfiguration configuration, BotOptions options, ILogger<ChatHistory> log)
{
    public async Task<ChatContext> Build(BotUser user, ChatMessage current, CancellationToken ct)
    {
        var snapshot = configuration.Read();
        var settings = snapshot.Generation;
        var limit = Math.Min(settings.InputTokenBudget,
            Math.Min(options.TokensPerMinute, options.TokensPerDay) - settings.MaxCompletionTokens - 128);
        var instruction = new AiMessage("system", snapshot.Instruction);
        var latest = new AiMessage("user", current.TurnText ?? current.Text);
        if (TokenEstimate.Count(new[] { instruction, latest }) > limit) throw new ContextTooLargeException();
        // Only literal messages from this session/version. An assistant message
        // becomes history after delivery, never while queued or superseded.
        var previous = await db.Messages.AsNoTracking().Where(x =>
                x.UserId == user.Id && x.SessionId == current.SessionId && x.MemoryVersion == current.MemoryVersion && !x.MoodDerived &&
                (x.Role == "user" && x.Id < current.Id && (x.Status == "done" || x.Status == "unanswered") ||
                 x.Role == "assistant" && x.ReplyToId < current.Id && x.Status == "done"))
            .OrderByDescending(x => x.ReplyToId ?? x.Id).ThenByDescending(x => x.Role)
            .Take(settings.HistoryTurns * 2 + 2).ToListAsync(ct);
        var groups = previous.GroupBy(x => x.ReplyToId ?? x.Id).OrderByDescending(x => x.Key)
            .Select(g => g.OrderBy(m => m.Role == "assistant" ? 1 : 0).ToArray())
            .Where(g => g[0].Role == "user").Take(settings.HistoryTurns);
        var history = new List<AiMessage>();
        foreach (var group in groups)
        {
            var candidate = group.Select(m => new AiMessage(m.Role, m.Role == "user" ? m.TurnText ?? m.Text : m.Text))
                .Concat(history).ToList();
            if (TokenEstimate.Count(candidate.Prepend(instruction).Append(latest)) > limit) break;
            history = candidate;
        }
        var messages = history.Prepend(instruction).Append(latest).ToArray();
        log.LogInformation("Chat baseline; turn {Turn}; SHA {Hash}; history messages {History}; examples {Examples}; estimated tokens {Tokens}; current preserved true",
            current.Id, snapshot.Hash, history.Count, snapshot.ExampleCount, TokenEstimate.Count(messages));
        return new(messages, snapshot);
    }
}
