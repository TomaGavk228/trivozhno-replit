using Microsoft.EntityFrameworkCore;
using Trivozhno.Features.Navigation;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Resources;

namespace Trivozhno.Features.Mood;

public sealed class MoodHandler(BotDb db, Ui ui, DraftStore drafts, Uk uk, IClock clock, BotOptions options)
{
    public void Open(BotUser u)
    {
        u.Go(UserState.MoodSelect);
        ui.Say(u, "mood.ask", ui.Inline(u, ("mood:1", "mood.1"), ("mood:2", "mood.2"), ("mood:3", "mood.3"),
            ("mood:4", "mood.4"), ("mood:5", "mood.5"), ("history:latest", "mood.history"), ("exit", "exit")));
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
