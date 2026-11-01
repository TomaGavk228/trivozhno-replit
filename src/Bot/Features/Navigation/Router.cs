using Microsoft.EntityFrameworkCore;
using Trivozhno.Features.Confessions;
using Trivozhno.Features.Conversation;
using Trivozhno.Features.Friend;
using Trivozhno.Features.Habits;
using Trivozhno.Features.Mood;
using Trivozhno.Features.Plus;
using Trivozhno.Features.Reminders;
using Trivozhno.Features.Settings;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Infrastructure.Telegram;
using Trivozhno.Resources;

namespace Trivozhno.Features.Navigation;

// All calls occur inside one per-user lock and one database transaction. No HTTP here.
public sealed class Router(BotDb db, Ui ui, DraftStore drafts, Uk uk, IClock clock, BotOptions options,
    ConversationHandler chat, ConfessionHandler confessions, MoodHandler mood, ReminderHandler reminders, SettingsHandler settings,
    PlusHandler plus, HabitHandler habits, FriendHandler friend)
{
    // Screens where nothing unsaved is in progress: navigation may leave them freely.
    private static bool Idle(UserState s) => s is UserState.MainMenu or UserState.Settings or UserState.MoodSelect or UserState.MoodHistory
        or UserState.ReminderFrequency or UserState.ReminderTime or UserState.ConfirmClearMemory or UserState.ConfirmDeleteData
        or UserState.Habits or UserState.HabitAdd or UserState.FriendSetup or UserState.FriendName;

    public async Task Handle(BotInput input, CancellationToken ct)
    {
        var command = input.Text is { } t && t.StartsWith('/') ? t.Split(' ', 2)[0].Split('@', 2)[0].ToLowerInvariant() : null;
        if (command == "/source")
        {
            ui.Enqueue(null, input.TelegramId,
                "Вихідний код цієї версії та ліцензія:\n" + "https://github.com/TomaGavk228/trivozhno-replit/tree/feat/gemini-3-5-flash-lite-2026-10-06");
            return;
        }
        var u = await db.Users.SingleOrDefaultAsync(x => x.TelegramId == input.TelegramId, ct);
        if (u is null)
        {
            // A payment can arrive after full data deletion; the money is never ignored.
            if (input.Payment is { } early)
            {
                u = new() { TelegramId = input.TelegramId, CreatedAt = clock.UtcNow }; db.Users.Add(u);
                await plus.Paid(u, early, ct); return;
            }
            if (command != "/start") { ui.Enqueue(null, input.TelegramId, uk["start.required"]); return; }
            u = new() { TelegramId = input.TelegramId, CreatedAt = clock.UtcNow }; db.Users.Add(u);
            ui.Menu(u, firstStart: true);
            return;
        }
        u.Blocked = false;
        if (input.Payment is { } payment) { await plus.Paid(u, payment, ct); return; }
        if (input.Unsupported) { ui.Say(u, "unsupported"); return; }
        if (command is not null && await Command(u, command, ct)) return;
        if (input.Action is { } raw)
        {
            ui.EditTarget = input.MessageId;
            var split = raw.Split('|', 2);
            if (Ui.Guarded(split[0]) && (split.Length != 2 || split[1] != u.UiToken)) { ui.Say(u, "stale"); return; }
            await Run(u, split[0], ct); return;
        }
        if (string.IsNullOrWhiteSpace(input.Text)) { ui.Say(u, "empty"); return; }

        // The main reply keyboard stays visible next to inline screens.
        var mainKeyboardVisible = Idle(u.State) && !(u.State == UserState.ReminderTime && u.CustomTime);
        if (mainKeyboardVisible)
        {
            foreach (var (key, action) in new[] { ("menu.talk", "menu:talk"), ("menu.confession", "menu:confession"), ("menu.mood", "menu:mood"), ("menu.plus", "plus:show"), ("menu.settings", "menu:settings") })
            {
                if (!uk.Is(key, input.Text)) continue;
                u.Go(UserState.MainMenu); u.PendingAction = "";
                await Run(u, action, ct);
                return;
            }
        }

        if (u.State == UserState.ConfessionConfirm && options.Confessions)
        {
            if (uk.Is("confession.send", input.Text)) { await confessions.Send(u, ct); return; }
            if (uk.Is("confession.edit", input.Text)) { await confessions.Edit(u, ct); return; }
            if (uk.Is("cancel", input.Text)) { await Exit(u, false, ct); return; }
        }
        if (u.State == UserState.ChatActive && uk.Is("chat.end", input.Text)) { await Exit(u, true, ct); return; }
        if (HasExitReply(u.State) && uk.Is("exit", input.Text)) { await Exit(u, false, ct); return; }
        if (u.State == UserState.ConfessionDraft && uk.Is("done", input.Text)) { await confessions.Confirm(u, ct); return; }
        if (u.State == UserState.MoodNote && uk.Is("mood.save", input.Text)) { await mood.Save(u, false, ct); return; }
        if (u.State == UserState.MoodNote && uk.Is("mood.skip", input.Text))
        {
            if (await drafts.HasText(u, ct)) Discard(u, "mood:skip"); else await mood.Save(u, true, ct);
            return;
        }
        // The 🆘 button existed in an earlier version; quietly bring the current keyboard back.
        if (uk.Is("sos.button", input.Text))
        {
            if (u.State == UserState.ChatActive) ui.Say(u, "chat.already", ui.ChatKeyboard()); else ui.Menu(u);
            return;
        }
        // Old/hidden service keys never become a confession, note or AI message.
        if (new[]
        {
            "chat.end", "exit", "done", "mood.save", "mood.skip", "sos.button",
            "menu.talk", "menu.confession", "menu.mood", "menu.plus", "menu.settings",
            "confession.send", "confession.edit", "cancel"
        }.Any(key => uk.Is(key, input.Text)))
        {
            ui.Say(u, "stale");
            return;
        }
        switch (u.State)
        {
            case UserState.ChatActive when options.Conversation: await chat.Receive(u, input, ct); break;
            case UserState.ConfessionDraft when options.Confessions:
            case UserState.MoodNote when options.Mood: await drafts.Append(u, input.Text, ct); break;
            case UserState.ConfessionConfirm: ui.Say(u, "confession.edit.required"); break;
            case UserState.ConfessionSending: ui.Say(u, "confession.pending"); break;
            case UserState.ReminderTime when u.CustomTime && options.Reminders: await reminders.Save(u, input.Text, ct); break;
            case UserState.HabitAdd: await habits.Add(u, input.Text, ct); break;
            case UserState.FriendName: friend.SaveName(u, input.Text); break;
            default: ui.Say(u, "choose"); break;
        }
    }

    private async Task<bool> Command(BotUser u, string command, CancellationToken ct)
    {
        switch (command)
        {
            case "/start" or "/menu":
                if (await drafts.HasText(u, ct)) { Discard(u, "menu"); return true; }
                await Exit(u, true, ct); return true;
            case "/talk": await Run(u, "menu:talk", ct); return true;
            case "/mood": await Run(u, "menu:mood", ct); return true;
            case "/settings": await Run(u, "menu:settings", ct); return true;
            case "/plus": plus.Show(u); return true;
            case "/donate": plus.Donate(u); return true;
            case "/share": plus.Share(u); return true;
            case "/help": plus.Help(u); return true;
            case "/paysupport": await plus.PaySupport(u, ct); return true;
            case "/terms": plus.Terms(u); return true;
            default: return false;
        }
    }

    private async Task Run(BotUser u, string action, CancellationToken ct)
    {
        // Information screens and exercises: valid in any state, change nothing.
        switch (action)
        {
            case "exit": await Exit(u, false, ct); return;
            case "plus:show": plus.Show(u); return;
            case "plus:buy": plus.Buy(u); return;
            case "plus:terms": plus.Terms(u); return;
            case "donate:show": plus.Donate(u); return;
            case "share": plus.Share(u); return;
            case "offer:no": u.ReminderPromptShown = true; if (u.State == UserState.MainMenu) ui.Menu(u, false); return;
        }
        if (action.StartsWith("donate:", StringComparison.Ordinal) && int.TryParse(action[7..], out var stars)) { plus.DonateStars(u, stars); return; }
        if (u.State == UserState.ConfirmDiscard)
        {
            if (action == "discard:yes")
            {
                var then = u.PendingAction;
                if (then == "mood:skip") await mood.Save(u, true, ct);
                else if (then is "menu" or "") await Exit(u, true, ct);
                else { await drafts.Delete(u, ct); u.Go(UserState.MainMenu); u.PendingAction = ""; await Run(u, then, ct); }
                return;
            }
            if (action == "discard:no") { await Restore(u, ct); return; }
        }
        if (u.State == UserState.ConfessionConfirm && options.Confessions)
        {
            switch (action)
            {
                case "conf:send": await confessions.Send(u, ct); return;
                case "conf:edit": await confessions.Edit(u, ct); return;
                case "conf:cancel": await Exit(u, false, ct); return;
            }
        }
        if (u.State == UserState.ConfessionSending && action == "conf:retry" && options.Confessions) { await confessions.Retry(u, ct); return; }
        if (action is "menu:talk" or "rem:start" && u.State == UserState.ChatActive) { ui.Say(u, "chat.already", ui.ChatKeyboard()); return; }
        if (!Navigation(action)) { ui.Say(u, "stale"); return; }
        if (!await Leave(u, action, ct)) return;
        switch (action)
        {
            case "menu:talk" or "rem:start" when options.Conversation: chat.Open(u); return;
            case "menu:confession" when options.Confessions: await confessions.Open(u, ct); return;
            case "menu:mood" when options.Mood: await mood.Open(u, ct); return;
            case "mood:month" when options.Mood: await mood.Month(u, ct); return;
            case "menu:settings" or "settings:show" or "settings:cancel": settings.Show(u); return;
            case "offer:yes" when options.Reminders && options.Conversation: reminders.Frequency(u); return;
            case "settings:reminders" when options.Reminders && options.Conversation: await reminders.Show(u, ct); return;
            case "settings:clear": settings.ConfirmClear(u); return;
            case "settings:delete": settings.ConfirmDelete(u); return;
            case "settings:about": settings.About(u); return;
            case "settings:privacy": settings.Privacy(u); return;
            case "settings:friend" when options.Conversation: friend.Show(u); return;
            case "friend:name": friend.AskName(u); return;
            case "rem:change" when options.Reminders && options.Conversation: reminders.Frequency(u); return;
            case "rem:enable" when options.Reminders && options.Conversation: await reminders.Toggle(u, true, ct); return;
            case "rem:disable" when options.Reminders && options.Conversation: await reminders.Toggle(u, false, ct); return;
            case "rem:personal:on" when options.Reminders && options.Conversation: await reminders.Personal(u, true, ct); return;
            case "rem:personal:off" when options.Reminders && options.Conversation: await reminders.Personal(u, false, ct); return;
            case "rem:custom" when options.Reminders: reminders.Custom(u); return;
            case "habit:list": await habits.List(u, ct); return;
            case "habit:add": await habits.AskTitle(u, ct); return;
            case "habit:del": await habits.PickDelete(u, ct); return;
            case "settings:clear:yes" when u.State == UserState.ConfirmClearMemory: await settings.Clear(u, ct); return;
            case "settings:delete:yes" when u.State == UserState.ConfirmDeleteData: await settings.Delete(u, ct); return;
        }
        if (action.StartsWith("friend:", StringComparison.Ordinal) && options.Conversation) { friend.Set(u, action); return; }
        if (action.StartsWith("mood:", StringComparison.Ordinal) && options.Mood && int.TryParse(action[5..], out var v) && v is >= 1 and <= 5)
        { await mood.Select(u, v, ct); return; }
        if (action.StartsWith("history:", StringComparison.Ordinal) && options.Mood && action[8..] is "latest" or "older" or "newer" or "previous")
        { await mood.History(u, u.State == UserState.MoodHistory ? action[8..] : "latest", ct); return; }
        if (action.StartsWith("rem:freq:", StringComparison.Ordinal) && options.Reminders && int.TryParse(action[9..], out var f) && f is >= 1 and <= 3)
        { reminders.Time(u, f); return; }
        if (action.StartsWith("rem:time:", StringComparison.Ordinal) && options.Reminders) { await reminders.Save(u, action[9..], ct); return; }
        if (action.StartsWith("habit:t:", StringComparison.Ordinal) && long.TryParse(action[8..], out var toggle)) { await habits.Toggle(u, toggle, ct); return; }
        if (action.StartsWith("habit:d:", StringComparison.Ordinal) && long.TryParse(action[8..], out var remove)) { await habits.Delete(u, remove, ct); return; }
        ui.Say(u, "stale");
    }

    private static bool Navigation(string action) =>
        action is "menu:talk" or "rem:start" or "menu:confession" or "menu:mood" or "menu:settings" or "offer:yes" ||
        action.StartsWith("settings:", StringComparison.Ordinal) || action.StartsWith("rem:", StringComparison.Ordinal) ||
        action.StartsWith("mood:", StringComparison.Ordinal) || action.StartsWith("history:", StringComparison.Ordinal) ||
        action.StartsWith("habit:", StringComparison.Ordinal) || action.StartsWith("friend:", StringComparison.Ordinal);

    // Prepares a state change requested from anywhere. An open chat is closed the
    // usual way (memory keeps working); unsaved draft text is never dropped silently.
    private async Task<bool> Leave(BotUser u, string then, CancellationToken ct)
    {
        if (Idle(u.State)) return true;
        if (u.State is UserState.ConfessionDraft or UserState.ConfessionConfirm or UserState.MoodNote or UserState.ConfirmDiscard &&
            await drafts.HasText(u, ct)) { Discard(u, then); return false; }
        await ui.CloseSession(u, ct);
        await drafts.Delete(u, ct);
        u.Go(UserState.MainMenu); u.PendingAction = "";
        return true;
    }
    private void Discard(BotUser u, string then)
    {
        if (u.State != UserState.ConfirmDiscard) u.ReturnState = u.State;
        u.PendingAction = then; u.Go(UserState.ConfirmDiscard);
        ui.Say(u, "discard", ui.Inline(u, ("discard:yes", "discard.yes"), ("discard:no", "back")));
    }
    private async Task Restore(BotUser u, CancellationToken ct)
    {
        var state = u.ReturnState; u.PendingAction = ""; u.Go(state);
        if (state == UserState.ConfessionConfirm) await confessions.Confirm(u, ct);
        else if (state == UserState.ConfessionDraft) ui.Say(u, "confession.invite", ui.ReplyRows(true, ["done", "exit"]));
        else if (state == UserState.MoodNote) ui.Say(u, "mood.note", ui.ReplyPair("mood.save", "mood.skip", "exit"));
        else ui.Menu(u);
    }
    private async Task Exit(BotUser u, bool successfulChat, CancellationToken ct)
    {
        var answered = successfulChat && u.SessionId is { } id && await db.Sessions.AnyAsync(x => x.Id == id && x.HasAnswer, ct);
        await ui.CloseSession(u, ct);
        // A submitted confession retains its delivery record even after navigation changes.
        await drafts.Delete(u, ct); ui.Menu(u);
        if (answered) ui.OfferReminder(u);
    }
    private static bool HasExitReply(UserState state) => state is UserState.ConfessionDraft or UserState.ConfessionConfirm or UserState.ConfessionSending or UserState.MoodNote or UserState.ReminderTime;
}
