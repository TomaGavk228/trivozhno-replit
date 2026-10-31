using Microsoft.EntityFrameworkCore;
using Trivozhno.Features.Navigation;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Resources;

namespace Trivozhno.Features.Mood;

public sealed class MoodHandler(BotDb db, Ui ui, DraftStore drafts, Uk uk, IClock clock, BotOptions options,
    Trivozhno.Features.Plus.PlusHandler plus)
{
    private TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(options.Timezone);
    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, Zone).DateTime);
    private string Emoji(int value) => uk[$"mood.{value}"].Split(' ')[0];
    private string Emoji(double average) => Emoji((int)Math.Clamp(Math.Round(average, MidpointRounding.AwayFromZero), 1, 5));

    // Daily averages by local date for the last `days` days, today included.
    private async Task<Dictionary<DateOnly, (double Average, int Count)>> Daily(BotUser u, int days, CancellationToken ct)
    {
        var first = Today.AddDays(1 - days);
        var since = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(first.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), Zone));
        var rows = await db.Moods.AsNoTracking().Where(x => x.UserId == u.Id && x.RecordedAt >= since)
            .Select(x => new { x.RecordedAt, x.Value }).ToListAsync(ct);
        return rows.GroupBy(x => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.RecordedAt, Zone).DateTime))
            .ToDictionary(g => g.Key, g => (g.Average(x => x.Value), g.Count()));
    }

    public async Task<string> WeekLine(BotUser u, CancellationToken ct)
    {
        var daily = await Daily(u, 366, ct);
        var today = Today;
        var week = string.Concat(Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6))
            .Select(d => daily.TryGetValue(d, out var x) ? Emoji(x.Average) : "▫️"));
        var day = daily.ContainsKey(today) ? today : today.AddDays(-1);
        var streak = 0;
        while (daily.ContainsKey(day)) { streak++; day = day.AddDays(-1); }
        return uk.Format("mood.week", week) + "\n" + (streak > 0 ? uk.Format("mood.streak", streak) : uk["mood.streak.none"]);
    }

    public async Task Open(BotUser u, CancellationToken ct)
    {
        u.Go(UserState.MoodSelect);
        var summary = await WeekLine(u, ct);
        ui.Text(u, summary + "\n\n" + uk["mood.ask"], ui.Keyboard(u,
            [ui.B("mood.1", "mood:1")], [ui.B("mood.2", "mood:2")], [ui.B("mood.3", "mood:3")], [ui.B("mood.4", "mood:4")], [ui.B("mood.5", "mood:5")],
            [ui.B("mood.history", "history:latest"), ui.B("mood.month", "mood:month")], [ui.B("exit", "exit")]));
    }

    // «Друг+»: the last four weeks against the four weeks before. No AI.
    public async Task Month(BotUser u, CancellationToken ct)
    {
        if (!u.IsPlus(clock.UtcNow)) { ui.Say(u, "plus.only"); plus.Show(u); return; }
        var daily = await Daily(u, 56, ct);
        var today = Today;
        var lines = new List<string> { uk["month.title"] };
        for (var w = 3; w >= 0; w--)
        {
            var from = today.AddDays(-7 * w - 6); var to = today.AddDays(-7 * w);
            var days = daily.Where(x => x.Key >= from && x.Key <= to).ToArray();
            var count = days.Sum(x => x.Value.Count);
            var range = $"{from:dd.MM}–{to:dd.MM}";
            lines.Add(count == 0 ? uk.Format("month.week.empty", range)
                : uk.Format("month.week", range, Emoji(Weighted(days.Select(x => x.Value))), Weighted(days.Select(x => x.Value)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), count));
        }
        var current = daily.Where(x => x.Key > today.AddDays(-28)).ToArray();
        var previous = daily.Where(x => x.Key <= today.AddDays(-28)).ToArray();
        if (current.Length == 0) { ui.Text(u, string.Join('\n', lines) + "\n\n" + uk["month.empty"], ui.Keyboard(u, [ui.B("mood.history", "history:latest")], [ui.B("exit", "exit")])); return; }
        var best = current.OrderByDescending(x => x.Value.Average).ThenByDescending(x => x.Key).First();
        var hardest = current.OrderBy(x => x.Value.Average).ThenByDescending(x => x.Key).First();
        lines.Add("");
        lines.Add(uk.Format("month.best", best.Key.ToString("dd.MM"), Emoji(best.Value.Average)));
        if (hardest.Key != best.Key) lines.Add(uk.Format("month.hardest", hardest.Key.ToString("dd.MM"), Emoji(hardest.Value.Average)));
        var now = Weighted(current.Select(x => x.Value));
        if (previous.Length == 0) lines.Add(uk["month.compare.none"]);
        else
        {
            var before = Weighted(previous.Select(x => x.Value));
            var key = now - before >= 0.3 ? "month.compare.better" : before - now >= 0.3 ? "month.compare.worse" : "month.compare.same";
            string Show(double x) => Emoji(x) + " " + x.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            lines.Add(uk.Format(key, Show(now), Show(before)));
        }
        ui.Text(u, string.Join('\n', lines), ui.Keyboard(u, [ui.B("mood.history", "history:latest")], [ui.B("exit", "exit")]));
    }
    private static double Weighted(IEnumerable<(double Average, int Count)> days)
    {
        var list = days.ToArray();
        return list.Sum(x => x.Average * x.Count) / Math.Max(1, list.Sum(x => x.Count));
    }
    public async Task Select(BotUser u, int value, CancellationToken ct)
    {
        await drafts.Begin(u, "mood", value, ct); u.Go(UserState.MoodNote);
        ui.Say(u, "mood.note", ui.Reply("mood.save", "mood.skip", "exit"));
    }
    public async Task Save(BotUser u, bool skip, CancellationToken ct)
    {
        var d = await drafts.Get(u, ct);
        if (d?.MoodValue is not { } value) return;
        db.Moods.Add(new() { UserId = u.Id, Value = value, Note = skip ? null : await drafts.Read(d, ct), RecordedAt = clock.UtcNow });
        await drafts.Delete(u, ct);
        ui.Menu(u, message: uk["mood.saved"]);
        ui.OfferReminder(u);
    }
    public async Task History(BotUser u, string direction, CancellationToken ct)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.Timezone);
        if (direction == "latest" || u.State != UserState.MoodHistory)
            u.HistoryEndLocal = TimeZoneInfo.ConvertTime(clock.UtcNow, zone).Date.AddDays(1);
        else if (direction == "previous") u.HistoryEndLocal = u.HistoryEndLocal.AddDays(-7);
        var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(u.HistoryEndLocal, DateTimeKind.Unspecified), zone));
        var begin = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(u.HistoryEndLocal.AddDays(-7), DateTimeKind.Unspecified), zone));
        var query = db.Moods.AsNoTracking().Where(x => x.UserId == u.Id && x.RecordedAt >= begin && x.RecordedAt < end);
        List<MoodEntry> page;
        if (direction == "older" && u.HistoryLastTime is { } last && u.HistoryLastId is { } lastId)
            page = await query.Where(x => x.RecordedAt < last || x.RecordedAt == last && x.Id < lastId).OrderByDescending(x => x.RecordedAt).ThenByDescending(x => x.Id).Take(5).ToListAsync(ct);
        else if (direction == "newer" && u.HistoryCursorTime is { } first && u.HistoryCursorId is { } firstId)
        {
            page = await query.Where(x => x.RecordedAt > first || x.RecordedAt == first && x.Id > firstId).OrderBy(x => x.RecordedAt).ThenBy(x => x.Id).Take(5).ToListAsync(ct);
            page.Reverse();
        }
        else page = await query.OrderByDescending(x => x.RecordedAt).ThenByDescending(x => x.Id).Take(5).ToListAsync(ct);
        u.Go(UserState.MoodHistory);
        u.HistoryCursorTime = page.FirstOrDefault()?.RecordedAt; u.HistoryCursorId = page.FirstOrDefault()?.Id;
        u.HistoryLastTime = page.LastOrDefault()?.RecordedAt; u.HistoryLastId = page.LastOrDefault()?.Id;
        var rows = new List<(string, string)>();
        if (page.Count > 0)
        {
            var f = page[0]; var l = page[^1];
            if (await query.AnyAsync(x => x.RecordedAt > f.RecordedAt || x.RecordedAt == f.RecordedAt && x.Id > f.Id, ct)) rows.Add(("history:newer", "history.newer"));
            if (await query.AnyAsync(x => x.RecordedAt < l.RecordedAt || x.RecordedAt == l.RecordedAt && x.Id < l.Id, ct)) rows.Add(("history:older", "history.older"));
        }
        rows.Add(("history:previous", "history.previous")); rows.Add(("history:latest", "history.latest")); rows.Add(("exit", "exit"));
        ui.Text(u, uk.Format("history.period", u.HistoryEndLocal.AddDays(-7).ToString("dd.MM.yyyy"), u.HistoryEndLocal.AddDays(-1).ToString("dd.MM.yyyy")));
        if (page.Count == 0) ui.Say(u, "history.empty", ui.Inline(u, rows.ToArray()));
        for (var i = 0; i < page.Count; i++)
        {
            var item = page[i]; var local = TimeZoneInfo.ConvertTime(item.RecordedAt, zone);
            var content = $"{local:dd.MM.yyyy HH:mm} — {uk.MoodName(item.Value)}" + (string.IsNullOrEmpty(item.Note) ? "" : "\n\n" + item.Note);
            ui.Text(u, content, i == page.Count - 1 ? ui.Inline(u, rows.ToArray()) : null);
        }
    }
}
