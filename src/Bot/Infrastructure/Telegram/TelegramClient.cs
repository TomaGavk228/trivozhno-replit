using System.Net.Http.Json;
using System.Text.Json;
using Trivozhno.Host;

namespace Trivozhno.Infrastructure.Telegram;

// Retain only routing fields; never persist Telegram names, usernames or whole profiles.
public sealed record BotInput(long UpdateId, long TelegramId, string? Text, string? Action, bool Unsupported = false, PaymentInfo? Payment = null, long? MessageId = null);
// Only service fields of a successful Telegram payment; no card or profile data exists for Stars.
public sealed record PaymentInfo(string Currency, int Amount, string Payload, string ChargeId, string ProviderChargeId);
public sealed record BotCommand(string Command, string Description);
public sealed record PollBatch(IReadOnlyList<BotInput> Inputs, long NextOffset);
public interface ITelegramClient
{
    Task<PollBatch> Poll(long offset, CancellationToken ct);
    Task<long> Send(long destination, string text, string? markup, CancellationToken ct);
    // Replaces the text and inline buttons of an earlier bot message; falls back to a new message.
    Task<long> Edit(long destination, long messageId, string text, string? markup, CancellationToken ct);
    Task Typing(long destination, CancellationToken ct);
    Task<string> Validate(CancellationToken ct);
    Task<long> SendInvoice(long destination, string invoiceJson, CancellationToken ct);
    Task SetCommands(IReadOnlyList<BotCommand> commands, CancellationToken ct);
}
// Bot username from getMe, used for share links.
public sealed class BotIdentity { public volatile string Username = ""; }
public sealed class TelegramFailure(int code, int retryAfter = 0, string description = "") : Exception($"Telegram code {code}")
{
    public string Description { get; } = description;
    public int Code { get; } = code;
    public int RetryAfter { get; } = retryAfter;
}
public sealed class DeliveryUnknownException : Exception { }

public sealed class TelegramClient(HttpClient http, BotOptions options, IClock clock, Trivozhno.Resources.Uk uk) : ITelegramClient
{
    private string Url(string method) => $"https://api.telegram.org/bot{options.TelegramToken}/{method}";
    private async Task<JsonDocument> Call(string method, object payload, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(Url(method), payload, ct);
        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!json.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var code = json.RootElement.TryGetProperty("error_code", out var c) ? c.GetInt32() : (int)response.StatusCode;
            var retry = json.RootElement.TryGetProperty("parameters", out var p) && p.TryGetProperty("retry_after", out var r) ? r.GetInt32() : 0;
            var description = json.RootElement.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            json.Dispose(); throw new TelegramFailure(code, retry, description);
        }
        return json;
    }
    public async Task<string> Validate(CancellationToken ct)
    {
        using var me = await Call("getMe", new { }, ct);
        using var hook = await Call("getWebhookInfo", new { }, ct);
        if (hook.RootElement.GetProperty("result").GetProperty("url").GetString() is { Length: > 0 })
            throw new InvalidOperationException("Webhook exists. Use the documented deleteWebhook step for the TEST bot first.");
        return me.RootElement.GetProperty("result").TryGetProperty("username", out var name) ? name.GetString() ?? "" : "";
    }
    public async Task SetCommands(IReadOnlyList<BotCommand> commands, CancellationToken ct)
    {
        using var _ = await Call("setMyCommands", new { commands = commands.Select(x => new { command = x.Command, description = x.Description }) }, ct);
    }
    public async Task<PollBatch> Poll(long offset, CancellationToken ct)
    {
        using var json = await Call("getUpdates", new { offset, timeout = 25, limit = 100, allowed_updates = new[] { "message", "callback_query", "pre_checkout_query" } }, ct);
        var inputs = new List<BotInput>(); var next = offset;
        foreach (var update in json.RootElement.GetProperty("result").EnumerateArray())
        {
            var id = update.GetProperty("update_id").GetInt64(); next = Math.Max(next, id + 1);
            if (update.TryGetProperty("pre_checkout_query", out var checkout))
            {
                // Telegram waits at most 10 seconds, so the stateless check is answered right here.
                var error = Trivozhno.Features.Plus.PaymentPayload.Check(checkout.GetProperty("from").GetProperty("id").GetInt64(),
                    checkout.GetProperty("currency").GetString(), checkout.GetProperty("total_amount").GetInt32(),
                    checkout.GetProperty("invoice_payload").GetString(), options, clock.UtcNow);
                object answer = error is null
                    ? new { pre_checkout_query_id = checkout.GetProperty("id").GetString(), ok = true }
                    : new { pre_checkout_query_id = checkout.GetProperty("id").GetString(), ok = false, error_message = uk[error] };
                try { using var ack = await Call("answerPreCheckoutQuery", answer, ct); }
                catch (Exception e) when (e is TelegramFailure or HttpRequestException or TaskCanceledException) { if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested(); }
                continue;
            }
            if (update.TryGetProperty("callback_query", out var cb))
            {
                try { using var ack = await Call("answerCallbackQuery", new { callback_query_id = cb.GetProperty("id").GetString() }, ct); }
                catch (Exception e) when (e is TelegramFailure or HttpRequestException or TaskCanceledException) { if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested(); }
                if (!cb.TryGetProperty("message", out var msg) || !IsPrivate(msg) || IsBot(cb.GetProperty("from"))) continue;
                var from = cb.GetProperty("from").GetProperty("id").GetInt64();
                if (from != msg.GetProperty("chat").GetProperty("id").GetInt64()) continue;
                inputs.Add(new(id, from, null, cb.TryGetProperty("data", out var d) ? d.GetString() : "",
                    MessageId: msg.TryGetProperty("message_id", out var mid) ? mid.GetInt64() : null));
            }
            else if (update.TryGetProperty("message", out var message) && IsPrivate(message) && message.TryGetProperty("from", out var sender) && !IsBot(sender))
            {
                var from = sender.GetProperty("id").GetInt64();
                if (from != message.GetProperty("chat").GetProperty("id").GetInt64()) continue;
                if (message.TryGetProperty("successful_payment", out var paid))
                {
                    inputs.Add(new(id, from, null, null, false, new(paid.GetProperty("currency").GetString() ?? "",
                        paid.GetProperty("total_amount").GetInt32(), paid.GetProperty("invoice_payload").GetString() ?? "",
                        paid.GetProperty("telegram_payment_charge_id").GetString() ?? "",
                        paid.TryGetProperty("provider_payment_charge_id", out var provider) ? provider.GetString() ?? "" : "")));
                    continue;
                }
                inputs.Add(new(id, from, message.TryGetProperty("text", out var txt) ? txt.GetString() : null, null, !message.TryGetProperty("text", out _)));
            }
        }
        return new(inputs, next);
    }
    private static bool IsPrivate(JsonElement msg) => msg.TryGetProperty("chat", out var c) && c.GetProperty("type").GetString() == "private";
    private static bool IsBot(JsonElement from) => from.TryGetProperty("is_bot", out var b) && b.GetBoolean();
    public async Task<long> Send(long destination, string text, string? markup, CancellationToken ct)
    {
        try
        {
            using var parsed = markup is null ? null : JsonDocument.Parse(markup);
            var payload = new Dictionary<string, object?> { ["chat_id"] = destination, ["text"] = text, ["link_preview_options"] = new { is_disabled = true } };
            if (parsed is not null) payload["reply_markup"] = parsed.RootElement;
            using var json = await Call("sendMessage", payload, ct);
            return json.RootElement.GetProperty("result").GetProperty("message_id").GetInt64();
        }
        catch (TelegramFailure e) when (e.Code >= 500) { throw new DeliveryUnknownException(); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { throw new DeliveryUnknownException(); }
    }
    public async Task<long> Edit(long destination, long messageId, string text, string? markup, CancellationToken ct)
    {
        try
        {
            using var parsed = markup is null ? null : JsonDocument.Parse(markup);
            var payload = new Dictionary<string, object?> { ["chat_id"] = destination, ["message_id"] = messageId, ["text"] = text,
                ["link_preview_options"] = new { is_disabled = true } };
            if (parsed is not null) payload["reply_markup"] = parsed.RootElement;
            using var json = await Call("editMessageText", payload, ct);
            return messageId;
        }
        catch (TelegramFailure e) when (e.Code == 400 && e.Description.Contains("not modified", StringComparison.OrdinalIgnoreCase)) { return messageId; }
        // Too old, deleted or not editable: show the screen as a new message instead.
        catch (TelegramFailure e) when (e.Code == 400) { return await Send(destination, text, markup, ct); }
        catch (TelegramFailure e) when (e.Code >= 500) { throw new DeliveryUnknownException(); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { throw new DeliveryUnknownException(); }
    }
    public async Task<long> SendInvoice(long destination, string invoiceJson, CancellationToken ct)
    {
        try
        {
            using var invoice = JsonDocument.Parse(invoiceJson);
            var payload = new Dictionary<string, object?> { ["chat_id"] = destination };
            foreach (var property in invoice.RootElement.EnumerateObject()) payload[property.Name] = property.Value;
            using var json = await Call("sendInvoice", payload, ct);
            return json.RootElement.GetProperty("result").GetProperty("message_id").GetInt64();
        }
        catch (TelegramFailure e) when (e.Code >= 500) { throw new DeliveryUnknownException(); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { throw new DeliveryUnknownException(); }
    }
    public async Task Typing(long destination, CancellationToken ct)
    {
        try { using var j = await Call("sendChatAction", new { chat_id = destination, action = "typing" }, ct); }
        catch (Exception e) when (e is TelegramFailure or HttpRequestException or TaskCanceledException) { }
    }
}
