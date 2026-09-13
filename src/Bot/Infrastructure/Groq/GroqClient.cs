using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;

namespace Trivozhno.Infrastructure.Groq;

public sealed record AiMessage(string Role, string Content);
public sealed record AiResult(string Text, string Model, int Tokens);
public interface IAiClient { Task<AiResult> Complete(IReadOnlyList<AiMessage> messages, bool summary, CancellationToken ct); }
public sealed class AiUnavailableException : Exception { }
public sealed class ContextTooLargeException : Exception { }
public static class TokenEstimate
{
    // Estimate, not a tokenizer: usage responses reconcile reservations; 429 remains authoritative.
    public static int Count(string text) => (Encoding.UTF8.GetByteCount(text) + 2) / 3 + 8;
    public static int Count(IEnumerable<AiMessage> messages) => messages.Sum(m => Count(m.Content) + 8);
}

public sealed class ApiUsage
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public int Tokens { get; set; }
    public bool Summary { get; set; }
}

public sealed class AiQuota(IServiceScopeFactory scopes, BotOptions options, IClock clock)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim slots = new(options.AiConcurrency, options.AiConcurrency);
    public async Task<IDisposable> Enter(CancellationToken ct) { await slots.WaitAsync(ct); return new Slot(slots); }
    private sealed class Slot(SemaphoreSlim semaphore) : IDisposable { public void Dispose() => semaphore.Release(); }
    public async Task<long> Reserve(int tokens, bool summary, CancellationToken ct)
    {
        if (tokens > options.TokensPerMinute || tokens > options.TokensPerDay) throw new ContextTooLargeException();
        while (true)
        {
            var delay = TimeSpan.FromSeconds(1);
            await gate.WaitAsync(ct);
            try
            {
                using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<BotDb>();
                var now = clock.UtcNow; var minute = now.AddMinutes(-1); var day = now.AddDays(-1);
                var recent = await db.Set<ApiUsage>().Where(x => x.At > day).ToListAsync(ct);
                var shortWindow = recent.Where(x => x.At > minute).ToArray();
                if (summary && await db.Messages.AnyAsync(x => x.Status == "queued" || x.Status == "processing", ct)) throw new AiUnavailableException();
                if (recent.Count >= options.RequestsPerDay || recent.Sum(x => x.Tokens) + tokens > options.TokensPerDay) throw new AiUnavailableException();
                if (shortWindow.Length < options.RequestsPerMinute && shortWindow.Sum(x => x.Tokens) + tokens <= options.TokensPerMinute)
                {
                    var row = new ApiUsage { At = now, Tokens = tokens, Summary = summary }; db.Add(row); await db.SaveChangesAsync(ct); return row.Id;
                }
                if (shortWindow.Length > 0) delay = shortWindow.Min(x => x.At).AddSeconds(61) - now;
            }
            finally { gate.Release(); }
            await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1), ct);
        }
    }
    public async Task Reconcile(long id, int actual, CancellationToken ct)
    {
        if (actual <= 0) return;
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<BotDb>();
        await db.Set<ApiUsage>().Where(x => x.Id == id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Tokens, actual), ct);
    }
}

public sealed class GroqClient(HttpClient http, BotOptions options, AiQuota quota, ILogger<GroqClient> log) : IAiClient
{
    public static Dictionary<string, object> Payload(string model, IReadOnlyList<AiMessage> messages, bool summary)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model, ["messages"] = messages.Select(x => new { role = x.Role, content = x.Content }).ToArray(),
            ["temperature"] = summary ? 0.2 : 0.7, ["max_completion_tokens"] = summary ? 600 : 1500, ["stream"] = false
        };
        if (model.StartsWith("openai/gpt-oss-", StringComparison.Ordinal)) { body["reasoning_effort"] = "low"; body["include_reasoning"] = false; }
        else if (model == "qwen/qwen3.6-27b") { body["reasoning_effort"] = "none"; body["reasoning_format"] = "hidden"; }
        return body;
    }
    public static string Clean(string text)
    {
        text = Regex.Replace(text, @"<think>.*?(</think>|$)", "", RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        var end = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (end >= 0) text = text[(end + 8)..];
        return text.Trim();
    }
    public async Task<AiResult> Complete(IReadOnlyList<AiMessage> messages, bool summary, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct); budget.CancelAfter(TimeSpan.FromSeconds(options.JobBudget));
        var token = budget.Token; var model = options.Model;
        using var slot = await quota.Enter(token);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt == 2) model = options.FallbackModel;
            var reservation = await quota.Reserve(TokenEstimate.Count(messages) + (summary ? 600 : 1500), summary, token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(options.AiTimeout));
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.GroqKey);
                req.Content = JsonContent.Create(Payload(model, messages, summary));
                using var response = await http.SendAsync(req, timeout.Token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new AiUnavailableException();
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(5);
                    log.LogWarning("Groq rate limited; model {Model}", model);
                    await Task.Delay(retry > TimeSpan.Zero ? retry : TimeSpan.FromSeconds(1), token); continue;
                }
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                {
                    using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                    var code = error.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("code", out var c) ? c.GetString() : "";
                    if (code is "model_not_found" or "model_decommissioned" or "model_not_supported" || response.StatusCode == HttpStatusCode.NotFound)
                    { if (attempt < 2) { attempt = 1; continue; } }
                    throw new AiUnavailableException();
                }
                if ((int)response.StatusCode >= 500) { await Task.Delay(500 * (attempt + 1) + Random.Shared.Next(250), token); continue; }
                if (!response.IsSuccessStatusCode) throw new AiUnavailableException();
                using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                var text = Clean(data.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "");
                if (string.IsNullOrWhiteSpace(text)) throw new AiUnavailableException();
                var usage = data.RootElement.TryGetProperty("usage", out var u) && u.TryGetProperty("total_tokens", out var t) ? t.GetInt32() : 0;
                await quota.Reconcile(reservation, usage, token);
                log.LogInformation("Groq response; model {Model}; tokens {Tokens}; summary {Summary}", model, usage, summary);
                return new(text, model, usage);
            }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException && !token.IsCancellationRequested)
            { await Task.Delay(500 + Random.Shared.Next(400), token); }
        }
        throw new AiUnavailableException();
    }
}
