using Microsoft.EntityFrameworkCore;
using Trivozhno.Features.Navigation;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Infrastructure.Telegram;
using Trivozhno.Resources;

namespace Trivozhno.Features.Confessions;

public sealed class ConfessionHandler(BotDb db, Ui ui, DraftStore drafts, Uk uk, IClock clock, BotOptions options)
{
    public async Task Open(BotUser u, CancellationToken ct)
    {
        var existing = await db.Submissions.Where(x => x.UserId == u.Id && x.Status != "sent").OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            u.Go(UserState.ConfessionSending);
            ui.Say(u, "confession.pending", ui.Reply("exit"));
            if (existing.Status == "failed") ui.Say(u, "confession.failed", ui.Inline(u, ("conf:retry", "confession.retry")));
            if (existing.Status == "unknown") ui.Say(u, "confession.unknown");
            return;
        }
        await drafts.Begin(u, "confession", null, ct); u.Go(UserState.ConfessionDraft);
        ui.Say(u, "confession.invite", ui.Reply("done", "exit"));
    }
    public async Task Confirm(BotUser u, CancellationToken ct)
    {
        var d = await drafts.Get(u, ct);
        if (d is null || d.ReplaceOnNext || !await drafts.HasText(u, ct)) { ui.Say(u, "confession.empty"); return; }
        u.Go(UserState.ConfessionConfirm);
        ui.Say(u, "done", ui.Reply("exit"));
        ui.Say(u, "confession.confirm", ui.Inline(u, ("conf:send", "confession.send"), ("conf:edit", "confession.edit"), ("conf:cancel", "cancel")));
    }
    public async Task Edit(BotUser u, CancellationToken ct)
    {
        var d = await drafts.Get(u, ct) ?? throw new InvalidOperationException("Draft missing");
        d.ReplaceOnNext = true; u.Go(UserState.ConfessionDraft);
        ui.Say(u, "confession.edit.invite", ui.Reply("done", "exit"));
    }
    public async Task Send(BotUser u, CancellationToken ct)
    {
        var d = await drafts.Get(u, ct) ?? throw new InvalidOperationException("Draft missing");
        if (await db.Submissions.AnyAsync(x => x.DraftId == d.Id, ct)) { ui.Say(u, "confession.pending"); return; }
        var s = new ConfessionSubmission { UserId = u.Id, DraftId = d.Id, CreatedAt = clock.UtcNow };
        db.Submissions.Add(s); await db.SaveChangesAsync(ct); // identity inside router transaction
        var parts = TextSplitter.Split(await drafts.Read(d, ct), 3800);
        for (var i = 0; i < parts.Count; i++)
        {
            var header = uk.Format("confession.header", s.Id) + (parts.Count > 1 ? "\n" + uk.Format("confession.part", i + 1, parts.Count) : "");
            db.Outbox.Add(new() { UserId = u.Id, Destination = options.ChannelId, Kind = "confession", OperationId = s.OperationId,
                PartIndex = i, Text = header + "\n\n" + parts[i], CreatedAt = clock.UtcNow, AvailableAt = clock.UtcNow });
        }
        u.Go(UserState.ConfessionSending); ui.Say(u, "confession.pending", ui.Reply("exit"));
    }
    public async Task Retry(BotUser u, CancellationToken ct)
    {
        var s = await db.Submissions.Where(x => x.UserId == u.Id && x.Status == "failed").OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        if (s is null) { ui.Say(u, "confession.pending"); return; }
        if (await db.Outbox.AnyAsync(x => x.OperationId == s.OperationId && x.Status == "delivery_unknown", ct)) { ui.Say(u, "confession.unknown"); return; }
        await db.Outbox.Where(x => x.OperationId == s.OperationId && x.Status == "failed")
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, "queued").SetProperty(y => y.Attempts, 0).SetProperty(y => y.AvailableAt, clock.UtcNow), ct);
        s.Status = "sending"; ui.Say(u, "confession.pending");
    }
    // Called only once all part statuses are confirmed. A later UI action is never overwritten.
    public async Task Reconcile(Guid userId, CancellationToken ct)
    {
        var submissions = await db.Submissions.Where(x => x.UserId == userId && x.Status == "sending").OrderBy(x => x.Id).Take(100).ToListAsync(ct);
        foreach (var s in submissions)
        {
            var parts = await db.Outbox.Where(x => x.OperationId == s.OperationId).Select(x => x.Status).ToListAsync(ct);
            var u = await db.Users.SingleOrDefaultAsync(x => x.Id == s.UserId, ct); if (u is null || parts.Count == 0) continue;
            if (parts.All(x => x == "sent"))
            {
                s.Status = "sent"; ui.Say(u, "confession.sent");
                await db.Drafts.Where(x => x.Id == s.DraftId).ExecuteDeleteAsync(ct);
                if (u.State == UserState.ConfessionSending) { ui.Menu(u); ui.OfferReminder(u); }
            }
            else if (parts.Any(x => x == "delivery_unknown")) { s.Status = "unknown"; ui.Say(u, "confession.unknown"); }
            else if (parts.Any(x => x == "failed"))
            {
                s.Status = "failed";
                ui.Say(u, "confession.failed", u.State == UserState.ConfessionSending ? ui.Inline(u, ("conf:retry", "confession.retry")) : null);
            }
        }
    }
}
