using Microsoft.EntityFrameworkCore;
using Trivozhno.Features.Navigation;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;

namespace Trivozhno.Features.Settings;

public sealed class SettingsHandler(BotDb db, Ui ui, BotOptions options, Trivozhno.Resources.Uk uk, ActiveGenerations active,
    IClock clock, Trivozhno.Features.Plus.PlusHandler plus)
{
    public void Show(BotUser u)
    {
        u.Go(UserState.Settings); u.PendingAction = "";
        var isPlus = u.IsPlus(clock.UtcNow);
        var plusButton = isPlus ? new Ui.Button(uk.Format("settings.plus.active", plus.Date(u.PlusUntil!.Value)), "plus:show") : ui.B("plus.button", "plus:show");
        var first = new List<Ui.Button>();
        if (options.Reminders && options.Conversation) first.Add(ui.B("settings.reminders", "settings:reminders"));
        first.Add(plusButton);
        Ui.Button[] plusRow = options.Conversation ? [ui.B("settings.friend", "settings:friend"), ui.B("settings.habits", "habit:list")] : [ui.B("settings.habits", "habit:list")];
        var text = uk["settings.title"] + "\n\n" + (isPlus ? uk.Format("settings.plus.status", plus.Date(u.PlusUntil!.Value)) : uk["settings.plus.offer"]);
        ui.Text(u, text, ui.Keyboard(u, first.ToArray(), plusRow,
            [ui.B("sos.button", "sos")],
            [ui.B("donate.button.long", "donate:show"), ui.B("share.button.menu", "share")],
            [ui.B("settings.clear", "settings:clear"), ui.B("settings.delete", "settings:delete")],
            [ui.B("settings.about", "settings:about"), ui.B("exit", "exit")]));
    }
    public void ConfirmClear(BotUser u)
    { u.Go(UserState.ConfirmClearMemory); ui.Say(u, "settings.clear.confirm", ui.Inline(u, ("settings:clear:yes", "settings.clear.yes"), ("settings:cancel", "cancel"))); }
    public void ConfirmDelete(BotUser u)
    { u.Go(UserState.ConfirmDeleteData); ui.Say(u, "settings.delete.confirm", ui.Inline(u, ("settings:delete:yes", "settings.delete.yes"), ("settings:cancel", "cancel"))); }
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
        active.Cancel(u.Id);
        var destination = u.TelegramId;
        await db.Inbox.Where(x => x.TelegramId == destination).ExecuteDeleteAsync(ct);
        db.Users.Remove(u); await db.SaveChangesAsync(ct);
        ui.Enqueue(null, destination, uk["settings.deleted"], Ui.RemoveKeyboard, "deletion-receipt");
    }
}
