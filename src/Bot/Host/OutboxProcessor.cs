using Microsoft.EntityFrameworkCore;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Infrastructure.Telegram;

namespace Trivozhno.Host;

// Accessed only while OutboxProcessor's claim gate is held.
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
        chats[destination] = clock.UtcNow.AddMilliseconds(destination < 0 ? options.TelegramChannelMilliseconds : options.TelegramChatMilliseconds);
        if (chats.Count > 2048)
            foreach (var k in chats.Where(x => x.Value < clock.UtcNow).Select(x => x.Key).ToArray()) chats.Remove(k);
    }
}

public sealed class OutboxProcessor(IServiceScopeFactory scopes, UserLocks locks, IClock clock, TelegramRateGate rate, ILogger<OutboxProcessor> log)
{
    private readonly SemaphoreSlim claimGate = new(1, 1);
    private sealed record Delivery(OutboxMessage Item, long Destination, long LockId, DateTimeOffset? InputAt, DateTimeOffset? TurnAt);

    public async Task<bool> Step(CancellationToken ct)
    {
        Delivery? delivery;
        await claimGate.WaitAsync(ct);
        try { delivery = await Claim(ct); }
        finally { claimGate.Release(); }
        if (delivery is null) return false;
        if (delivery.Item.Status != "sending") return true;

        long? telegramId = null;
        TelegramFailure? failure = null;
        var unknown = false;
        // Neither user locks nor a database transaction span the HTTP call.
        using (var scope = scopes.CreateScope())
        {
            try
            {
                telegramId = await scope.ServiceProvider.GetRequiredService<ITelegramClient>()
                    .Send(delivery.Destination, delivery.Item.Text, delivery.Item.Markup, ct);
            }
            catch (TelegramFailure error) { failure = error; }
            catch (DeliveryUnknownException) { unknown = true; }
            catch (OperationCanceledException) { unknown = true; }
        }
        // Finish the ledger even during shutdown, with a bounded grace period.
        // If saving fails, maintenance expires the lease without resending.
        using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Finish(delivery, telegramId, failure, unknown, finish.Token);
        return true;
    }

    private async Task<Delivery?> Claim(CancellationToken ct)
    {
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<BotDb>();
        var item = await db.Outbox.AsNoTracking().Where(x => x.Status == "queued" && x.AvailableAt <= clock.UtcNow &&
                !db.Outbox.Any(y => y.Destination == x.Destination && y.Id < x.Id && (y.Status == "queued" || y.Status == "sending")) &&
                !db.Outbox.Any(y => y.OperationId == x.OperationId && y.PartIndex < x.PartIndex && y.Status != "sent"))
            .OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (item?.Destination is not { } destination) return null;
        var owner = item.UserId is { } uid ? await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == uid, ct) : null;
        var lockId = owner?.TelegramId ?? destination;
        if (!rate.Ready(destination, out var due))
        {
            await db.Outbox.Where(x => x.Id == item.Id && x.Status == "queued")
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.AvailableAt, due), ct);
            return new(item, destination, lockId, null, null);
        }
        using var guard = await locks.Lock(lockId, ct);
        var current = await db.Outbox.SingleOrDefaultAsync(x => x.Id == item.Id && x.Status == "queued", ct);
        if (current is null) return null;
        var user = item.UserId is { } userId ? await db.Users.SingleOrDefaultAsync(x => x.Id == userId, ct) : null;
        var valid = item.UserId is null || user is not null && !user.Blocked;
        if (current.Kind == "ai")
        {
            var revision = await db.Sessions.Where(x => x.Id == current.SessionId).Select(x => (long?)x.Revision).SingleOrDefaultAsync(ct);
            valid &= user is not null && user.SessionId == current.SessionId && user.MemoryVersion == current.MemoryVersion &&
                (current.TurnRevision is null || revision == current.TurnRevision);
        }
        if (current.Kind == "reminder") valid &= user?.State == UserState.MainMenu &&
            await db.Reminders.AnyAsync(x => x.UserId == user.Id && x.Enabled && x.Version == current.ReminderVersion, ct);
        if (!valid)
        {
            current.Status = "cancelled"; Scrub(current); await db.SaveChangesAsync(ct);
            return new(current, destination, lockId, null, null);
        }
        var input = current.Kind == "ai" ? await db.Messages.AsNoTracking().Where(x => x.Id == current.ReplyToId)
            .Select(x => new { x.CreatedAt, x.TurnStartedAt }).SingleOrDefaultAsync(ct) : null;
        current.Status = "sending"; current.Attempts++; current.LeaseUntil = clock.UtcNow.AddSeconds(90);
        await db.SaveChangesAsync(ct);
        rate.Used(destination);
        return new(current, destination, lockId, input?.CreatedAt, input?.TurnStartedAt ?? input?.CreatedAt);
    }

    private async Task Finish(Delivery delivery, long? telegramId, TelegramFailure? failure, bool unknown, CancellationToken ct)
    {
        using var guard = await locks.Lock(delivery.LockId, ct);
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<BotDb>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var current = await db.Outbox.SingleOrDefaultAsync(x => x.Id == delivery.Item.Id && x.Status == "sending", ct);
        // Full deletion may remove the ledger while a send is in flight. Never
        // recreate the user, memory, outbox or history after deletion.
        if (current is null || current.Attempts != delivery.Item.Attempts) return;
        var user = current.UserId is { } uid ? await db.Users.SingleOrDefaultAsync(x => x.Id == uid, ct) : null;
        current.LeaseUntil = null;
        if (telegramId is not null)
        {
            current.TelegramMessageId = telegramId; current.Status = "sent";
            if (user is not null && current.Kind == "ai" && current.ReplyToId is { } replyTo)
            {
                var answer = await db.Messages.SingleOrDefaultAsync(x => x.ReplyToId == replyTo && x.Role == "assistant", ct);
                if (answer is not null)
                {
                    answer.Text = answer.Text.Length == 0 ? delivery.Item.Text :
                        answer.Text + (current.Burst ? "\n" : "") + delivery.Item.Text;
                    var complete = !await db.Outbox.AnyAsync(x => x.OperationId == current.OperationId && x.Id != current.Id && x.Status != "sent", ct);
                    var interrupted = answer.Status == "interrupted" || user.SessionId != current.SessionId ||
                        !await db.Sessions.AnyAsync(x => x.Id == current.SessionId && x.Revision == current.TurnRevision, ct);
                    answer.Status = complete && !interrupted ? "done" : interrupted ? "interrupted" : "partial_delivery";
                }
                var session = await db.Sessions.SingleOrDefaultAsync(x => x.Id == current.SessionId, ct);
                if (session is not null) session.HasAnswer = true;
                log.LogInformation("AI delivery {Operation}; part {Part}; burst {Burst}; outbox wait {OutboxMs} ms; since latest input {LatestMs} ms; since turn start {TurnMs} ms",
                    current.OperationId, current.PartIndex, current.Burst, (clock.UtcNow - current.CreatedAt).TotalMilliseconds,
                    delivery.InputAt is { } at ? (clock.UtcNow - at).TotalMilliseconds : 0,
                    delivery.TurnAt is { } turn ? (clock.UtcNow - turn).TotalMilliseconds : 0);
            }
            if (user is not null && current.Kind == "reminder")
                await db.Occurrences.Where(x => x.UserId == user.Id && x.Version == current.ReminderVersion && x.Status == "queued")
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, "sent"), ct);
            Scrub(current);
        }
        else if (failure is not null)
        {
            current.ErrorCode = failure.Code.ToString();
            if (failure.Code == 429 && current.Attempts < 5)
            { current.Status = "queued"; current.AvailableAt = clock.UtcNow.AddSeconds(Math.Max(1, failure.RetryAfter)); }
            else
            {
                current.Status = "failed";
                if (failure.Code == 403 && delivery.Destination > 0 && user is not null)
                {
                    user.Blocked = true;
                    await db.Reminders.Where(x => x.UserId == user.Id)
                        .ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, false).SetProperty(y => y.Version, y => y.Version + 1), ct);
                }
                if (current.Kind != "confession") Scrub(current);
            }
        }
        else
        {
            current.Status = "delivery_unknown"; current.ErrorCode = unknown ? "ambiguous-send" : "missing-receipt";
            if (current.Kind != "confession") Scrub(current);
            log.LogWarning("Outbox {Operation} delivery unknown; no automatic resend", current.Id);
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    public static void Scrub(OutboxMessage item) { item.Text = ""; item.Markup = null; item.Destination = null; }
}
