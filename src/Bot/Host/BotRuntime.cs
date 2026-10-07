using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Trivozhno.Features.Confessions;
using Trivozhno.Features.Reminders;
using Trivozhno.Infrastructure.Ai;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Infrastructure.Telegram;

namespace Trivozhno.Host;

public sealed class WorkerStatus { public volatile bool Running; public DateTimeOffset Heartbeat { get; set; } }
public static class BotMetrics
{
    public static readonly Meter Meter = new("Trivozhno", "1.0");
    public static readonly Counter<long> Completed = Meter.CreateCounter<long>("bot.tasks.completed");
    public static readonly Counter<long> Errors = Meter.CreateCounter<long>("bot.tasks.errors");
}

public sealed class BotLease : IAsyncDisposable
{
    private readonly NpgsqlConnection connection;
    private BotLease(NpgsqlConnection c) { connection = c; }
    public static async Task<BotLease> Acquire(BotOptions options, CancellationToken ct, bool migration = false)
    {
        var c = new NpgsqlConnection(ConnectionStrings.Parse(options.Database)); await c.OpenAsync(ct);
        var key = migration ? 726813641L : BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(options.TelegramToken)), 0);
        await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", c); cmd.Parameters.AddWithValue("key", key);
        if (await cmd.ExecuteScalarAsync(ct) is not true) { await c.DisposeAsync(); throw new InvalidOperationException("Another instance is active. Stop it before starting this operation."); }
        return new(c);
    }
    public async Task Heartbeat(CancellationToken ct) { await using var command = new NpgsqlCommand("SELECT 1", connection); await command.ExecuteScalarAsync(ct); }
    public ValueTask DisposeAsync() => connection.DisposeAsync();
}

public sealed class Maintenance(IServiceScopeFactory scopes, UserLocks locks, IClock clock, BotOptions options)
{
    public async Task Recover(CancellationToken ct)
    {
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<BotDb>();
        await db.Outbox.Where(x => x.Status == "sending").ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, "delivery_unknown")
            .SetProperty(y => y.LeaseUntil, (DateTimeOffset?)null).SetProperty(y => y.ErrorCode, "restart-during-send"), ct);
        await db.Messages.Where(x => x.Status == "processing").ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, "queued").SetProperty(y => y.LeaseUntil, (DateTimeOffset?)null), ct);
    }
    public async Task Tick(CancellationToken ct)
    {
        using var scan = scopes.CreateScope(); var scanDb = scan.ServiceProvider.GetRequiredService<BotDb>();
        await scanDb.Outbox.Where(x => x.Status == "sending" && (x.LeaseUntil == null || x.LeaseUntil < clock.UtcNow))
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, "delivery_unknown").SetProperty(y => y.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(y => y.ErrorCode, "send-lease-expired"), ct);
        var userIds = await scanDb.Users.AsNoTracking().Where(u =>
                scanDb.Submissions.Any(s => s.UserId == u.Id && s.Status == "sending") ||
                options.Reminders && options.Conversation && scanDb.Reminders.Any(r => r.UserId == u.Id && r.Enabled && r.NextDueAt <= clock.UtcNow))
            .Select(u => new { u.Id, u.TelegramId }).Take(100).ToListAsync(ct);
        foreach (var item in userIds)
        {
            using var guard = await locks.Lock(item.TelegramId, ct); using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDb>(); await using var tx = await db.Database.BeginTransactionAsync(ct);
            var u = await db.Users.SingleOrDefaultAsync(x => x.Id == item.Id, ct); if (u is null) continue;
            await scope.ServiceProvider.GetRequiredService<ConfessionHandler>().Reconcile(u.Id, ct);
            if (options.Reminders && options.Conversation) await scope.ServiceProvider.GetRequiredService<ReminderHandler>().Schedule(u, ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        // Failed/unknown non-confession deliveries cannot block an operation forever.
        await scanDb.Outbox.Where(x => x.Kind != "confession" && x.Status == "queued" &&
                scanDb.Outbox.Any(y => y.OperationId == x.OperationId && (y.Status == "failed" || y.Status == "delivery_unknown" || y.Status == "cancelled")))
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, "cancelled").SetProperty(y => y.Text, "").SetProperty(y => y.Markup, (string?)null).SetProperty(y => y.Destination, (long?)null), ct);
        await scanDb.Outbox.Where(x => x.Kind != "confession" && x.Status == "delivery_unknown")
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Text, "").SetProperty(y => y.Markup, (string?)null).SetProperty(y => y.Destination, (long?)null), ct);
        await scanDb.Messages.Where(x => x.Role == "assistant" && (x.Status == "partial_delivery" || x.Status == "pending_delivery") &&
                scanDb.Outbox.Any(o => o.Kind == "ai" && o.ReplyToId == x.ReplyToId &&
                    (o.Status == "cancelled" || o.Status == "failed" || o.Status == "delivery_unknown")))
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, "interrupted"), ct);
        // Unowned delivery receipts expire after an hour, including their routing ID.
        await scanDb.Outbox.Where(x => x.UserId == null && x.CreatedAt < clock.UtcNow.AddHours(-1)).ExecuteDeleteAsync(ct);
        await scanDb.Inbox.Where(x => x.Status != "queued" && x.CreatedAt < clock.UtcNow.AddDays(-7)).ExecuteDeleteAsync(ct);
        await scanDb.Set<ApiUsage>().Where(x => x.At < clock.UtcNow.AddDays(-2)).ExecuteDeleteAsync(ct);
        await scanDb.Outbox.Where(x => x.Kind != "confession" && x.Status != "queued" && x.Status != "sending" && x.CreatedAt < clock.UtcNow.AddDays(-7)).ExecuteDeleteAsync(ct);
        await scanDb.Occurrences.Where(x => x.Status != "queued" && x.ScheduledAt < clock.UtcNow.AddDays(-30)).ExecuteDeleteAsync(ct);
        // A lost AI lease is recoverable without holding a database context across calls.
        await scanDb.Messages.Where(x => x.Status == "processing" && x.LeaseUntil < clock.UtcNow)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, "queued").SetProperty(y => y.LeaseUntil, (DateTimeOffset?)null), ct);
    }

}

public sealed class BotRuntime(IServiceScopeFactory scopes, InboxProcessor inbox, AiProcessor ai, OutboxProcessor outbox, Maintenance maintenance,
    BotOptions options, WorkerStatus status, IClock clock, IHostApplicationLifetime lifetime, ILogger<BotRuntime> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var lease = await BotLease.Acquire(options, stoppingToken);
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ITelegramClient>().Validate(stoppingToken);
            var db = scope.ServiceProvider.GetRequiredService<BotDb>();
            if ((await db.Database.GetPendingMigrationsAsync(stoppingToken)).Any()) throw new InvalidOperationException("Run migrate before starting workers.");
            await maintenance.Recover(stoppingToken); status.Running = true; status.Heartbeat = clock.UtcNow;
            var tasks = new List<Task>
            {
                Loop("poll", async ct =>
                {
                    var offset = await inbox.Offset(ct);
                    using var pollScope = scopes.CreateScope();
                    var batch = await pollScope.ServiceProvider.GetRequiredService<ITelegramClient>().Poll(offset, ct);
                    if (!await inbox.Store(batch, ct)) await Task.Delay(2000, ct);
                    return true;
                }, 100, stoppingToken),
                Loop("inbox", inbox.Step, 80, stoppingToken),
                Loop("maintenance", async ct => { await maintenance.Tick(ct); return false; }, 3000, stoppingToken),
                Heartbeat(lease, stoppingToken)
            };
            for (var i = 0; i < options.AiConcurrency; i++) tasks.Add(Loop("ai", ai.Step, 150, stoppingToken));
            for (var i = 0; i < 2; i++) tasks.Add(Loop("outbox", outbox.Step, 50, stoppingToken));
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception e) { Environment.ExitCode = 1; log.LogCritical("Worker startup/stopping failure: {Category}. Check configuration, database, migration and token ownership.", e.GetType().Name); lifetime.StopApplication(); }
        finally { status.Running = false; }
    }
    private async Task Heartbeat(BotLease lease, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await lease.Heartbeat(ct); status.Heartbeat = clock.UtcNow; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { status.Running = false; Environment.ExitCode = 1; lifetime.StopApplication(); return; }
            await Task.Delay(10000, ct);
        }
    }
    private async Task Loop(string kind, Func<CancellationToken, Task<bool>> action, int delay, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { if (!await action(ct)) await Task.Delay(delay, ct); else BotMetrics.Completed.Add(1, new KeyValuePair<string, object?>("kind", kind)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (TelegramFailure e) when (e.Code is 401 or 409)
            { status.Running = false; Environment.ExitCode = 1; log.LogCritical("Telegram polling conflict or invalid token, code {Code}", e.Code); lifetime.StopApplication(); return; }
            catch (Exception e)
            {
                BotMetrics.Errors.Add(1, new KeyValuePair<string, object?>("kind", kind));
                log.LogWarning("Worker {Worker}: {Category}", kind, e.GetType().Name); await Task.Delay(3000, ct);
            }
        }
    }
}
