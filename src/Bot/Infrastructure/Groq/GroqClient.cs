using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Trivozhno.Features.Conversation;
using Trivozhno.Host;

namespace Trivozhno.Infrastructure.Groq;

// Plain chat completion. Retries are for HTTP 429/5xx only, on the same model.
public sealed class GroqClient(HttpClient http, BotOptions options, AiQuota quota, ILogger<GroqClient> log) : IAiClient
{
    public async Task<AiResult> Complete(IReadOnlyList<AiMessage> messages, ChatGenerationSettings settings, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(options.JobBudget));
        var token = budget.Token;
        using var slot = await quota.Enter(token);
        var estimate = TokenEstimate.Count(messages);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var reservation = await quota.Reserve(settings.Model, estimate + settings.MaxCompletionTokens, false, token, estimate);
            var settled = false;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.AiTimeout));
                var payload = new Dictionary<string, object?>
                {
                    ["model"] = settings.Model,
                    ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
                    ["temperature"] = settings.Temperature,
                    ["top_p"] = settings.TopP,
                    ["max_completion_tokens"] = settings.MaxCompletionTokens,
                    ["stream"] = false
                };
                if (settings.ReasoningEffort is not null)
                {
                    payload["reasoning_effort"] = settings.ReasoningEffort;
                    if (settings.Model.StartsWith("openai/gpt-oss-", StringComparison.Ordinal))
                        payload["include_reasoning"] = false;
                    else payload["reasoning_format"] = "hidden";
                }
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.GroqKey);
                request.Content = JsonContent.Create(payload);
                log.LogInformation("Groq text request; model {Model}; reasoning {Effort}; input estimate {Input}; output ceiling {Output}; attempt {Attempt}",
                    settings.Model, settings.ReasoningEffort ?? "default", estimate, settings.MaxCompletionTokens, attempt + 1);
                using var response = await http.SendAsync(request, timeout.Token);
                log.LogInformation("Groq HTTP {Status}; model {Model}", (int)response.StatusCode, settings.Model);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    await quota.Release(reservation, token);
                    settled = true;
                    var retry = response.Headers.RetryAfter?.Delta ??
                        (response.Headers.RetryAfter?.Date is { } at ? at - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(2));
                    retry = TimeSpan.FromSeconds(Math.Clamp(retry.TotalSeconds, 1, options.JobBudget));
                    await quota.BackOff(settings.Model, retry, token);
                    if (attempt == 2) throw new AiUnavailableException("rate_limit");
                    continue;
                }
                if ((int)response.StatusCode >= 500)
                {
                    // Provider failure may have consumed tokens; keep its estimate.
                    await quota.Abandon(reservation);
                    settled = true;
                    if (attempt == 2) throw new AiUnavailableException("provider_unavailable");
                    await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    await quota.Release(reservation, token);
                    settled = true;
                    throw new AiUnavailableException(response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized => "authentication",
                        HttpStatusCode.Forbidden => "permission_denied",
                        _ => "request_rejected"
                    });
                }
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                var root = json.RootElement;
                int Count(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
                    value.TryGetProperty(name, out var n) && n.TryGetInt32(out var count) ? Math.Max(0, count) : 0;
                var hasUsage = root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object;
                var prompt = hasUsage ? Count(usage, "prompt_tokens") : 0;
                var completion = hasUsage ? Count(usage, "completion_tokens") : 0;
                var total = hasUsage ? Count(usage, "total_tokens") : estimate + settings.MaxCompletionTokens;
                var reasoning = hasUsage && usage.TryGetProperty("completion_tokens_details", out var outputDetails)
                    ? Count(outputDetails, "reasoning_tokens") : 0;
                var cached = hasUsage && usage.TryGetProperty("prompt_tokens_details", out var inputDetails)
                    ? Count(inputDetails, "cached_tokens") : 0;
                var result = new AiResult("", settings.Model, total, prompt, completion, reasoning, cached);
                if (hasUsage) await quota.Reconcile(reservation, result, token, GroqRateSnapshot.Read(response.Headers));
                else await quota.Abandon(reservation);
                settled = true;
                var choice = root.GetProperty("choices")[0];
                if (choice.GetProperty("finish_reason").GetString() != "stop")
                    throw new AiUnavailableException("incomplete_response");
                var content = choice.GetProperty("message").GetProperty("content").GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(content) || content.Contains("<think>", StringComparison.OrdinalIgnoreCase))
                    throw new AiUnavailableException("invalid_response");
                log.LogInformation("Groq text response; model {Model}; prompt {Prompt}; completion {Completion}; reasoning {Reasoning}; cached {Cached}",
                    settings.Model, prompt, completion, reasoning, cached);
                return result with { Text = content };
            }
            finally
            {
                // Timeout/cancellation is not evidence that the provider did no work.
                if (!settled) await quota.Abandon(reservation);
            }
        }
        throw new AiUnavailableException();
    }
}
