using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Trivozhno.Features.Memory;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;

namespace Trivozhno.Infrastructure.Groq;

public sealed record AiMessage(string Role, string Content);
public sealed record AiResult(
    string Text,
    string Model,
    int Tokens,
    int PromptTokens = 0,
    int CompletionTokens = 0,
    int ReasoningTokens = 0,
    int CachedTokens = 0);
public sealed record AiTurnDraft(
    string ConversationState,
    string KnowledgeQuery,
    IReadOnlyList<string> ProfileDelta,
    string Reply);
public sealed record AiTurnResult(AiTurnDraft Turn, string Model, int Tokens);
public interface IAiClient
{
    Task<AiResult> Complete(IReadOnlyList<AiMessage> messages, bool summary, CancellationToken ct);
    Task<AiTurnResult> CompleteTurn(IReadOnlyList<AiMessage> messages, CancellationToken ct);
}
public sealed class AiUnavailableException : Exception { }
public sealed class ContextTooLargeException : Exception { }

public static class TokenEstimate
{
    // Fast conservative estimate used only for local admission control.
    public static int Count(string text) => (Encoding.UTF8.GetByteCount(text) + 2) / 3 + 8;
    public static int Count(IEnumerable<AiMessage> messages) => messages.Sum(m => Count(m.Content) + 8);
}

public sealed class ApiUsage
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string Model { get; set; } = "";
    public int Tokens { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int ReasoningTokens { get; set; }
    public int CachedTokens { get; set; }
    public bool Summary { get; set; }
}

public sealed class AiQuota(IServiceScopeFactory scopes, BotOptions options, IClock clock)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim slots = new(options.AiConcurrency, options.AiConcurrency);

    public async Task<IDisposable> Enter(CancellationToken ct)
    {
        await slots.WaitAsync(ct);
        return new Slot(slots);
    }

    private sealed class Slot(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }

    public async Task<long> Reserve(string model, int tokens, bool summary, CancellationToken ct)
    {
        if (tokens > options.TokensPerMinute || tokens > options.TokensPerDay)
            throw new ContextTooLargeException();

        while (true)
        {
            var delay = TimeSpan.FromSeconds(1);
            await gate.WaitAsync(ct);
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BotDb>();
                var now = clock.UtcNow;
                var minute = now.AddMinutes(-1);
                var day = now.AddDays(-1);

                // Background memory never competes with a live queued/processing reply.
                if (summary && await db.Messages.AnyAsync(
                        x => x.Status == "queued" || x.Status == "processing", ct))
                    throw new AiUnavailableException();

                // Groq limits are model-scoped. Do not make gpt-oss summary traffic consume
                // the local Qwen bucket.
                var recent = await db.Set<ApiUsage>()
                    .Where(x => x.Model == model && x.At > day)
                    .ToListAsync(ct);
                var shortWindow = recent.Where(x => x.At > minute).ToArray();

                if (recent.Count >= options.RequestsPerDay ||
                    recent.Sum(x => x.Tokens) + tokens > options.TokensPerDay)
                    throw new AiUnavailableException();

                if (shortWindow.Length < options.RequestsPerMinute &&
                    shortWindow.Sum(x => x.Tokens) + tokens <= options.TokensPerMinute)
                {
                    var row = new ApiUsage
                    {
                        At = now,
                        Model = model,
                        Tokens = tokens,
                        Summary = summary
                    };
                    db.Add(row);
                    await db.SaveChangesAsync(ct);
                    return row.Id;
                }

                if (shortWindow.Length > 0)
                    delay = shortWindow.Min(x => x.At).AddSeconds(61) - now;
            }
            finally
            {
                gate.Release();
            }

            await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1), ct);
        }
    }

    public async Task Reconcile(long id, AiResult usage, CancellationToken ct)
    {
        if (usage.Tokens <= 0) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDb>();
        await db.Set<ApiUsage>().Where(x => x.Id == id).ExecuteUpdateAsync(x => x
            .SetProperty(y => y.Tokens, usage.Tokens)
            .SetProperty(y => y.PromptTokens, usage.PromptTokens)
            .SetProperty(y => y.CompletionTokens, usage.CompletionTokens)
            .SetProperty(y => y.ReasoningTokens, usage.ReasoningTokens)
            .SetProperty(y => y.CachedTokens, usage.CachedTokens), ct);
    }
}

public sealed class GroqClient(
    HttpClient http,
    BotOptions options,
    AiQuota quota,
    ILogger<GroqClient> log) : IAiClient
{
    private const int ChatCompletionTokens = 320;
    private const int TurnCompletionTokens = 360;
    private const int SummaryCompletionTokens = 300;

    public static Dictionary<string, object> Payload(
        string model,
        IReadOnlyList<AiMessage> messages,
        bool summary)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages.Select(x => new { role = x.Role, content = x.Content }).ToArray(),
            ["temperature"] = summary ? 0.15 : 0.72,
            ["max_completion_tokens"] = summary ? SummaryCompletionTokens : ChatCompletionTokens,
            ["stream"] = false
        };
        AddReasoning(body, model, structuredTurn: false, summary);
        return body;
    }

    public static Dictionary<string, object> TurnPayload(
        string model,
        IReadOnlyList<AiMessage> messages)
    {
        var allowedProfile = ChatStyleProfile.AllowedValues.ToArray();
        var properties = new Dictionary<string, object>
        {
            ["conversation_state"] = new
            {
                type = "string",
                maxLength = 240,
                description = "Коротко: поточна потреба/тема, реакція на попередній хід і що не повторювати."
            },
            ["knowledge_query"] = new
            {
                type = "string",
                maxLength = 120,
                description = "Український запит до психологічної книги лише коли потрібен перевірений психологічний факт/спосіб. Інакше ''. Якщо не порожній — reply має бути ''."
            },
            ["profile_delta"] = new
            {
                type = "array",
                maxItems = 3,
                uniqueItems = true,
                items = new { type = "string", @enum = allowedProfile },
                description = "Лише стійкі або прямо висловлені уподобання стилю. Не роби висновок з одного випадкового повідомлення."
            },
            ["reply"] = new
            {
                type = "string",
                maxLength = 900,
                description = "Фінальна природна репліка чату. Порожня лише якщо knowledge_query непорожній."
            }
        };

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new[] { "conversation_state", "knowledge_query", "profile_delta", "reply" },
            ["additionalProperties"] = false
        };

        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages.Select(x => new { role = x.Role, content = x.Content }).ToArray(),
            ["temperature"] = 0.72,
            ["max_completion_tokens"] = TurnCompletionTokens,
            ["stream"] = false,
            ["response_format"] = new
            {
                type = "json_schema",
                json_schema = new { name = "chat_turn", strict = true, schema }
            }
        };
        AddReasoning(body, model, structuredTurn: true, summary: false);
        return body;
    }

    private static void AddReasoning(
        Dictionary<string, object> body,
        string model,
        bool structuredTurn,
        bool summary)
    {
        if (model.StartsWith("openai/gpt-oss-", StringComparison.Ordinal))
        {
            body["reasoning_effort"] = summary ? "low" : "low";
            body["include_reasoning"] = false;
        }
        else if (model.StartsWith("qwen/", StringComparison.Ordinal))
        {
            // Groq recommends none for efficient general-purpose dialogue.
            // The chatbot should react naturally, not spend hidden reasoning on "привіт".
            body["reasoning_effort"] = "none";
        }
    }

    public static string Clean(string text)
    {
        text = Regex.Replace(
            text,
            @"<think>.*?(</think>|$)",
            "",
            RegexOptions.Singleline | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
        var end = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (end >= 0) text = text[(end + 8)..];
        return text.Trim();
    }

    public Task<AiResult> Complete(
        IReadOnlyList<AiMessage> messages,
        bool summary,
        CancellationToken ct)
        => CompleteRaw(messages, summary, structuredTurn: false, ct);

    public async Task<AiTurnResult> CompleteTurn(
        IReadOnlyList<AiMessage> messages,
        CancellationToken ct)
    {
        var raw = await CompleteRaw(messages, summary: false, structuredTurn: true, ct);
        using var json = JsonDocument.Parse(raw.Text);
        var root = json.RootElement;
        var delta = root.GetProperty("profile_delta").EnumerateArray()
            .Select(x => x.GetString() ?? "")
            .Where(x => x.Length > 0)
            .ToArray();

        var turn = new AiTurnDraft(
            root.GetProperty("conversation_state").GetString()?.Trim() ?? "",
            root.GetProperty("knowledge_query").GetString()?.Trim() ?? "",
            delta,
            root.GetProperty("reply").GetString()?.Trim() ?? "");

        if (string.IsNullOrWhiteSpace(turn.Reply) &&
            string.IsNullOrWhiteSpace(turn.KnowledgeQuery))
            throw new AiUnavailableException();

        return new(turn, raw.Model, raw.Tokens);
    }

    private async Task<AiResult> CompleteRaw(
        IReadOnlyList<AiMessage> messages,
        bool summary,
        bool structuredTurn,
        CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(options.JobBudget));
        var token = budget.Token;
        var model = summary ? options.SummaryModel : options.Model;

        using var slot = await quota.Enter(token);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt == 2) model = options.FallbackModel;

            var completionTokens = summary
                ? SummaryCompletionTokens
                : structuredTurn ? TurnCompletionTokens : ChatCompletionTokens;
            var reservation = await quota.Reserve(
                model,
                TokenEstimate.Count(messages) + completionTokens,
                summary,
                token);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.AiTimeout));

            try
            {
                using var req = new HttpRequestMessage(
                    HttpMethod.Post,
                    "https://api.groq.com/openai/v1/chat/completions");
                req.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", options.GroqKey);
                req.Content = JsonContent.Create(
                    structuredTurn
                        ? TurnPayload(model, messages)
                        : Payload(model, messages, summary));

                using var response = await http.SendAsync(req, timeout.Token);
                LogRateHeaders(response, model);

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new AiUnavailableException();

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retry =
                        response.Headers.RetryAfter?.Delta ??
                        (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ??
                        TimeSpan.FromSeconds(5);
                    log.LogWarning("Groq rate limited; model {Model}", model);
                    await Task.Delay(
                        retry > TimeSpan.Zero ? retry : TimeSpan.FromSeconds(1),
                        token);
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

                    if (structuredTurn &&
                        response.StatusCode == HttpStatusCode.BadRequest &&
                        model.StartsWith("qwen/", StringComparison.Ordinal) &&
                        !string.Equals(model, options.FallbackModel, StringComparison.Ordinal))
                    {
                        log.LogWarning(
                            "Qwen structured turn rejected (code {Code}); retrying with fallback {FallbackModel}",
                            code ?? "unknown",
                            options.FallbackModel);
                        model = options.FallbackModel;
                        attempt = 1;
                        continue;
                    }

                    if (code is "model_not_found" or "model_decommissioned" or "model_not_supported" ||
                        response.StatusCode == HttpStatusCode.NotFound)
                    {
                        if (attempt < 2)
                        {
                            attempt = 1;
                            continue;
                        }
                    }

                    throw new AiUnavailableException();
                }

                if ((int)response.StatusCode >= 500)
                {
                    await Task.Delay(
                        500 * (attempt + 1) + Random.Shared.Next(250),
                        token);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new AiUnavailableException();

                using var data = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(timeout.Token));
                var text = Clean(
                    data.RootElement
                        .GetProperty("choices")[0]
                        .GetProperty("message")
                        .GetProperty("content")
                        .GetString() ?? "");
                if (string.IsNullOrWhiteSpace(text))
                    throw new AiUnavailableException();

                var usage = ParseUsage(data.RootElement, text, model);
                await quota.Reconcile(reservation, usage, token);

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
                await Task.Delay(500 + Random.Shared.Next(400), token);
            }
        }

        throw new AiUnavailableException();
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
