using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Trivozhno.Features.Conversation;
using Trivozhno.Features.Memory;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Persistence;

namespace Trivozhno.Infrastructure.Gemini;

// Shared by transient HTTP clients. Provider cooldown also applies to later jobs.
public sealed class GeminiRequestGate(BotOptions options, IClock clock)
{
    private readonly SemaphoreSlim slots = new(options.GeminiConcurrency, options.GeminiConcurrency);
    private readonly object sync = new();
    private DateTimeOffset blockedUntil;

    public async Task<IDisposable> Enter(CancellationToken ct)
    {
        await slots.WaitAsync(ct);
        return new Slot(slots);
    }

    public void Defer(TimeSpan wait)
    {
        lock (sync)
        {
            var until = clock.UtcNow + wait;
            if (until > blockedUntil) blockedUntil = until;
        }
    }

    public async Task WaitUntilReady(CancellationToken ct)
    {
        while (true)
        {
            TimeSpan delay;
            lock (sync) delay = blockedUntil - clock.UtcNow;
            if (delay <= TimeSpan.Zero) return;
            await Task.Delay(delay, ct);
        }
    }

    private sealed class Slot(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}

// Native Google AI Studio API: one schema-constrained generation for reply + memory.
public sealed class GeminiClient(HttpClient http, BotOptions options, GeminiRequestGate gate,
    IServiceScopeFactory scopes, IClock clock, ILogger<GeminiClient> log) : IAiClient
{
    public async Task<AiResult> Complete(IReadOnlyList<AiMessage> messages, ChatGenerationSettings settings, CancellationToken ct)
    {
        options.ValidateModel(settings.Model);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(options.JobBudget));
        var token = budget.Token;
        var watch = Stopwatch.StartNew();
        using var slot = await gate.Enter(token);
        var slotMs = watch.ElapsedMilliseconds;
        var thinking = settings.ReasoningEffort is null or "none" or "default" ? "MINIMAL" : settings.ReasoningEffort.ToUpperInvariant();
        var estimate = TokenEstimate.Count(messages) + ChatReplyFormat.FormatTokens(settings.Model);
        // Google uses one systemInstruction and user/model roles in contents.
        if (messages.Any(m => m.Role is not ("system" or "user" or "assistant")) || !messages.Any(m => m.Role == "user"))
            throw new AiUnavailableException("invalid_request");
        var contents = new List<Content>();
        foreach (var message in messages.Where(m => m.Role != "system"))
        {
            var role = message.Role == "assistant" ? "model" : "user";
            // An unanswered turn may leave two adjacent user messages. Preserve
            // their order as separate parts instead of creating ambiguous chat turns.
            if (contents.Count > 0 && contents[^1].Role == role) contents[^1].Parts.Add(new(message.Content));
            else contents.Add(new(role, [new(message.Content)]));
        }
        var payload = new
        {
            systemInstruction = new { parts = messages.Where(m => m.Role == "system").Select(m => new { text = m.Content }).ToArray() },
            contents,
            generationConfig = new
            {
                temperature = settings.Temperature, topP = settings.TopP,
                maxOutputTokens = settings.MaxCompletionTokens, candidateCount = 1,
                thinkingConfig = new { thinkingLevel = thinking, includeThoughts = false },
                responseFormat = ChatReplyFormat.GeminiResponseFormat()
            },
            store = false
        };
        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{settings.Model}:generateContent";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            watch.Restart();
            await gate.WaitUntilReady(token);
            var cooldownMs = watch.ElapsedMilliseconds;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.AiTimeout));
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            // Never put the key in the URL, payload or logs.
            request.Headers.Add("x-goog-api-key", options.GeminiKey);
            request.Content = JsonContent.Create(payload);
            log.LogInformation("Gemini text request; model {Model}; thinking {Thinking}; format JSON schema; input estimate {Input}; output ceiling {Output}; attempt {Attempt}",
                settings.Model, thinking, estimate, settings.MaxCompletionTokens, attempt + 1);
            watch.Restart();
            using var response = await http.SendAsync(request, timeout.Token);
            log.LogInformation("Gemini timing; model {Model}; slot wait {SlotMs} ms; cooldown wait {CooldownMs} ms; HTTP {HttpMs} ms; status {Status}",
                settings.Model, slotMs, cooldownMs, watch.ElapsedMilliseconds, (int)response.StatusCode);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var (providerWait, daily) = await ReadQuotaError(response, timeout.Token);
                if (daily) throw new AiUnavailableException("daily_quota");
                var retry = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date is { } at ? at - clock.UtcNow : providerWait ?? TimeSpan.FromSeconds(2 * (attempt + 1)));
                retry = TimeSpan.FromSeconds(Math.Clamp(retry.TotalSeconds, 1, 86400));
                gate.Defer(retry);
                log.LogWarning("Gemini rate limited; retry after {Seconds} seconds", retry.TotalSeconds);
                if (attempt == 2 || retry.TotalSeconds >= options.JobBudget) throw new AiUnavailableException("rate_limit");
                continue;
            }
            if ((int)response.StatusCode >= 500)
            {
                if (attempt == 2) throw new AiUnavailableException("provider_unavailable");
                await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var category = await ReadErrorCategory(response, timeout.Token);
                log.LogWarning("Gemini rejected request; status {Status}; category {Category}", (int)response.StatusCode, category);
                throw new AiUnavailableException(category is "API_KEY_INVALID" or "API_KEY_EXPIRED" ? "authentication" : response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "authentication",
                    HttpStatusCode.Forbidden => "permission_denied",
                    HttpStatusCode.NotFound => "model_unavailable",
                    _ => "request_rejected"
                });
            }

            JsonDocument json;
            try { json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token)); }
            catch (JsonException) { throw new AiUnavailableException("invalid_response"); }
            using (json)
            {
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new AiUnavailableException("invalid_response");
                var hasUsage = root.TryGetProperty("usageMetadata", out var usage) && usage.ValueKind == JsonValueKind.Object;
                var prompt = hasUsage ? Count(usage, "promptTokenCount") : 0;
                var visible = hasUsage ? Count(usage, "candidatesTokenCount") : 0;
                var reasoning = hasUsage ? Count(usage, "thoughtsTokenCount") : 0;
                var cached = hasUsage ? Count(usage, "cachedContentTokenCount") : 0;
                var total = hasUsage && usage.TryGetProperty("totalTokenCount", out _) ? Count(usage, "totalTokenCount") :
                    hasUsage ? prompt + visible + reasoning : estimate + settings.MaxCompletionTokens;
                var result = new AiResult("", settings.Model, total, prompt, visible + reasoning, reasoning, cached);
                await RecordUsage(result, token);
                log.LogInformation("Gemini text response; model {Model}; prompt {Prompt}; visible {Visible}; reasoning {Reasoning}; cached {Cached}; usage reported {Reported}",
                    settings.Model, prompt, visible, reasoning, cached, hasUsage);
                if (root.TryGetProperty("promptFeedback", out var feedback) && feedback.ValueKind == JsonValueKind.Object &&
                    feedback.TryGetProperty("blockReason", out var block) && block.ValueKind == JsonValueKind.String &&
                    block.GetString() is not (null or "" or "BLOCK_REASON_UNSPECIFIED"))
                    throw new AiUnavailableException("provider_blocked");
                if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
                    throw new AiUnavailableException("invalid_response");
                var candidate = candidates[0];
                if (candidate.ValueKind != JsonValueKind.Object || !candidate.TryGetProperty("finishReason", out var finish) || finish.ValueKind != JsonValueKind.String)
                    throw new AiUnavailableException("invalid_response");
                var reason = finish.GetString();
                if (reason != "STOP")
                    throw new AiUnavailableException(reason is "SAFETY" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "RECITATION"
                        ? "provider_blocked" : "incomplete_response");
                if (!candidate.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object ||
                    !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
                    throw new AiUnavailableException("invalid_response");
                var answer = string.Concat(parts.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object &&
                    !(p.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True))
                    .Select(p => p.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : "")).Trim();
                if (string.IsNullOrWhiteSpace(answer) || answer.Contains("<think>", StringComparison.OrdinalIgnoreCase))
                    throw new AiUnavailableException("invalid_response");
                // Native schema is enforced by Google. No plain-text/repair fallback.
                var reply = ChatReplyFormat.Parse(answer);
                return result with { Text = reply.Reply, ConversationState = reply.ConversationState, MemoryUpdates = reply.MemoryUpdates };
            }
        }
        throw new AiUnavailableException();
    }

    private sealed record TextPart(string Text);
    private sealed record Content(string Role, List<TextPart> Parts);

    private static async Task<string> ReadErrorCategory(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
                return "unknown";
            if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                foreach (var item in details.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
                    if (item.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String &&
                        reason.GetString() is "API_KEY_INVALID" or "API_KEY_EXPIRED") return reason.GetString()!;
            var status = error.TryGetProperty("status", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            // Fixed allowlist: do not log free-form provider messages or request content.
            return status is "INVALID_ARGUMENT" or "FAILED_PRECONDITION" or "PERMISSION_DENIED" or "UNAUTHENTICATED" or "NOT_FOUND"
                ? status : "unknown";
        }
        catch (JsonException) { return "unknown"; }
    }

    private static int Count(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var count)
        ? Math.Max(0, count) : 0;

    private static async Task<(TimeSpan? Wait, bool Daily)> ReadQuotaError(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object ||
                !error.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Array) return (null, false);
            TimeSpan? wait = null;
            var daily = false;
            foreach (var item in details.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
            {
                if (item.TryGetProperty("retryDelay", out var delay) && delay.ValueKind == JsonValueKind.String &&
                    delay.GetString() is { } duration && duration.EndsWith('s') &&
                    double.TryParse(duration[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds))
                    wait = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 86400));
                if (item.TryGetProperty("violations", out var violations) && violations.ValueKind == JsonValueKind.Array)
                    foreach (var violation in violations.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
                        if (violation.TryGetProperty("quotaId", out var id) && id.ValueKind == JsonValueKind.String &&
                            id.GetString() is { } name && (name.Contains("PerDay", StringComparison.OrdinalIgnoreCase) || name.Contains("daily", StringComparison.OrdinalIgnoreCase)))
                            daily = true;
            }
            return (wait, daily);
        }
        catch (JsonException) { return (null, false); }
    }

    private async Task RecordUsage(AiResult result, CancellationToken ct)
    {
        // Provider telemetry only. Groq's old daily reservations do not apply.
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDb>();
            db.Add(new ApiUsage
            {
                At = clock.UtcNow, Model = "google/" + result.Model, Tokens = result.Tokens,
                PromptTokens = result.PromptTokens, CompletionTokens = result.CompletionTokens,
                ReasoningTokens = result.ReasoningTokens, CachedTokens = result.CachedTokens
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Gemini usage recording unavailable: {Category}", e.GetType().Name);
        }
    }
}
