using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Trivozhno.Features.Conversation;
using Trivozhno.Features.Memory;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Persistence;

namespace Trivozhno.Infrastructure.Zai;

// Shared across transient HTTP clients. A 429 cooldown also applies to the next job.
public sealed class ZaiRequestGate(BotOptions options, IClock clock)
{
    private readonly SemaphoreSlim slots = new(options.ZaiConcurrency, options.ZaiConcurrency);
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

// One Z.ai completion returns reply, conversation state and long-term memory updates.
public sealed class ZaiClient(HttpClient http, BotOptions options, ZaiRequestGate gate,
    IServiceScopeFactory scopes, IClock clock, ILogger<ZaiClient> log) : IAiClient
{
    private const string Endpoint = "https://api.z.ai/api/paas/v4/chat/completions";

    public async Task<AiResult> Complete(IReadOnlyList<AiMessage> messages, ChatGenerationSettings settings, CancellationToken ct)
    {
        options.ValidateModel(settings.Model);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(options.JobBudget));
        var token = budget.Token;
        var watch = Stopwatch.StartNew();
        using var slot = await gate.Enter(token);
        var slotMs = watch.ElapsedMilliseconds;
        var thinking = settings.ReasoningEffort is not (null or "none");
        var estimate = TokenEstimate.Count(messages) + ChatReplyFormat.FormatTokens(settings.Model);
        var payload = new
        {
            model = settings.Model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            temperature = settings.Temperature,
            top_p = settings.TopP,
            max_tokens = settings.MaxCompletionTokens,
            thinking = new { type = thinking ? "enabled" : "disabled" },
            response_format = new { type = "json_object" },
            stream = false
        };

        for (var attempt = 0; attempt < 3; attempt++)
        {
            watch.Restart();
            await gate.WaitUntilReady(token);
            var cooldownMs = watch.ElapsedMilliseconds;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.AiTimeout));
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ZaiKey);
            request.Content = JsonContent.Create(payload);
            log.LogInformation("Z.ai text request; model {Model}; thinking {Thinking}; input estimate {Input}; output ceiling {Output}; attempt {Attempt}",
                settings.Model, thinking, estimate, settings.MaxCompletionTokens, attempt + 1);
            watch.Restart();
            using var response = await http.SendAsync(request, timeout.Token);
            log.LogInformation("Z.ai timing; model {Model}; slot wait {SlotMs} ms; cooldown wait {CooldownMs} ms; HTTP {HttpMs} ms; status {Status}",
                settings.Model, slotMs, cooldownMs, watch.ElapsedMilliseconds, (int)response.StatusCode);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date is { } at ? at - clock.UtcNow : TimeSpan.FromSeconds(2 * (attempt + 1)));
                retry = TimeSpan.FromSeconds(Math.Clamp(retry.TotalSeconds, 1, 86400));
                gate.Defer(retry);
                log.LogWarning("Z.ai rate limited; retry after {Seconds} seconds", retry.TotalSeconds);
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
                // Log only a numeric provider error code, never its body/message or key.
                var code = "unknown";
                try
                {
                    using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                    var value = error.RootElement;
                    if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("error", out var nested)) value = nested;
                    if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("code", out var errorCode) &&
                        int.TryParse(errorCode.ToString(), out var numeric)) code = numeric.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                catch (JsonException) { }
                log.LogWarning("Z.ai rejected request; status {Status}; code {Code}", (int)response.StatusCode, code);
                throw new AiUnavailableException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "authentication",
                    HttpStatusCode.Forbidden => "permission_denied",
                    _ => "request_rejected"
                });
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var root = json.RootElement;
            static int Count(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty(name, out var number) && number.TryGetInt32(out var count) ? Math.Max(0, count) : 0;
            var hasUsage = root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object;
            var prompt = hasUsage ? Count(usage, "prompt_tokens") : 0;
            var completion = hasUsage ? Count(usage, "completion_tokens") : 0;
            var total = hasUsage && usage.TryGetProperty("total_tokens", out _) ? Count(usage, "total_tokens") :
                hasUsage ? prompt + completion : estimate + settings.MaxCompletionTokens;
            var cached = hasUsage && usage.TryGetProperty("prompt_tokens_details", out var inputDetails)
                ? Count(inputDetails, "cached_tokens") : 0;
            var reasoning = hasUsage && usage.TryGetProperty("completion_tokens_details", out var outputDetails)
                ? Count(outputDetails, "reasoning_tokens") : 0;
            var result = new AiResult("", settings.Model, total, prompt, completion, reasoning, cached);
            await RecordUsage(result, token);

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw new AiUnavailableException("invalid_response");
            var choice = choices[0];
            if (!choice.TryGetProperty("finish_reason", out var finish) || finish.GetString() != "stop")
                throw new AiUnavailableException("incomplete_response");
            if (!choice.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var contentValue) || contentValue.ValueKind != JsonValueKind.String)
                throw new AiUnavailableException("invalid_response");
            var content = contentValue.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(content) || content.Contains("<think>", StringComparison.OrdinalIgnoreCase))
                throw new AiUnavailableException("invalid_response");
            log.LogInformation("Z.ai text response; model {Model}; prompt {Prompt}; completion {Completion}; reasoning {Reasoning}; cached {Cached}; usage reported {Reported}",
                settings.Model, prompt, completion, reasoning, cached, hasUsage);
            ChatReply reply;
            var shape = "invalid_json";
            try
            {
                reply = ChatReplyFormat.ParseZai(content, out shape, out var metadataIgnored);
                if (metadataIgnored)
                    log.LogWarning("Z.ai chat reply accepted without some metadata; {Shape}", shape);
            }
            catch (AiUnavailableException)
            {
                log.LogWarning("Z.ai invalid chat envelope; {Shape}", shape);
                throw;
            }
            return result with { Text = reply.Reply, ConversationState = reply.ConversationState, MemoryUpdates = reply.MemoryUpdates };
        }
        throw new AiUnavailableException();
    }

    private async Task RecordUsage(AiResult result, CancellationToken ct)
    {
        // Telemetry only: no invented daily quota, no reuse of Groq's persisted reservations.
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDb>();
            db.Add(new ApiUsage
            {
                At = clock.UtcNow, Model = "zai/" + result.Model, Tokens = result.Tokens,
                PromptTokens = result.PromptTokens, CompletionTokens = result.CompletionTokens,
                ReasoningTokens = result.ReasoningTokens, CachedTokens = result.CachedTokens
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Z.ai usage recording unavailable: {Category}", e.GetType().Name);
        }
    }
}
