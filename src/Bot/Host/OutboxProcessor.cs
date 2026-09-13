using Microsoft.EntityFrameworkCore;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Infrastructure.Telegram;

namespace Trivozhno.Host;

public sealed class TelegramRateGate(IClock clock, BotOptions options)
{
    private DateTimeOffset next;
    private readonly Dictionary<long, DateTimeOffset> chats = [];
    public bool Ready(long destination, out DateTimeOffset due)
    {
        var chatDue = chats.GetValueOrDefault(destination);
        due = chatDue > next ? chatDue : next; return due <= clock.UtcNow;
    }
    public void Used(long destination)
    {
        next = clock.UtcNow.AddMilliseconds(1000d / options.TelegramPerSecond);
        // Channels count towards a more conservative 20 messages/minute.
        chats[destination] = clock.UtcNow.AddMilliseconds(destination < 0 ? options.TelegramChannelMilliseconds : options.TelegramChatMilliseconds);
        if (chats.Count > 2048) foreach (var k in chats.Where(x => x.Value < clock.UtcNow).Select(x => x.Key).ToArray()) chats.Remove(k);
    }
}

public sealed class OutboxProcessor(IServiceScopeFactory scopes, UserLocks locks, IClock clock, TelegramRateGate rate, ILogger<OutboxProcessor> log)
{
    public async Task<bool> Step(CancellationToken ct)
    {
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<BotDb>();
        var item = await db.Outbox.AsNoTracking().Where(x => x.Status == "queued" && x.AvailableAt <= clock.UtcNow &&
                !db.Outbox.Any(y => y.Destination == x.Destination && y.Id < x.Id && (y.Status == "queued" || y.Status == "sending")) &&
                !db.Outbox.Any(y => y.OperationId == x.OperationId && y.PartIndex < x.PartIndex && y.Status != "sent"))
            .OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (item?.Destination is not { } destination) return false;
        if (!rate.Ready(destination, out var due))
        { await db.Outbox.Where(x => x.Id == item.Id && x.Status == "queued").ExecuteUpdateAsync(x => x.SetProperty(y => y.AvailableAt, due), ct); return true; }
        var owner = item.UserId is { } uid ? await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == uid, ct) : null;
        using var guard = await locks.Lock(owner?.TelegramId ?? destination, ct);
        var current = await db.Outbox.SingleOrDefaultAsync(x => x.Id == item.Id && x.Status == "queued", ct);
        if (current is null) return true;
        var u = item.UserId is { } userId ? await db.Users.SingleOrDefaultAsync(x => x.Id == userId, ct) : null;
        var valid = item.UserId is null || u is not null && !u.Blocked;
        if (current.Kind == "ai") valid &= u is not null && u.SessionId == current.SessionId && u.MemoryVersion == current.MemoryVersion;
        if (current.Kind == "reminder") valid &= u?.State == UserState.MainMenu && await db.Reminders.AnyAsync(x => x.UserId == u.Id && x.Enabled && x.Version == current.ReminderVersion, ct);
        if (!valid)
        { current.Status = "cancelled"; Scrub(current); await db.SaveChangesAsync(ct); return true; }
        current.Status = "sending"; current.Attempts++; await db.SaveChangesAsync(ct);
        rate.Used(destination);
        try
        {
            current.TelegramMessageId = await scope.ServiceProvider.GetRequiredService<ITelegramClient>().Send(destination, current.Text, current.Markup, ct);
            current.Status = "sent"; Scrub(current);
            if (u is not null && current.Kind == "ai" && !await db.Outbox.AnyAsync(x => x.OperationId == current.OperationId && x.Id != current.Id && x.Status != "sent", ct))
            {
                var session = await db.Sessions.SingleOrDefaultAsync(x => x.Id == current.SessionId, ct);
                if (session is not null) session.HasAnswer = true;
            }
            if (u is not null && current.Kind == "reminder")
                await db.Occurrences.Where(x => x.UserId == u.Id && x.Version == current.ReminderVersion && x.Status == "queued").ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, "sent"), ct);
        }
        catch (TelegramFailure e)
        {
            current.ErrorCode = e.Code.ToString();
            if (e.Code == 429 && current.Attempts < 5)
            { current.Status = "queued"; current.AvailableAt = clock.UtcNow.AddSeconds(Math.Max(1, e.RetryAfter)); }
            else
            {
                current.Status = "failed";
                if (e.Code == 403 && destination > 0 && u is not null)
                {
                    u.Blocked = true;
                    await db.Reminders.Where(x => x.UserId == u.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, false).SetProperty(y => y.Version, y => y.Version + 1), ct);
                }
                if (current.Kind != "confession") Scrub(current);
            }
        }
        catch (DeliveryUnknownException)
        {
            current.Status = "delivery_unknown"; current.ErrorCode = "ambiguous-send";
            if (current.Kind != "confession") Scrub(current);
            log.LogWarning("Outbox {Operation} delivery unknown; manual review required", current.Id);
        }
        // If commit fails after Telegram accepted the send, 'sending' remains durable;
        // recovery changes it to delivery_unknown rather than replaying blindly.
        await db.SaveChangesAsync(CancellationToken.None);
        return true;
    }
    public static void Scrub(OutboxMessage x) { x.Text = ""; x.Markup = null; x.Destination = null; }
}
