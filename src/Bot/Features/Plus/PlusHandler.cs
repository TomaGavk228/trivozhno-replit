using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Trivozhno.Features.Navigation;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Infrastructure.Telegram;
using Trivozhno.Resources;

namespace Trivozhno.Features.Plus;

// Invoice payloads are self-describing, so pre_checkout_query is answered
// without a database round trip inside Telegram's 10-second window.
public static class PaymentPayload
{
    public const int MaxAgeSeconds = 86400;
    public sealed record Parsed(string Kind, int Amount, long TelegramId, long IssuedAt);

    public static string Plus(long telegramId, int amount, DateTimeOffset now) => $"plus|{amount}|{telegramId}|{now.ToUnixTimeSeconds()}";
    public static string Donation(long telegramId, int amount, DateTimeOffset now) => $"donate|{amount}|{telegramId}|{now.ToUnixTimeSeconds()}";

    public static Parsed? Parse(string? payload)
    {
        var parts = payload?.Split('|');
        if (parts is not { Length: 4 } || parts[0] is not ("plus" or "donate") ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var amount) ||
            !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var telegramId) ||
            !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var issued)) return null;
        return new(parts[0], amount, telegramId, issued);
    }

    // Returns null when the payment may proceed, otherwise a uk.json key.
    public static string? Check(long from, string? currency, int total, string? payload, BotOptions options, DateTimeOffset now)
    {
        var p = Parse(payload);
        if (p is null || currency != "XTR" || p.TelegramId != from || p.Amount != total) return "pay.error.invalid";
        if (now.ToUnixTimeSeconds() - p.IssuedAt > MaxAgeSeconds) return "pay.error.stale";
        if (p.Kind == "plus" && total != options.PlusPriceStars) return "pay.error.stale";
        if (p.Kind == "donate" && !BotOptions.DonationAmounts.Contains(total)) return "pay.error.invalid";
        return null;
    }
}

public sealed class ChatLimits(Ui ui, Uk uk, IClock clock, BotOptions options)
{
    public DateOnly Today()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.Timezone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, zone).DateTime);
    }
    // Counts one chat turn. Returns false when today's limit is already used up.
    public bool TryCount(BotUser u)
    {
        var today = Today();
        if (u.ChatCountDay != today) { u.ChatCountDay = today; u.ChatCount = 0; }
        var limit = u.IsPlus(clock.UtcNow) ? options.PlusDailyMax : options.ChatDailyFree;
        if (u.ChatCount >= limit) return false;
        u.ChatCount++;
        return true;
    }
    public void Notify(BotUser u)
    {
        if (u.IsPlus(clock.UtcNow)) { ui.Say(u, "limit.plus"); return; }
        ui.Text(u, uk.Format("limit.free", options.ChatDailyFree),
            ui.Keyboard(u, [ui.B("plus.button", "plus:show"), ui.B("donate.button", "donate:show")]));
    }
}

public sealed class PlusHandler(BotDb db, Ui ui, Uk uk, IClock clock, BotOptions options, BotIdentity identity)
{
    private DateTimeOffset Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(options.Timezone));
    public string Date(DateTimeOffset at) => Local(at).ToString("dd.MM", CultureInfo.InvariantCulture);

    public void Show(BotUser u)
    {
        var now = clock.UtcNow;
        var buy = new Ui.Button(uk.Format(u.IsPlus(now) ? "plus.extend" : "plus.buy", options.PlusPriceStars), "plus:buy");
        if (u.IsPlus(now))
        {
            Ui.Button[] tools = options.Conversation ? [ui.B("settings.friend", "settings:friend"), ui.B("settings.habits", "habit:list")] : [ui.B("settings.habits", "habit:list")];
            ui.Text(u, uk.Format("plus.active", Date(u.PlusUntil!.Value)), ui.Keyboard(u, tools, [buy]));
            return;
        }
        ui.Text(u, uk.Format("plus.text", options.PlusPriceStars, options.PlusDays), ui.Keyboard(u, [buy], [ui.B("plus.terms", "plus:terms")]));
    }
    public void Buy(BotUser u)
    {
        ui.Invoice(u, uk["plus.invoice.title"], uk.Format("plus.invoice.description", options.PlusDays),
            PaymentPayload.Plus(u.TelegramId, options.PlusPriceStars, clock.UtcNow), options.PlusPriceStars);
    }
    public void Donate(BotUser u)
    {
        var stars = BotOptions.DonationAmounts.Select(a => new Ui.Button(uk.Format("donate.stars", a), $"donate:{a}")).ToArray();
        Ui.Button[] link = options.DonateUrl.Length > 0 ? [ui.Link("donate.mono", options.DonateUrl)] : [];
        ui.Say(u, "donate.text", ui.Keyboard(u, stars, link));
    }
    public void DonateStars(BotUser u, int amount)
    {
        if (!BotOptions.DonationAmounts.Contains(amount)) { ui.Say(u, "stale"); return; }
        ui.Invoice(u, uk["donate.invoice.title"], uk["donate.invoice.description"], PaymentPayload.Donation(u.TelegramId, amount, clock.UtcNow), amount);
    }

    // Idempotent: a repeated successful_payment with the same charge id changes nothing.
    public async Task Paid(BotUser u, PaymentInfo paid, CancellationToken ct)
    {
        if (paid.ChargeId.Length == 0 || await db.Payments.AnyAsync(x => x.TelegramChargeId == paid.ChargeId, ct)) return;
        var now = clock.UtcNow;
        var parsed = PaymentPayload.Parse(paid.Payload);
        var row = new Payment
        {
            UserId = u.Id, TelegramId = u.TelegramId, Kind = parsed?.Kind == "plus" && paid.Currency == "XTR" ? "plus" : "donation",
            Amount = paid.Amount, Currency = paid.Currency, TelegramChargeId = paid.ChargeId, ProviderChargeId = paid.ProviderChargeId, PaidAt = now
        };
        if (row.Kind == "plus")
        {
            // A renewal during an active period extends from the current end date.
            var start = u.PlusUntil is { } until && until > now ? until : now;
            row.PeriodStart = start; row.PeriodEnd = start.AddDays(options.PlusDays); u.PlusUntil = row.PeriodEnd;
            ui.Text(u, uk.Format("plus.thanks", Date(row.PeriodEnd.Value), Short(paid.ChargeId)));
        }
        else ui.Text(u, uk.Format("donate.thanks", paid.Amount, Short(paid.ChargeId)));
        db.Payments.Add(row);
        await db.SaveChangesAsync(ct);
    }
    private static string Short(string chargeId) => chargeId.Length <= 12 ? chargeId : chargeId[^12..];

    public void Terms(BotUser u) => ui.Text(u, uk.Format("terms.text", options.PlusPriceStars, options.PlusDays));
    public async Task PaySupport(BotUser u, CancellationToken ct)
    {
        var text = options.SupportContact.Length > 0 ? uk.Format("paysupport.text", options.SupportContact) : uk["paysupport.nocontact"];
        var recent = await db.Payments.AsNoTracking().Where(x => x.TelegramId == u.TelegramId).OrderByDescending(x => x.PaidAt).Take(3).ToListAsync(ct);
        if (recent.Count > 0)
            text += "\n\n" + uk["paysupport.recent"] + "\n" + string.Join('\n', recent.Select(x =>
                $"{Local(x.PaidAt):dd.MM.yyyy} · {x.Amount} ⭐ · {uk[x.Kind == "plus" ? "pay.kind.plus" : "pay.kind.donation"]} · …{Short(x.TelegramChargeId)}" +
                (x.RefundedAt is null ? "" : " · " + uk["pay.refunded"])));
        ui.Text(u, text);
    }
    public string BotLink => identity.Username.Length > 0 ? $"https://t.me/{identity.Username}" : "";
    public void Share(BotUser u)
    {
        if (BotLink.Length == 0) { ui.Say(u, "share.unavailable"); return; }
        var text = uk.Format("share.message", BotLink);
        var url = "https://t.me/share/url?url=" + Uri.EscapeDataString(BotLink) + "&text=" + Uri.EscapeDataString(uk["share.short"]);
        ui.Say(u, "share.intro");
        ui.Text(u, text, ui.Keyboard(u, [ui.Link("share.button", url)]));
    }
    public void Help(BotUser u) =>
        ui.Text(u, uk["help.text"] + (options.TikTokUrl.Length > 0 ? "\n\n" + uk.Format("welcome.tiktok", options.TikTokUrl) : ""));

    // Expiry notices: three days before and on the last day, once per end date.
    public void Notices(BotUser u)
    {
        if (u.PlusUntil is not { } until) return;
        var now = clock.UtcNow;
        var markup = ui.Keyboard(u, [new Ui.Button(uk.Format("plus.extend", options.PlusPriceStars), "plus:buy")]);
        if (u.PlusEndNoticeFor != until && until < TomorrowStart() && until > now.AddDays(-2))
        {
            u.PlusEndNoticeFor = until; u.PlusWarnedFor = until;
            if (!u.Blocked) ui.Text(u, until > now ? uk.Format("plus.ends.today", Local(until).ToString("HH:mm", CultureInfo.InvariantCulture)) : uk["plus.ended"], markup, "plus-notice");
        }
        else if (u.PlusWarnedFor != until && until <= now.AddDays(3) && until > now)
        {
            u.PlusWarnedFor = until;
            if (!u.Blocked) ui.Text(u, uk.Format("plus.ends.soon", Date(until)), markup, "plus-notice");
        }
    }
    public DateTimeOffset TomorrowStart()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.Timezone);
        return Trivozhno.Features.Reminders.ReminderDates.Resolve(TimeZoneInfo.ConvertTime(clock.UtcNow, zone).Date.AddDays(1), zone);
    }
}
