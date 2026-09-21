using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Trivozhno.Host;

namespace Trivozhno.Infrastructure.Groq;

public sealed partial class GroqClient(
    HttpClient http,
    BotOptions options,
    AiQuota quota,
    ILogger<GroqClient> log) : IAiClient
{
    private async Task<AiResult> CompleteRaw(
        IReadOnlyList<AiMessage> messages,
        bool summary,
        bool structuredTurn,
        CancellationToken ct)
    {
        // Normalize before quota accounting, payload generation and request logs.
        // Payload builders also normalize for direct callers; Prepare is idempotent.
        var originalInstructionBlocks = messages.Count(m => m.Role is "system" or "developer");
        messages = GroqMessageLayout.Prepare(messages);
        log.LogInformation("Groq layout; instruction blocks {Before} -> {After}; instruction chars {Chars}; last role {LastRole}",
            originalInstructionBlocks, messages.Count(m => m.Role == "system"),
            messages.FirstOrDefault(m => m.Role == "system")?.Content.Length ?? 0,
            messages.LastOrDefault()?.Role ?? "none");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(options.JobBudget));
        var token = budget.Token;
        var model = summary ? options.SummaryModel : options.Model;
        var briefChat = !summary && !structuredTurn && !ChatReplyBudget.WantsDetail(messages);
        var completionTokens = summary
            ? SummaryCompletionTokens
            : structuredTurn ? options.TurnOutputBudget : ChatReplyBudget.Limit(messages, options.TurnOutputBudget);
        var lengthRetried = false;

        var admission = Stopwatch.StartNew();
        using var slot = await quota.Enter(token);
        log.LogInformation("Groq slot wait {ElapsedMs} ms", admission.ElapsedMilliseconds);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt == 2) model = options.FallbackModel;

            admission.Restart();
            var inputEstimate = TokenEstimate.Count(messages) + (structuredTurn ? TurnSchemaReserve : 0);
            var reservation = await quota.Reserve(
                model,
                inputEstimate + completionTokens,
                summary,
                token,
                inputEstimate);

            log.LogInformation("Groq quota admission {ElapsedMs} ms", admission.ElapsedMilliseconds);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.AiTimeout));

            try
            {
                using var req = new HttpRequestMessage(
                    HttpMethod.Post,
                    "https://api.groq.com/openai/v1/chat/completions");
                req.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", options.GroqKey);
                var payload = structuredTurn
                    ? TurnPayload(model, messages, completionTokens)
                    : Payload(model, messages, summary);
                payload["max_completion_tokens"] = completionTokens;
                req.Content = JsonContent.Create(payload);
                log.LogInformation("Groq request; model {Model}; reasoning {Effort}; output budget {Budget}; roles {Roles}; last user chars {UserChars}",
                    model, payload.GetValueOrDefault("reasoning_effort") ?? "default",
                    completionTokens,
                    string.Join(',', messages.Select(m => m.Role)),
                    messages.LastOrDefault(m => m.Role == "user")?.Content.Length ?? 0);

                var network = Stopwatch.StartNew();
                using var response = await http.SendAsync(req, timeout.Token);
                log.LogInformation("Groq HTTP {Status}; model {Model}; elapsed {ElapsedMs} ms", (int)response.StatusCode, model, network.ElapsedMilliseconds);
                LogRateHeaders(response, model);
                var rateSnapshot = GroqRateSnapshot.Read(response.Headers);

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    await quota.Reconcile(reservation, new("", model, 0), token, rateSnapshot);
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                        throw new AiUnavailableException("authentication");
                    var reason = "permission_denied";
                    try
                    {
                        using var denied = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                        if (denied.RootElement.ValueKind == JsonValueKind.Object &&
                            denied.RootElement.TryGetProperty("error", out var error) &&
                            error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) &&
                            code.ValueKind == JsonValueKind.String)
                            reason = code.GetString() switch
                            {
                                "model_permission_blocked_org" => "model_permission_blocked_org",
                                "model_permission_blocked_project" => "model_permission_blocked_project",
                                _ => "permission_denied"
                            };
                    }
                    catch (JsonException) { /* A non-JSON 403 is still an access denial. */ }
                    log.LogWarning("Groq access denied; model {Model}; reason {Reason}", model, reason);
                    // Do not retry another model to work around a permission denial.
                    throw new AiUnavailableException(reason);
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retry =
                        response.Headers.RetryAfter?.Delta ??
                        (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ??
                        TimeSpan.FromSeconds(5);
                    await quota.BackOff(model, retry, token);
                    await quota.Reconcile(reservation, new("", model, 0), token, rateSnapshot);
                    log.LogWarning("Groq rate limited; model {Model}", model);
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                {
                    var body = await response.Content.ReadAsStringAsync(timeout.Token);
                    using var error = JsonDocument.Parse(body);
                    var code =
                        error.RootElement.TryGetProperty("error", out var e) &&
                        e.TryGetProperty("code", out var c)
                            ? c.GetString()
                            : "";

                    // Validation failures may have generated billable tokens. Use reported
                    // usage when present, otherwise keep the conservative reservation.
                    if (code == "json_validate_failed")
                    {
                        var used = ParseUsage(error.RootElement, "", model);
                        if (used.Tokens > 0) await quota.Reconcile(reservation, used, token, rateSnapshot);
                        log.LogWarning("Groq rejected generated JSON; model {Model}", model);
                        throw new AiUnavailableException("json_validate_failed");
                    }
                    await quota.Reconcile(reservation, new("", model, 0), token, rateSnapshot);
                    if (code is "model_not_found" or "model_decommissioned" or "model_not_supported" ||
                        response.StatusCode == HttpStatusCode.NotFound)
                    {
                        if (attempt < 2 && model != options.FallbackModel)
                        {
                            attempt = 1;
                            continue;
                        }
                    }
                    throw new AiUnavailableException("request_rejected");
                }

                if ((int)response.StatusCode >= 500)
                {
                    // A server failure can occur after inference; do not erase unknown usage.
                    await Task.Delay(
                        500 * (attempt + 1) + Random.Shared.Next(250),
                        token);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new AiUnavailableException("http_error");

                using var data = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(timeout.Token));
                var choice = data.RootElement.GetProperty("choices")[0];
                var text = Clean(
                    choice.GetProperty("message")
                        .GetProperty("content")
                        .GetString() ?? "");

                var usage = ParseUsage(data.RootElement, text, model);
                if (usage.Tokens > 0) await quota.Reconcile(reservation, usage, token, rateSnapshot);
                var finishReason = choice.TryGetProperty("finish_reason", out var finish)
                    ? finish.GetString() : null;
                if (finishReason == "length")
                {
                    if (briefChat && !lengthRetried && attempt < 2)
                    {
                        // Retry only an unfinished response, once, with the SAME allowance.
                        // Never turn a short advice request into a larger generation.
                        lengthRetried = true;
                        messages = ChatReplyBudget.CompactRetry(messages);
                        log.LogWarning("Incomplete brief reply; compact retry with unchanged budget {Budget}", completionTokens);
                        continue;
                    }
                    var room = Math.Min(options.TokensPerMinute, options.TokensPerDay) -
                        TokenEstimate.Count(messages) - (structuredTurn ? TurnSchemaReserve : 0);
                    var larger = Math.Min(3600, Math.Min(room, completionTokens + 1200));
                    if (!summary && !briefChat && !lengthRetried && attempt < 2 && larger > completionTokens)
                    {
                        // Regenerate from the same conversation, never send partial JSON/text.
                        // Normal successful turns still use a single generation.
                        lengthRetried = true;
                        completionTokens = larger;
                        log.LogWarning("Incomplete Groq turn; retrying once with budget {Budget}", larger);
                        continue;
                    }
                    throw new AiUnavailableException("output_truncated");
                }
                if (finishReason != "stop" || string.IsNullOrWhiteSpace(text))
                    throw new AiUnavailableException("empty_or_unfinished_output");

                log.LogInformation(
                    "Groq response; model {Model}; total {Total}; prompt {Prompt}; completion {Completion}; reasoning {Reasoning}; cached {Cached}; summary {Summary}; structuredTurn {StructuredTurn}",
                    model,
                    usage.Tokens,
                    usage.PromptTokens,
                    usage.CompletionTokens,
                    usage.ReasoningTokens,
                    usage.CachedTokens,
                    summary,
                    structuredTurn);
                return usage;
            }
            catch (Exception e) when (
                e is HttpRequestException ||
                e is TaskCanceledException && !token.IsCancellationRequested)
            {
                log.LogWarning("Groq transport failure; attempt {Attempt}; category {Category}", attempt + 1, e.GetType().Name);
                await Task.Delay(500 + Random.Shared.Next(400), token);
            }
            catch (JsonException)
            {
                throw new AiUnavailableException("invalid_response_json");
            }
            finally
            {
                // Reconciled requests are already removed. Failed/cancelled calls
                // retain conservative usage, but must not become permanent holds.
                await quota.Abandon(reservation);
            }
        }

        throw new AiUnavailableException("retries_exhausted");
    }

    private static AiResult ParseUsage(JsonElement root, string text, string model)
    {
        if (!root.TryGetProperty("usage", out var usage))
            return new(text, model, 0);

        var total = ReadInt(usage, "total_tokens");
        var prompt = ReadInt(usage, "prompt_tokens");
        var completion = ReadInt(usage, "completion_tokens");
        var reasoning = 0;
        var cached = 0;

        if (usage.TryGetProperty("completion_tokens_details", out var completionDetails))
            reasoning = ReadInt(completionDetails, "reasoning_tokens");
        if (usage.TryGetProperty("prompt_tokens_details", out var promptDetails))
            cached = ReadInt(promptDetails, "cached_tokens");

        return new(text, model, total, prompt, completion, reasoning, cached);
    }

    private static int ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result)
            ? result
            : 0;

    private void LogRateHeaders(HttpResponseMessage response, string model)
    {
        string? Header(string name) =>
            response.Headers.TryGetValues(name, out var values)
                ? values.FirstOrDefault()
                : null;

        var remainingTokens = Header("x-ratelimit-remaining-tokens");
        var resetTokens = Header("x-ratelimit-reset-tokens");
        var remainingRequests = Header("x-ratelimit-remaining-requests");

        if (remainingTokens is not null || remainingRequests is not null)
            log.LogInformation(
                "Groq limits; model {Model}; remainingTokens {RemainingTokens}; resetTokens {ResetTokens}; remainingRequests {RemainingRequests}",
                model,
                remainingTokens ?? "?",
                resetTokens ?? "?",
                remainingRequests ?? "?");
    }
}
