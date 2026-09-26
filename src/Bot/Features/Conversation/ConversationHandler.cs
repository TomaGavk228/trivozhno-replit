using Microsoft.EntityFrameworkCore;
using Trivozhno.Features.Navigation;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Infrastructure.Telegram;

namespace Trivozhno.Features.Conversation;

public sealed class ConversationHandler(BotDb db, Ui ui, IClock clock, BotOptions options)
{
    public void Open(BotUser u)
    {
        u.Go(UserState.ChatActive);
        var session = new ChatSession { UserId = u.Id, StartedAt = clock.UtcNow };
        db.Sessions.Add(session); u.SessionId = session.Id;
        ui.Say(u, "chat.invite", ui.Reply("chat.end"));
    }

    public async Task Receive(BotUser u, BotInput input, CancellationToken ct)
    {
        if (!u.AiNoticeShown) { u.AiNoticeShown = true; ui.Say(u, "chat.notice"); }

        var now = clock.UtcNow;
        var session = await db.Sessions.SingleAsync(x => x.Id == u.SessionId, ct);
        var recent = await db.Messages
            .Where(x => x.UserId == u.Id &&
                        x.SessionId == u.SessionId &&
                        x.MemoryVersion == u.MemoryVersion &&
                        x.Role == "user" &&
                        (x.Status == "queued" || x.Status == "processing" || x.Status == "done"))
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);
        // A generated answer does not count as a conversational turn until it
        // actually reaches Telegram. Include all user messages still unanswered.
        var unserved = recent is not null &&
            !await db.Messages.AnyAsync(x => x.ReplyToId == recent.Id && x.Role == "assistant" && x.Status == "done", ct);
        var firstAt = unserved ? recent!.TurnStartedAt ?? recent.CreatedAt : now;
        var turnText = unserved ? (recent!.TurnText ?? recent.Text).TrimEnd() + "\n" + input.Text!.TrimStart() : input.Text!;
        var quietUntil = now.AddMilliseconds(options.ChatQuietMilliseconds);
        var maxUntil = firstAt.AddMilliseconds(options.ChatGatherMilliseconds);
        if (unserved) recent!.Status = "superseded";
        session.Revision++;
        // Cancel only unsent AI messages. A message already sent cannot be recalled.
        await db.Outbox.Where(x => x.UserId == u.Id && x.SessionId == session.Id && x.Kind == "ai" && x.Status == "queued")
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, "cancelled")
                .SetProperty(y => y.Text, "").SetProperty(y => y.Markup, (string?)null)
                .SetProperty(y => y.Destination, (long?)null), ct);
        if (unserved)
            await db.Messages.Where(x => x.ReplyToId == recent!.Id && x.Role == "assistant" && x.Status == "pending_delivery")
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, "superseded"), ct);
        var pending = await db.Messages.CountAsync(x => x.UserId == u.Id && (x.Status == "queued" || x.Status == "processing"), ct);
        var overloaded = pending >= options.MaxUserAiQueue;
        var message = new ChatMessage
        {
            UserId = u.Id,
            SessionId = u.SessionId!.Value,
            MemoryVersion = u.MemoryVersion,
            Text = input.Text!,
            TurnText = turnText,
            TurnStartedAt = firstAt,
            ReadyAt = quietUntil < maxUntil ? quietUntil : maxUntil,
            TurnRevision = session.Revision,
            UpdateId = input.UpdateId,
            CreatedAt = now,
            Status = overloaded ? "unanswered" : "queued"
        };
        db.Messages.Add(message);

        if (overloaded) ui.Say(u, "chat.expired");
    }
}
