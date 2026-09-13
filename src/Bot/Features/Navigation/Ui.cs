using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Infrastructure.Telegram;
using Trivozhno.Resources;

namespace Trivozhno.Features.Navigation;

public sealed class Ui(BotDb db, Uk uk, IClock clock, BotOptions options)
{
    public static string RemoveKeyboard => "{\"remove_keyboard\":true}";
    public string Reply(params string[] keys) => JsonSerializer.Serialize(new
    { keyboard = keys.Select(k => new[] { new { text = uk[k] } }), resize_keyboard = true, is_persistent = true });
    public string Inline(BotUser u, params (string code, string key)[] rows) => JsonSerializer.Serialize(new
    { inline_keyboard = rows.Select(r => new[] { new { text = uk[r.key], callback_data = $"{r.code}|{u.UiToken}" } }) });
    public string InlineText(BotUser u, params (string code, string text)[] rows) => JsonSerializer.Serialize(new
    { inline_keyboard = rows.Select(r => new[] { new { text = r.text, callback_data = $"{r.code}|{u.UiToken}" } }) });
    public void Say(BotUser u, string key, string? markup = null) => Text(u, uk[key], markup);
    public void Text(BotUser u, string text, string? markup = null, string kind = "ui", Guid? sessionId = null, long? memoryVersion = null, long? reminderVersion = null) =>
        Enqueue(u.Id, u.TelegramId, text, markup, kind, sessionId, memoryVersion, reminderVersion);
    public void Enqueue(Guid? user, long destination, string text, string? markup = null, string kind = "ui", Guid? sessionId = null,
        long? memoryVersion = null, long? reminderVersion = null, Guid? operation = null)
    {
        var parts = TextSplitter.Split(text); var op = operation ?? Guid.NewGuid();
        for (var i = 0; i < parts.Count; i++) db.Outbox.Add(new()
        {
            UserId = user, Destination = destination, Text = parts[i], Markup = i == parts.Count - 1 ? markup : null,
            Kind = kind, SessionId = sessionId, MemoryVersion = memoryVersion, ReminderVersion = reminderVersion,
            OperationId = op, PartIndex = i, CreatedAt = clock.UtcNow, AvailableAt = clock.UtcNow
        });
    }
    public void Menu(BotUser u, bool removeReply = true)
    {
        u.Go(UserState.MainMenu); u.PendingAction = "";
        if (removeReply) Say(u, "menu.return", RemoveKeyboard);
        var rows = new List<(string, string)>(); var descriptions = new List<string>();
        if (options.Conversation) { rows.Add(("menu:talk", "menu.talk")); descriptions.Add(uk["description.talk"]); }
        if (options.Confessions) { rows.Add(("menu:confession", "menu.confession")); descriptions.Add(uk["description.confession"]); }
        if (options.Mood) { rows.Add(("menu:mood", "menu.mood")); descriptions.Add(uk["description.mood"]); }
        rows.Add(("menu:settings", "menu.settings")); descriptions.Add(uk["description.settings"]);
        Text(u, uk.Format("welcome", string.Join('\n', descriptions)), Inline(u, rows.ToArray()));
    }
    public void OfferReminder(BotUser u)
    {
        if (!options.Reminders || !options.Conversation || u.ReminderPromptShown || u.State != UserState.MainMenu) return;
        u.ReminderPromptShown = true;
        Say(u, "reminder.offer", Inline(u, ("offer:yes", "reminder.configure"), ("offer:no", "reminder.no")));
    }
    public async Task CloseSession(BotUser u, CancellationToken ct)
    {
        if (u.SessionId is not { } id) return;
        var s = await db.Sessions.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (s is not null) s.EndedAt = clock.UtcNow;
        await db.Messages.Where(x => x.UserId == u.Id && x.SessionId == id && (x.Status == "queued" || x.Status == "processing"))
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, "cancelled").SetProperty(y => y.LeaseUntil, (DateTimeOffset?)null), ct);
        await db.Outbox.Where(x => x.UserId == u.Id && x.SessionId == id && x.Status == "queued").ExecuteDeleteAsync(ct);
        u.SessionId = null;
    }
}

public sealed class DraftStore(BotDb db, IClock clock, Ui ui)
{
    public async Task<Draft> Begin(BotUser u, string kind, int? mood, CancellationToken ct)
    {
        await Delete(u, ct);
        var d = new Draft { UserId = u.Id, Kind = kind, MoodValue = mood, CreatedAt = clock.UtcNow }; db.Drafts.Add(d); return d;
    }
    public Task<Draft?> Get(BotUser u, CancellationToken ct) => db.Drafts.SingleOrDefaultAsync(x => x.UserId == u.Id, ct);
    public Task<bool> HasText(BotUser u, CancellationToken ct) => db.DraftParts.AnyAsync(p => db.Drafts.Any(d => d.UserId == u.Id && d.Id == p.DraftId), ct);
    public async Task Delete(BotUser u, CancellationToken ct) => await db.Drafts.Where(x => x.UserId == u.Id).ExecuteDeleteAsync(ct);
    public async Task Append(BotUser u, string text, CancellationToken ct)
    {
        var d = await Get(u, ct) ?? throw new InvalidOperationException("Draft missing");
        if (d.ReplaceOnNext) { await db.DraftParts.Where(x => x.DraftId == d.Id).ExecuteDeleteAsync(ct); d.ReplaceOnNext = false; }
        if (await db.DraftParts.CountAsync(x => x.DraftId == d.Id, ct) >= 4096)
        { ui.Say(u, "draft.capacity"); return; }
        db.DraftParts.Add(new() { DraftId = d.Id, Text = text }); ui.Say(u, "draft.saved");
    }
    public async Task<string> Read(Draft d, CancellationToken ct) => string.Join("\n\n", await db.DraftParts.Where(x => x.DraftId == d.Id).OrderBy(x => x.Id).Select(x => x.Text).ToListAsync(ct));
}
