using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Infrastructure.Telegram;
using Trivozhno.Resources;

namespace Trivozhno.Features.Navigation;

public sealed class Ui(BotDb db, Uk uk, IClock clock, BotOptions options, ActiveGenerations active)
{
    public static string RemoveKeyboard => "{\"remove_keyboard\":true}";
    public sealed record Button(string Text, string? Code = null, string? Url = null);
    // Only irreversible confirmations carry the one-time UiToken. Navigation
    // buttons stay usable after later transitions; Router checks the state.
    public static bool Guarded(string code) => code is "settings:clear:yes" or "settings:delete:yes" or "conf:send" or "discard:yes"
        || code.StartsWith("habit:d:", StringComparison.Ordinal);
    public static string Data(BotUser u, string code) => Guarded(code) ? $"{code}|{u.UiToken}" : code;
    public string Reply(params string[] keys) => ReplyRows(true, keys.Select(k => new[] { k }).ToArray());
    // Two short actions side by side and «Вийти» below: shorter than a column of three.
    public string ReplyPair(string first, string second, string exit) => ReplyRows(true, [first, second], [exit]);
    public string ReplyRows(bool persistent, params string[][] rows) => JsonSerializer.Serialize(new
    { keyboard = rows.Select(r => r.Select(k => new { text = uk[k] })), resize_keyboard = true, is_persistent = persistent });
    // The chat keyboard can be folded with Telegram's keyboard icon; the buttons stay available.
    public string ChatKeyboard() => ReplyRows(false, new[] { "chat.end" });
    // One wide main action on top, everything else in pairs below it.
    public string MainKeyboard()
    {
        var rest = new List<string>();
        if (options.Mood) rest.Add("menu.mood");
        if (options.Confessions) rest.Add("menu.confession");
        rest.Add("menu.plus");
        rest.Add("menu.settings");
        var rows = new List<string[]>();
        if (options.Conversation) rows.Add(["menu.talk"]);
        rows.AddRange(rest.Chunk(2));
        return ReplyRows(true, rows.ToArray());
    }
    public Button B(string key, string code) => new(uk[key], code);
    public Button Link(string key, string url) => new(uk[key], null, url);
    public string Keyboard(BotUser u, params Button[][] rows) => JsonSerializer.Serialize(new
    {
        inline_keyboard = rows.Where(r => r.Length > 0).Select(r => r.Select(b => b.Url is null
            ? (object)new { text = b.Text, callback_data = Data(u, b.Code!) } : new { text = b.Text, url = b.Url }))
    });
    public string Inline(BotUser u, params (string code, string key)[] rows) => JsonSerializer.Serialize(new
    { inline_keyboard = rows.Select(r => new[] { new { text = uk[r.key], callback_data = Data(u, r.code) } }) });
    public string InlineText(BotUser u, params (string code, string text)[] rows) => JsonSerializer.Serialize(new
    { inline_keyboard = rows.Select(r => new[] { new { text = r.text, callback_data = Data(u, r.code) } }) });
    // A Stars invoice goes through the same ordered outbox as ordinary messages.
    public void Invoice(BotUser u, string title, string description, string payload, int amount)
    {
        db.Outbox.Add(new()
        {
            UserId = u.Id, Destination = u.TelegramId, Kind = "invoice", OperationId = Guid.NewGuid(), PartIndex = 0,
            Text = JsonSerializer.Serialize(new { title, description, payload, currency = "XTR", prices = new[] { new { label = title, amount } } }),
            CreatedAt = clock.UtcNow, AvailableAt = clock.UtcNow
        });
    }
    public void Say(BotUser u, string key, string? markup = null) => Text(u, uk[key], markup);
    // Set by Router for a button press: the first screen of the answer replaces
    // that message in place (only text with inline buttons or none can be edited).
    public long? EditTarget { get; set; }
    public void Text(BotUser u, string text, string? markup = null, string kind = "ui", Guid? sessionId = null, long? memoryVersion = null, long? reminderVersion = null,
        long? turnRevision = null, long? replyToId = null)
    {
        var target = EditTarget; EditTarget = null;
        if (target is { } id && kind == "ui" && (markup is null || markup.StartsWith("{\"inline_keyboard\"", StringComparison.Ordinal)) &&
            TextSplitter.Split(text).Count == 1)
        {
            db.Outbox.Add(new()
            {
                UserId = u.Id, Destination = u.TelegramId, Kind = "edit", TelegramMessageId = id, Text = text, Markup = markup,
                OperationId = Guid.NewGuid(), PartIndex = 0, CreatedAt = clock.UtcNow, AvailableAt = clock.UtcNow
            });
            return;
        }
        Enqueue(u.Id, u.TelegramId, text, markup, kind, sessionId, memoryVersion, reminderVersion,
            turnRevision: turnRevision, replyToId: replyToId);
    }
    public void Enqueue(Guid? user, long destination, string text, string? markup = null, string kind = "ui", Guid? sessionId = null,
        long? memoryVersion = null, long? reminderVersion = null, Guid? operation = null, long? turnRevision = null, long? replyToId = null)
    {
        var parts = kind == "ai" ? TextSplitter.SplitChat(text) : TextSplitter.Split(text);
        var burst = kind == "ai" && TextSplitter.IsChatBurst(text);
        var op = operation ?? Guid.NewGuid();
        for (var i = 0; i < parts.Count; i++) db.Outbox.Add(new()
        {
            UserId = user, Destination = destination, Text = parts[i], Markup = i == parts.Count - 1 ? markup : null,
            Kind = kind, SessionId = sessionId, MemoryVersion = memoryVersion, ReminderVersion = reminderVersion,
            TurnRevision = turnRevision, ReplyToId = replyToId, Burst = burst,
            OperationId = op, PartIndex = i, CreatedAt = clock.UtcNow, AvailableAt = clock.UtcNow
        });
    }
    public void Menu(BotUser u, bool firstStart = false, string? message = null)
    {
        u.Go(UserState.MainMenu);
        u.PendingAction = "";
        var text = firstStart
            ? uk.Format("welcome", options.ChatDailyFree) + (options.TikTokUrl.Length > 0 ? "\n\n" + uk.Format("welcome.tiktok", options.TikTokUrl) : "")
            : message ?? uk["menu.title"];
        Text(u, text, MainKeyboard());
    }
    public void OfferReminder(BotUser u)
    {
        if (!options.Reminders || !options.Conversation || u.ReminderPromptShown || u.State != UserState.MainMenu) return;
        u.ReminderPromptShown = true;
        Say(u, "reminder.offer", Inline(u, ("offer:yes", "reminder.configure"), ("offer:no", "reminder.no")));
    }
    public async Task CloseSession(BotUser u, CancellationToken ct)
    {
        active.Cancel(u.Id);
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
        db.DraftParts.Add(new() { DraftId = d.Id, Text = text });
    }
    public async Task<string> Read(Draft d, CancellationToken ct) => string.Join("\n\n", await db.DraftParts.Where(x => x.DraftId == d.Id).OrderBy(x => x.Id).Select(x => x.Text).ToListAsync(ct));
}