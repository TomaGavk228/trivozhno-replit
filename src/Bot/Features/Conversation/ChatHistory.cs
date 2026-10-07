using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Trivozhno.Features.Memory;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Ai;
using Trivozhno.Infrastructure.Knowledge;
using Trivozhno.Infrastructure.Persistence;

namespace Trivozhno.Features.Conversation;

public sealed record ChatContext(IReadOnlyList<AiMessage> Messages, ChatConfigurationSnapshot Configuration,
    IReadOnlyList<BookReference> References);

public sealed class ChatHistory(BotDb db, ChatConfiguration configuration, BotOptions options, ChatMemory memory,
    BookContext books, ILogger<ChatHistory> log)
{
    public async Task<ChatContext> Build(BotUser user, ChatMessage current, CancellationToken ct)
    {
        var snapshot = configuration.Read();
        var settings = snapshot.Generation;
        options.ValidateModel(settings.Model);
        var limit = settings.InputTokenBudget - ChatReplyFormat.FormatTokens(settings.Model);
        var instruction = new AiMessage("system", snapshot.Instruction);
        var latest = new AiMessage("user", current.TurnText ?? current.Text);
        var mandatory = TokenEstimate.Count(new[] { instruction, latest });
        if (mandatory > limit) throw new ContextTooLargeException();
        var available = limit - mandatory;
        var previous = await db.Messages.AsNoTracking().Where(x =>
                x.UserId == user.Id && x.SessionId == current.SessionId && x.MemoryVersion == current.MemoryVersion && !x.MoodDerived &&
                (x.Role == "user" && x.Id < current.Id && (x.Status == "done" || x.Status == "unanswered") ||
                 x.Role == "assistant" && x.ReplyToId < current.Id &&
                    (x.Status == "done" || x.Status == "partial_delivery" || x.Status == "interrupted")))
            .OrderByDescending(x => x.ReplyToId ?? x.Id).ThenByDescending(x => x.Role)
            .Take(settings.HistoryTurns * 2 + 2).ToListAsync(ct);
        var recent = previous.Where(x => x.Role == "user").OrderBy(x => x.Id)
            .TakeLast(8).Select(x => x.TurnText ?? x.Text).ToArray();
        var material = await books.Build(latest.Content, recent, Math.Min(4000, available / 3), ct);
        var bookCost = material.Text.Length == 0 ? 0 : TokenEstimate.Count(new[] { new AiMessage("system", material.Text) });
        if (bookCost > available) { material = new("", [], "budget_exhausted"); bookCost = 0; }
        var memoryReserve = Math.Min(settings.MemoryTokenBudget, Math.Max(0, (available - bookCost) / 3));
        var historyBudget = available - bookCost - memoryReserve;
        var groups = previous.GroupBy(x => x.ReplyToId ?? x.Id).OrderByDescending(x => x.Key)
            .Select(g => g.OrderBy(m => m.Role == "assistant" ? 1 : 0).ToArray())
            .Where(g => g[0].Role == "user").Take(settings.HistoryTurns);
        var history = new List<AiMessage>();
        var included = new List<ChatMessage>();
        foreach (var group in groups)
        {
            var turn = group.Select(m => new AiMessage(m.Role, m.Role == "user" ? m.TurnText ?? m.Text :
                JsonSerializer.Serialize(new ChatReply(m.Text, m.Status == "done" ? ChatMemory.ReadState(m.SourcesJson) : "", [], []), ChatReplyFormat.Json))).ToArray();
            var cost = TokenEstimate.Count(turn);
            if (cost > historyBudget) break;
            history.InsertRange(0, turn); included.AddRange(group); historyBudget -= cost;
        }
        var remembered = await memory.Build(user, current, included, Math.Min(settings.MemoryTokenBudget, memoryReserve + historyBudget), ct);
        var messages = new List<AiMessage> { instruction };
        if (material.Text.Length > 0) messages.Add(new("system", material.Text));
        if (remembered.Text.Length > 0) messages.Add(new("system", remembered.Text));
        messages.AddRange(history); messages.Add(latest);
        // Optional context never displaces the exact current message.
        while (TokenEstimate.Count(messages) > limit && messages.Count > 2) messages.RemoveAt(1);
        if (!messages.Any(x => x.Role == "system" && x.Content == material.Text)) material = material with { References = [] };
        log.LogInformation("Chat context; turn {Turn}; SHA {Hash}; history messages {History}; examples {Examples}; memory facts {Facts}; memory episodes {Episodes}; state {State}; books {Books}; book status {BookStatus}; estimated tokens {Tokens}; current preserved true",
            current.Id, snapshot.Hash, history.Count, snapshot.ExampleCount, remembered.Facts, remembered.Episodes, remembered.HasState,
            material.References.Count, material.Status, TokenEstimate.Count(messages) + ChatReplyFormat.FormatTokens(settings.Model));
        var references = material.References.Concat(messages.Any(x => x.Role == "system" && x.Content == remembered.Text)
                ? remembered.Sources ?? [] : [])
            .DistinctBy(x => x.Id).ToArray();
        return new(messages, snapshot, references);
    }
}
