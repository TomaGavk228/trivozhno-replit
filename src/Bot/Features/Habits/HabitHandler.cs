using Microsoft.EntityFrameworkCore;
using Trivozhno.Features.Navigation;
using Trivozhno.Features.Plus;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Resources;

namespace Trivozhno.Features.Habits;

// «Друг+»: up to five simple daily habits, marked with buttons. No AI.
public sealed class HabitHandler(BotDb db, Ui ui, Uk uk, IClock clock, ChatLimits limits, PlusHandler plus)
{
    public const int MaxHabits = 5;
    public const int MaxTitle = 40;

    private bool Allowed(BotUser u)
    {
        if (u.IsPlus(clock.UtcNow)) return true;
        ui.Say(u, "plus.only"); plus.Show(u); return false;
    }

    public async Task List(BotUser u, CancellationToken ct, string? notice = null)
    {
        if (!Allowed(u)) return;
        u.Go(UserState.Habits);
        var today = limits.Today();
        var habits = await db.Habits.AsNoTracking().Where(x => x.UserId == u.Id).OrderBy(x => x.Id).ToListAsync(ct);
        var ids = habits.Select(x => x.Id).ToArray();
        var since = today.AddDays(-60);
        var marks = (await db.HabitMarks.AsNoTracking().Where(x => ids.Contains(x.HabitId) && x.Day >= since).ToListAsync(ct))
            .GroupBy(x => x.HabitId).ToDictionary(g => g.Key, g => g.Select(x => x.Day).ToHashSet());
        var lines = new List<string>();
        var rows = new List<Ui.Button[]>();
        foreach (var h in habits)
        {
            var days = marks.GetValueOrDefault(h.Id) ?? [];
            var done = days.Contains(today);
            var week = Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6)).ToArray();
            var bar = string.Concat(week.Select(d => days.Contains(d) ? "🟩" : "⬜"));
            lines.Add($"{(done ? "✅" : "▫️")} {h.Title}\n{bar} {week.Count(days.Contains)}/7 · " + uk.Format("habit.streak", Streak(days, today)));
            rows.Add([new Ui.Button((done ? "✅ " : "⬜ ") + h.Title, $"habit:t:{h.Id}")]);
        }
        var tools = new List<Ui.Button>();
        if (habits.Count < MaxHabits) tools.Add(ui.B("habit.add", "habit:add"));
        if (habits.Count > 0) tools.Add(ui.B("habit.delete", "habit:del"));
        rows.Add(tools.ToArray());
        rows.Add([ui.B("back", "plus:show")]);
        var text = habits.Count == 0 ? uk.Format("habit.empty", MaxHabits) : uk["habit.title"] + "\n\n" + string.Join("\n\n", lines) + "\n\n" + uk["habit.hint"];
        if (notice is not null) text = notice + "\n\n" + text;
        ui.Text(u, text, ui.Keyboard(u, rows.ToArray()));
    }

    // Consecutive days with a mark, ending today (or yesterday while today is still open).
    public static int Streak(IReadOnlySet<DateOnly> days, DateOnly today)
    {
        var day = days.Contains(today) ? today : today.AddDays(-1);
        var count = 0;
        while (days.Contains(day)) { count++; day = day.AddDays(-1); }
        return count;
    }

    public async Task Toggle(BotUser u, long id, CancellationToken ct)
    {
        if (!Allowed(u)) return;
        if (!await db.Habits.AnyAsync(x => x.Id == id && x.UserId == u.Id, ct)) { await List(u, ct); return; }
        var today = limits.Today();
        var mark = await db.HabitMarks.SingleOrDefaultAsync(x => x.HabitId == id && x.Day == today, ct);
        if (mark is null) db.HabitMarks.Add(new() { HabitId = id, Day = today }); else db.HabitMarks.Remove(mark);
        await db.SaveChangesAsync(ct);
        await List(u, ct);
    }

    public async Task AskTitle(BotUser u, CancellationToken ct)
    {
        if (!Allowed(u)) return;
        if (await db.Habits.CountAsync(x => x.UserId == u.Id, ct) >= MaxHabits) { await List(u, ct, uk.Format("habit.full", MaxHabits)); return; }
        u.Go(UserState.HabitAdd);
        ui.Text(u, uk.Format("habit.ask", MaxTitle), ui.Keyboard(u, [ui.B("back", "habit:list")]));
    }

    public async Task Add(BotUser u, string text, CancellationToken ct)
    {
        if (!Allowed(u)) return;
        var title = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (title.Length is 0 or > MaxTitle) { ui.Text(u, uk.Format("habit.invalid", MaxTitle)); return; }
        if (await db.Habits.CountAsync(x => x.UserId == u.Id, ct) >= MaxHabits) { await List(u, ct, uk.Format("habit.full", MaxHabits)); return; }
        db.Habits.Add(new() { UserId = u.Id, Title = title, CreatedAt = clock.UtcNow });
        await db.SaveChangesAsync(ct);
        await List(u, ct, uk["habit.added"]);
    }

    public async Task PickDelete(BotUser u, CancellationToken ct)
    {
        if (!Allowed(u)) return;
        u.Go(UserState.Habits);
        var habits = await db.Habits.AsNoTracking().Where(x => x.UserId == u.Id).OrderBy(x => x.Id).ToListAsync(ct);
        var rows = habits.Select(h => new[] { new Ui.Button("🗑 " + h.Title, $"habit:d:{h.Id}") }).ToList();
        rows.Add([ui.B("back", "habit:list")]);
        ui.Say(u, "habit.delete.pick", ui.Keyboard(u, rows.ToArray()));
    }

    public async Task Delete(BotUser u, long id, CancellationToken ct)
    {
        await db.Habits.Where(x => x.Id == id && x.UserId == u.Id).ExecuteDeleteAsync(ct);
        await List(u, ct, uk["habit.deleted"]);
    }

    public Task<bool> Any(BotUser u, CancellationToken ct) => db.Habits.AnyAsync(x => x.UserId == u.Id, ct);
}
