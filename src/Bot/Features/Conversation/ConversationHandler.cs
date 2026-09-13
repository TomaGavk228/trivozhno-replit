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
        var pending = await db.Messages.CountAsync(x => x.UserId == u.Id && (x.Status == "queued" || x.Status == "processing"), ct);
        var overloaded = pending >= options.MaxUserAiQueue;
        var message = new ChatMessage { UserId = u.Id, SessionId = u.SessionId!.Value, MemoryVersion = u.MemoryVersion,
            Text = input.Text!, UpdateId = input.UpdateId, CreatedAt = clock.UtcNow, Status = overloaded ? "unanswered" : "queued" };
        db.Messages.Add(message);
        if (overloaded) ui.Say(u, "chat.expired");
        else if (pending > 0 && !await db.Messages.AnyAsync(x => x.UserId == u.Id && x.Status == "queued" && x.QueueNoticeShown, ct))
        { message.QueueNoticeShown = true; ui.Say(u, "chat.queue"); }
    }
}
