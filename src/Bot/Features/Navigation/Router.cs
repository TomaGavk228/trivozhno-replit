using Microsoft.EntityFrameworkCore;
using Trivozhno.Features.Confessions;
using Trivozhno.Features.Conversation;
using Trivozhno.Features.Mood;
using Trivozhno.Features.Reminders;
using Trivozhno.Features.Settings;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Infrastructure.Telegram;
using Trivozhno.Resources;

namespace Trivozhno.Features.Navigation;

// All calls occur inside one per-user lock and one database transaction. No HTTP here.
public sealed class Router(BotDb db, Ui ui, DraftStore drafts, Uk uk, IClock clock, BotOptions options,
    ConversationHandler chat, ConfessionHandler confessions, MoodHandler mood, ReminderHandler reminders, SettingsHandler settings)
{
    public async Task Handle(BotInput input, CancellationToken ct)
    {
        var command = input.Text?.Split(' ', 2)[0].Split('@', 2)[0];
        var u = await db.Users.SingleOrDefaultAsync(x => x.TelegramId == input.TelegramId, ct);
        if (u is null)
        {
            if (command != "/start") { ui.Enqueue(null, input.TelegramId, uk["start.required"]); return; }
            u = new() { TelegramId = input.TelegramId, CreatedAt = clock.UtcNow }; db.Users.Add(u);
            ui.Menu(u, firstStart: true);
            return;
        }
        u.Blocked = false;
        if (input.Unsupported) { ui.Say(u, "unsupported"); return; }
        if (command is "/start" or "/menu")
        {
            if (await drafts.HasText(u, ct)) { Discard(u, "menu"); return; }
            await Exit(u, true, ct); return;
        }
        if (input.Action is { } raw)
        {
            var split = raw.Split('|', 2);
            if (split.Length != 2 || split[1] != u.UiToken) { ui.Say(u, "stale"); return; }
            await Callback(u, split[0], ct); return;
        }
        if (string.IsNullOrWhiteSpace(input.Text)) { ui.Say(u, "empty"); return; }
                var menuButtons = new (string Key, string Action)[]
        {
            ("menu.talk", "menu:talk"),
            ("menu.confession", "menu:confession"),
            ("menu.mood", "menu:mood"),
            ("menu.settings", "menu:settings")
        };

        // Головна клавіатура доступна також поруч із
        // налаштуваннями та історією з inline-кнопками.
        var mainKeyboardVisible = u.State is UserState.MainMenu
            or UserState.Settings or UserState.MoodSelect or UserState.MoodHistory
            or UserState.ReminderFrequency or UserState.ConfirmMoodContext
            or UserState.ConfirmClearMemory or UserState.ConfirmDeleteData
            || u.State == UserState.ReminderTime && !u.CustomTime;

        if (mainKeyboardVisible)
        {
            foreach (var button in menuButtons)
            {
                if (!uk.Is(button.Key, input.Text)) continue;

                u.Go(UserState.MainMenu);
                u.PendingAction = "";
                await Callback(u, button.Action, ct);
                return;
            }
        }

        if (u.State == UserState.ConfessionConfirm && options.Confessions)
        {
            if (uk.Is("confession.send", input.Text))
            {
                await confessions.Send(u, ct);
                return;
            }

            if (uk.Is("confession.edit", input.Text))
            {
                await confessions.Edit(u, ct);
                return;
            }

            if (uk.Is("cancel", input.Text))
            {
                await Exit(u, false, ct);
                return;
            }
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
        // Old/hidden service keys never become a confession, note or AI message.
                if (new[]
        {
            "chat.end", "exit", "done", "mood.save", "mood.skip",
            "menu.talk", "menu.confession", "menu.mood", "menu.settings",
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
            default: ui.Say(u, "choose"); break;
        }
    }
    private async Task Callback(BotUser u, string action, CancellationToken ct)
    {
        if (action == "exit") { await Exit(u, false, ct); return; }
        if (u.State == UserState.ConfirmDiscard)
        {
            if (action == "discard:yes")
            {
                if (u.PendingAction == "mood:skip") await mood.Save(u, true, ct);
                else await Exit(u, true, ct);
            }
            else if (action == "discard:no") await Restore(u, ct);
            else ui.Say(u, "stale");
            return;
        }
        if (u.State == UserState.MainMenu)
        {
            switch (action)
            {
                case "menu:talk" or "rem:start" when options.Conversation: chat.Open(u); return;
                case "menu:confession" when options.Confessions: await confessions.Open(u, ct); return;
                case "menu:mood" when options.Mood: mood.Open(u); return;
                case "menu:settings": settings.Show(u); return;
                case "offer:yes" when options.Reminders && options.Conversation: reminders.Frequency(u); return;
                case "offer:no": u.ReminderPromptShown = true; ui.Menu(u, false); return;
            }
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
        if (u.State == UserState.MoodSelect && options.Mood && action.StartsWith("mood:") && int.TryParse(action[5..], out var v) && v is >= 1 and <= 5)
        { await mood.Select(u, v, ct); return; }
        if (u.State is UserState.MoodHistory or UserState.MoodSelect && options.Mood && action.StartsWith("history:") && action[8..] is "latest" or "older" or "newer" or "previous")
        { await mood.History(u, action[8..], ct); return; }
        if (u.State == UserState.Settings)
        {
            switch (action)
            {
                case "settings:reminders" when options.Reminders && options.Conversation: await reminders.Show(u, ct); return;
                case "settings:mood" when options.Mood && options.Conversation: settings.ToggleMood(u); return;
                case "settings:clear": settings.ConfirmClear(u); return;
                case "settings:delete": settings.ConfirmDelete(u); return;
                case "settings:about": ui.Say(u, "settings.about.text"); settings.Show(u); return;
                case "rem:change" when options.Reminders && options.Conversation: reminders.Frequency(u); return;
                case "rem:enable" when options.Reminders && options.Conversation: await reminders.Toggle(u, true, ct); return;
                case "rem:disable" when options.Reminders && options.Conversation: await reminders.Toggle(u, false, ct); return;
            }
        }
        if (u.State == UserState.ReminderFrequency && options.Reminders && action.StartsWith("rem:freq:") && int.TryParse(action[9..], out var f) && f is >= 1 and <= 3)
        { reminders.Time(u, f); return; }
        if (u.State == UserState.ReminderTime && options.Reminders)
        {
            if (action == "rem:custom") { reminders.Custom(u); return; }
            if (action.StartsWith("rem:time:")) { await reminders.Save(u, action[9..], ct); return; }
        }
        if (action == "settings:cancel" && u.State is UserState.ConfirmClearMemory or UserState.ConfirmDeleteData or UserState.ConfirmMoodContext) { settings.Show(u); return; }
        if (u.State == UserState.ConfirmClearMemory && action == "settings:clear:yes") { await settings.Clear(u, ct); return; }
        if (u.State == UserState.ConfirmDeleteData && action == "settings:delete:yes") { await settings.Delete(u, ct); return; }
        if (u.State == UserState.ConfirmMoodContext && action == "settings:mood:yes" && options.Mood && options.Conversation) { settings.ConsentMood(u); return; }
        ui.Say(u, "stale");
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
        else if (state == UserState.ConfessionDraft) ui.Say(u, "confession.invite", ui.Reply("done", "exit"));
        else if (state == UserState.MoodNote) ui.Say(u, "mood.note", ui.Reply("mood.save", "mood.skip", "exit"));
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
