using Microsoft.EntityFrameworkCore;
using Trivozhno.Features.Memory;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Persistence;

namespace Trivozhno.Features.Conversation;

public sealed record ChatContext(IReadOnlyList<AiMessage> Messages, ChatConfigurationSnapshot Configuration);

public sealed class ChatHistory(BotDb db, ChatConfiguration configuration, BotOptions options, ChatMemory memory, ILogger<ChatHistory> log)
{
    public async Task<ChatContext> Build(BotUser user, ChatMessage current, CancellationToken ct)
    {
        var snapshot = configuration.Read();
        var settings = snapshot.Generation;
        var limit = Math.Min(settings.InputTokenBudget,
                Math.Min(options.TokensPerMinute, options.TokensPerDay) - settings.MaxCompletionTokens - 128)
            - ChatReplyFormat.FormatTokens(settings.Model);
        var instruction = new AiMessage("system", snapshot.Instruction);
        var latest = new AiMessage("user", current.TurnText ?? current.Text);
        if (TokenEstimate.Count(new[] { instruction, latest }) > limit) throw new ContextTooLargeException();
        var available = limit - TokenEstimate.Count(new[] { instruction, latest });
        // Preserve most room for literal recent exchanges. Memory is optional
        // and can never cause a short current request to be rejected.
        var memoryReserve = Math.Min(settings.MemoryTokenBudget, available / 3);
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
        var included = new List<ChatMessage>();
        foreach (var group in groups)
        {
            var candidate = group.Select(m => new AiMessage(m.Role, m.Role == "user" ? m.TurnText ?? m.Text : m.Text))
                .Concat(history).ToList();
            var candidateTokens = TokenEstimate.Count(candidate.Prepend(instruction).Append(latest));
            if (candidateTokens > limit || history.Count > 0 && candidateTokens > limit - memoryReserve) break;
            history = candidate;
            included.AddRange(group);
            if (candidateTokens > limit - memoryReserve) break;
        }
        var remaining = limit - TokenEstimate.Count(history.Prepend(instruction).Append(latest));
        var remembered = await memory.Build(user, current, included,
            Math.Min(settings.MemoryTokenBudget, Math.Max(0, remaining - 16)), ct);
        var messages = new List<AiMessage> { instruction };
        if (remembered.Text.Length > 0)
        {
            var note = new AiMessage("system", remembered.Text);
            if (TokenEstimate.Count(history.Prepend(instruction).Append(note).Append(latest)) <= limit)
                messages.Add(note);
        }
        messages.AddRange(history);
        messages.Add(latest);
        log.LogInformation("Chat context; turn {Turn}; SHA {Hash}; history messages {History}; examples {Examples}; memory facts {Facts}; memory episodes {Episodes}; state {State}; estimated tokens {Tokens}; current preserved true",
            current.Id, snapshot.Hash, history.Count, snapshot.ExampleCount, remembered.Facts, remembered.Episodes, remembered.HasState,
            TokenEstimate.Count(messages) + ChatReplyFormat.FormatTokens(settings.Model));
        return new(messages, snapshot);
    }
}
