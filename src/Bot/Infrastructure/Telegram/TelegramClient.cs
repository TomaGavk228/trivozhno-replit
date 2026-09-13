using System.Net.Http.Json;
using System.Text.Json;
using Trivozhno.Host;

namespace Trivozhno.Infrastructure.Telegram;

// Retain only routing fields; never persist Telegram names, usernames or whole profiles.
public sealed record BotInput(long UpdateId, long TelegramId, string? Text, string? Action, bool Unsupported = false);
public sealed record PollBatch(IReadOnlyList<BotInput> Inputs, long NextOffset);
public interface ITelegramClient
{
    Task<PollBatch> Poll(long offset, CancellationToken ct);
    Task<long> Send(long destination, string text, string? markup, CancellationToken ct);
    Task Typing(long destination, CancellationToken ct);
    Task Validate(CancellationToken ct);
}
public sealed class TelegramFailure(int code, int retryAfter = 0) : Exception($"Telegram code {code}")
{
    public int Code { get; } = code;
    public int RetryAfter { get; } = retryAfter;
}
public sealed class DeliveryUnknownException : Exception { }

public sealed class TelegramClient(HttpClient http, BotOptions options) : ITelegramClient
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
            json.Dispose(); throw new TelegramFailure(code, retry);
        }
        return json;
    }
    public async Task Validate(CancellationToken ct)
    {
        using var me = await Call("getMe", new { }, ct);
        using var hook = await Call("getWebhookInfo", new { }, ct);
        if (hook.RootElement.GetProperty("result").GetProperty("url").GetString() is { Length: > 0 })
            throw new InvalidOperationException("Webhook exists. Use the documented deleteWebhook step for the TEST bot first.");
    }
    public async Task<PollBatch> Poll(long offset, CancellationToken ct)
    {
        using var json = await Call("getUpdates", new { offset, timeout = 25, limit = 100, allowed_updates = new[] { "message", "callback_query" } }, ct);
        var inputs = new List<BotInput>(); var next = offset;
        foreach (var update in json.RootElement.GetProperty("result").EnumerateArray())
        {
            var id = update.GetProperty("update_id").GetInt64(); next = Math.Max(next, id + 1);
            if (update.TryGetProperty("callback_query", out var cb))
            {
                try { using var ack = await Call("answerCallbackQuery", new { callback_query_id = cb.GetProperty("id").GetString() }, ct); }
                catch (Exception e) when (e is TelegramFailure or HttpRequestException or TaskCanceledException) { if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested(); }
                if (!cb.TryGetProperty("message", out var msg) || !IsPrivate(msg) || IsBot(cb.GetProperty("from"))) continue;
                var from = cb.GetProperty("from").GetProperty("id").GetInt64();
                if (from != msg.GetProperty("chat").GetProperty("id").GetInt64()) continue;
                inputs.Add(new(id, from, null, cb.TryGetProperty("data", out var d) ? d.GetString() : ""));
            }
            else if (update.TryGetProperty("message", out var message) && IsPrivate(message) && message.TryGetProperty("from", out var sender) && !IsBot(sender))
            {
                var from = sender.GetProperty("id").GetInt64();
                if (from != message.GetProperty("chat").GetProperty("id").GetInt64()) continue;
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
    public async Task Typing(long destination, CancellationToken ct)
    {
        try { using var j = await Call("sendChatAction", new { chat_id = destination, action = "typing" }, ct); }
        catch (Exception e) when (e is TelegramFailure or HttpRequestException or TaskCanceledException) { }
    }
}
