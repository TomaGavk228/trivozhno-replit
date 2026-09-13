using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Trivozhno.Features.Navigation;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Infrastructure.Telegram;

namespace Trivozhno.Host;

public sealed class InboxProcessor(IServiceScopeFactory scopes, UserLocks locks, IClock clock, BotOptions options, ILogger<InboxProcessor> log)
{
    public async Task<long> Offset(CancellationToken ct)
    {
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<BotDb>();
        return (await db.Checkpoints.SingleOrDefaultAsync(x => x.Id == 1, ct))?.Offset ?? 0;
    }
    public async Task<bool> Store(PollBatch batch, CancellationToken ct)
    {
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<BotDb>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.Inbox.CountAsync(x => x.Status == "queued", ct) + batch.Inputs.Count > options.MaxQueue) return false;
        var ids = batch.Inputs.Select(x => x.UpdateId).ToArray();
        var existing = (await db.Inbox.Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        foreach (var input in batch.Inputs.Where(x => !existing.Contains(x.UpdateId)))
            db.Inbox.Add(new() { Id = input.UpdateId, TelegramId = input.TelegramId, Payload = JsonSerializer.Serialize(input), CreatedAt = clock.UtcNow, AvailableAt = clock.UtcNow });
        var checkpoint = await db.Checkpoints.SingleOrDefaultAsync(x => x.Id == 1, ct);
        if (checkpoint is null) { checkpoint = new(); db.Checkpoints.Add(checkpoint); }
        checkpoint.Offset = Math.Max(checkpoint.Offset, batch.NextOffset);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true;
    }
    public async Task<bool> Step(CancellationToken ct)
    {
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<BotDb>();
        var item = await db.Inbox.AsNoTracking().Where(x => x.Status == "queued" && x.AvailableAt <= clock.UtcNow &&
                !db.Inbox.Any(y => y.TelegramId == x.TelegramId && y.Id < x.Id && y.Status == "queued"))
            .OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (item?.TelegramId is not { } telegram) return false;
        using var guard = await locks.Lock(telegram, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var current = await db.Inbox.SingleOrDefaultAsync(x => x.Id == item.Id && x.Status == "queued", ct);
            if (current is null) return true;
            var input = JsonSerializer.Deserialize<BotInput>(current.Payload) ?? throw new JsonException();
            await scope.ServiceProvider.GetRequiredService<Router>().Handle(input, ct);
            await db.SaveChangesAsync(ct);
            // Also works after full deletion removed the current inbox row.
            await db.Inbox.Where(x => x.Id == item.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, "done")
                .SetProperty(y => y.Payload, "").SetProperty(y => y.TelegramId, (long?)null), ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
            var current = await db.Inbox.SingleOrDefaultAsync(x => x.Id == item.Id, ct);
            if (current is not null)
            {
                current.Attempts++; current.AvailableAt = clock.UtcNow.AddSeconds(Math.Pow(2, current.Attempts));
                if (current.Attempts >= 3)
                {
                    current.Status = "failed"; current.Payload = ""; current.TelegramId = null;
                    var u = await db.Users.SingleOrDefaultAsync(x => x.TelegramId == telegram, ct);
                    if (u is not null) scope.ServiceProvider.GetRequiredService<Ui>().Say(u, "service.error");
                }
                await db.SaveChangesAsync(ct);
            }
            log.LogWarning("Inbox {Operation} failed: {Category}", item.Id, e.GetType().Name);
        }
        return true;
    }
}
