using Microsoft.EntityFrameworkCore;
using Trivozhno.Features.Navigation;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;

namespace Trivozhno.Features.Settings;

public sealed class SettingsHandler(BotDb db, Ui ui, BotOptions options, Trivozhno.Resources.Uk uk)
{
    public void Show(BotUser u)
    {
        u.Go(UserState.Settings); u.PendingAction = "";
        var rows = new List<(string, string)>();
        if (options.Reminders && options.Conversation) rows.Add(("settings:reminders", "settings.reminders"));
        if (options.Mood && options.Conversation) rows.Add(("settings:mood", u.MoodContextEnabled ? "settings.mood.on" : "settings.mood.off"));
        rows.Add(("settings:clear", "settings.clear")); rows.Add(("settings:delete", "settings.delete"));
        rows.Add(("settings:about", "settings.about")); rows.Add(("exit", "exit"));
        ui.Say(u, "settings.title", ui.Inline(u, rows.ToArray()));
    }
    public void ConfirmClear(BotUser u)
    { u.Go(UserState.ConfirmClearMemory); ui.Say(u, "settings.clear.confirm", ui.Inline(u, ("settings:clear:yes", "settings.clear.yes"), ("settings:cancel", "cancel"))); }
    public void ConfirmDelete(BotUser u)
    { u.Go(UserState.ConfirmDeleteData); ui.Say(u, "settings.delete.confirm", ui.Inline(u, ("settings:delete:yes", "settings.delete.yes"), ("settings:cancel", "cancel"))); }
    public void ToggleMood(BotUser u)
    {
        if (!u.MoodContextEnabled && !u.MoodConsentShown)
        { u.Go(UserState.ConfirmMoodContext); ui.Say(u, "settings.mood.consent", ui.Inline(u, ("settings:mood:yes", "yes"), ("settings:cancel", "cancel"))); return; }
        u.MoodContextEnabled = !u.MoodContextEnabled; Show(u);
    }
    public void ConsentMood(BotUser u) { u.MoodConsentShown = true; u.MoodContextEnabled = true; Show(u); }
    public async Task Clear(BotUser u, CancellationToken ct)
    {
        u.MemoryVersion++;
        u.ChatStyleProfile = "";
        await ui.CloseSession(u, ct);
        await db.Messages.Where(x => x.UserId == u.Id).ExecuteDeleteAsync(ct);
        await db.Summaries.Where(x => x.UserId == u.Id).ExecuteDeleteAsync(ct);
        await db.Sessions.Where(x => x.UserId == u.Id).ExecuteDeleteAsync(ct);
        await db.Outbox.Where(x => x.UserId == u.Id && x.Kind == "ai").ExecuteDeleteAsync(ct);
        ui.Say(u, "settings.cleared"); Show(u);
    }
    public async Task Delete(BotUser u, CancellationToken ct)
    {
        // No channel deletion/edit API exists anywhere in this application.
        var destination = u.TelegramId;
        await db.Inbox.Where(x => x.TelegramId == destination).ExecuteDeleteAsync(ct);
        db.Users.Remove(u); await db.SaveChangesAsync(ct);
        ui.Enqueue(null, destination, uk["settings.deleted"], Ui.RemoveKeyboard, "deletion-receipt");
    }
}
