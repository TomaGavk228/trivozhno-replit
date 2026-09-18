using Microsoft.EntityFrameworkCore;
using Trivozhno.Features.Navigation;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Infrastructure.Telegram;

namespace Trivozhno.Features.Conversation;

public sealed class ConversationHandler(BotDb db, Ui ui, IClock clock, BotOptions options)
{
    private static readonly TimeSpan MergeWindow = TimeSpan.FromMilliseconds(1500);

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
        var recentQueued = await db.Messages
            .Where(x => x.UserId == u.Id &&
                        x.SessionId == u.SessionId &&
                        x.MemoryVersion == u.MemoryVersion &&
                        x.Role == "user" &&
                        x.Status == "queued")
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);

        if (recentQueued is not null && now - recentQueued.CreatedAt <= MergeWindow)
        {
            recentQueued.Text = string.IsNullOrWhiteSpace(recentQueued.Text)
                ? input.Text!
                : recentQueued.Text.TrimEnd() + "\n" + input.Text!.TrimStart();
            recentQueued.CreatedAt = now;
            return;
        }

        var pending = await db.Messages.CountAsync(x => x.UserId == u.Id && (x.Status == "queued" || x.Status == "processing"), ct);
        var overloaded = pending >= options.MaxUserAiQueue;
        var message = new ChatMessage
        {
            UserId = u.Id,
            SessionId = u.SessionId!.Value,
            MemoryVersion = u.MemoryVersion,
            Text = input.Text!,
            UpdateId = input.UpdateId,
            CreatedAt = now,
            Status = overloaded ? "unanswered" : "queued"
        };
        db.Messages.Add(message);

        if (overloaded) ui.Say(u, "chat.expired");
    }
}
