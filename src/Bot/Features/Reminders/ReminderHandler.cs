using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Trivozhno.Features.Navigation;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Resources;

namespace Trivozhno.Features.Reminders;

public static class ReminderDates
{
    public static DateTimeOffset Resolve(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Min() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
    public static DateTimeOffset First(DateTimeOffset now, string time, string timezone)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone); var t = TimeOnly.ParseExact(time, "HH:mm", CultureInfo.InvariantCulture);
        var day = TimeZoneInfo.ConvertTime(now, zone).Date; var due = Resolve(day.Add(t.ToTimeSpan()), zone);
        return due > now ? due : Resolve(day.AddDays(1).Add(t.ToTimeSpan()), zone);
    }
    public static DateTimeOffset Next(DateTimeOffset previous, DateTimeOffset now, int interval, string time, string timezone)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone); var t = TimeOnly.ParseExact(time, "HH:mm", CultureInfo.InvariantCulture);
        var day = TimeZoneInfo.ConvertTime(previous, zone).Date;
        DateTimeOffset next;
        do { day = day.AddDays(interval); next = Resolve(day.Add(t.ToTimeSpan()), zone); } while (next <= now);
        return next;
    }
}

public sealed class ReminderHandler(BotDb db, Ui ui, Uk uk, IClock clock, BotOptions options)
{
    public async Task Show(BotUser u, CancellationToken ct)
    {
        u.Go(UserState.Settings); u.PendingAction = "reminders";
        var r = await db.Reminders.SingleOrDefaultAsync(x => x.UserId == u.Id, ct);
        var on = r?.Enabled == true;
        var text = on ? uk.Format("reminder.current", r!.IntervalDays, r.LocalTime) : uk["reminder.off"];
        var rows = new List<Ui.Button[]> { new[] { ui.B("reminder.change", "rem:change") },
            new[] { on ? ui.B("reminder.disable", "rem:disable") : ui.B("reminder.enable", "rem:enable") } };
        if (on && u.IsPlus(clock.UtcNow))
        {
            text += "\n" + uk[r!.Personal ? "reminder.personal.on" : "reminder.personal.off"];
            rows.Add([ui.B(r.Personal ? "reminder.personal.disable" : "reminder.personal.enable", r.Personal ? "rem:personal:off" : "rem:personal:on")]);
        }
        rows.Add([ui.B("back", "settings:show")]);
        ui.Text(u, text, ui.Keyboard(u, rows.ToArray()));
    }
    public async Task Personal(BotUser u, bool enable, CancellationToken ct)
    {
        var r = await db.Reminders.SingleOrDefaultAsync(x => x.UserId == u.Id, ct);
        if (r is not null && (u.IsPlus(clock.UtcNow) || !enable)) { r.Personal = enable; r.Version++; await CancelPending(u, ct); }
        await db.SaveChangesAsync(ct); await Show(u, ct);
    }
    public void Frequency(BotUser u)
    {
        u.Go(UserState.ReminderFrequency); u.PendingFrequency = 1; u.CustomTime = false;
        ui.Say(u, "reminder.frequency", ui.Inline(u, ("rem:freq:1", "reminder.1"), ("rem:freq:2", "reminder.2"), ("rem:freq:3", "reminder.3"), ("exit", "exit")));
    }
    public void Time(BotUser u, int frequency)
    {
        u.PendingFrequency = frequency; u.Go(UserState.ReminderTime); u.CustomTime = false;
        ui.Say(u, "reminder.time", ui.InlineText(u, ("rem:time:09:00", "09:00"), ("rem:time:15:00", "15:00"),
            ("rem:time:20:00", "20:00"), ("rem:custom", uk["reminder.custom"]), ("exit", uk["exit"])));
    }
    public void Custom(BotUser u) { u.CustomTime = true; u.Go(UserState.ReminderTime); ui.Say(u, "reminder.time.input", ui.Reply("exit")); }
    public async Task Save(BotUser u, string time, CancellationToken ct)
    {
        if (!TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) { ui.Say(u, "reminder.time.invalid"); return; }
        var r = await db.Reminders.SingleOrDefaultAsync(x => x.UserId == u.Id, ct);
        if (r is null) { r = new() { UserId = u.Id }; db.Reminders.Add(r); }
        r.IntervalDays = u.PendingFrequency; r.LocalTime = time; r.Timezone = options.Timezone; r.Enabled = true; r.Version++;
        r.NextDueAt = ReminderDates.First(clock.UtcNow, time, r.Timezone); u.ReminderPromptShown = true;
                await CancelPending(u, ct);
        ui.Menu(u, message: uk.Format("reminder.saved", r.IntervalDays, time));
    }
    public async Task Toggle(BotUser u, bool enable, CancellationToken ct)
    {
        var r = await db.Reminders.SingleOrDefaultAsync(x => x.UserId == u.Id, ct);
        if (r is null && enable) { Frequency(u); return; }
        if (r is not null)
        {
            r.Enabled = enable; r.Version++; u.ReminderPromptShown = true;
            if (enable) r.NextDueAt = ReminderDates.First(clock.UtcNow, r.LocalTime, r.Timezone);
            await CancelPending(u, ct);
        }
        await db.SaveChangesAsync(ct); await Show(u, ct);
    }
    public async Task CancelPending(BotUser u, CancellationToken ct)
    {
        await db.Outbox.Where(x => x.UserId == u.Id && x.Kind == "reminder" && x.Status == "queued").ExecuteDeleteAsync(ct);
        await db.Occurrences.Where(x => x.UserId == u.Id && (x.Status == "queued" || x.Status == "compose"))
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, "cancelled"), ct);
    }
    public async Task Schedule(BotUser u, CancellationToken ct)
    {
        var r = await db.Reminders.SingleOrDefaultAsync(x => x.UserId == u.Id, ct);
        if (r is null || !r.Enabled || r.NextDueAt > clock.UtcNow) return;
        var due = r.NextDueAt;
        if (!await db.Occurrences.AnyAsync(x => x.UserId == u.Id && x.ScheduledAt == due, ct))
        {
            var send = !u.Blocked && u.State == UserState.MainMenu && clock.UtcNow - due <= TimeSpan.FromHours(1);
            // «Друг пише першим»: PersonalReminderComposer writes the text outside the lock.
            var personal = send && r.Personal && u.IsPlus(clock.UtcNow);
            db.Occurrences.Add(new() { UserId = u.Id, ScheduledAt = due, Version = r.Version, Status = personal ? "compose" : send ? "queued" : "skipped" });
            if (send && !personal) await Send(u, uk["reminder.message"], r.Version, ct);
        }
        r.NextDueAt = ReminderDates.Next(due, clock.UtcNow, r.IntervalDays, r.LocalTime, r.Timezone);
    }
    public async Task Send(BotUser u, string text, long version, CancellationToken ct)
    {
        Ui.Button[] habits = u.IsPlus(clock.UtcNow) && await db.Habits.AnyAsync(x => x.UserId == u.Id, ct) ? [ui.B("reminder.habits", "habit:list")] : [];
        ui.Text(u, text, ui.Keyboard(u, [ui.B("reminder.start", "rem:start")], habits), "reminder", reminderVersion: version);
    }
}
