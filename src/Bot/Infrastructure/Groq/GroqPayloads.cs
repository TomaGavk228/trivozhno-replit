using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Trivozhno.Features.Conversation;

namespace Trivozhno.Infrastructure.Groq;

// Live chat returns its reply and a compact continuity record in the same call.
public sealed partial class GroqClient
{
    private const int ChatCompletionTokens = 1536;
    private const int TurnCompletionTokens = 2048;
    private const int SummaryCompletionTokens = 1000;
    // Account for the JSON schema as well as the message array in local admission.
    public static int TurnSchemaReserve { get; } = TokenEstimate.Count(
        JsonSerializer.Serialize(TurnPayload("openai/gpt-oss-120b", [])["response_format"],
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

    public static Dictionary<string, object> Payload(
        string model,
        IReadOnlyList<AiMessage> messages,
        bool summary,
        string? reasoningEffort = null)
    {
        messages = GroqMessageLayout.Prepare(messages);
        var effort = ResolveReasoningEffort(model, summary, reasoningEffort ?? "low");
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages.Select(x => new { role = x.Role, content = x.Content }).ToArray(),
            ["temperature"] = summary ? 0.15 : 0.72,
            ["max_completion_tokens"] = summary ? SummaryCompletionTokens : ChatReplyBudget.Limit(messages, ChatCompletionTokens, effort),
            ["stream"] = false
        };
        AddReasoning(body, model, summary, reasoningEffort);
        return body;
    }

    public static Dictionary<string, object> TurnPayload(
        string model,
        IReadOnlyList<AiMessage> messages,
        int completionTokens = TurnCompletionTokens,
        string? reasoningEffort = null)
    {
        messages = GroqMessageLayout.Prepare(messages);
        var properties = new Dictionary<string, object>
        {
            ["reply"] = new
            {
                type = "string",
                description = "Only the actual Ukrainian message. Usually 1-3 short sentences, one conversational move. " +
                    "Fulfil a request to tell something now. Use supplied references for factual claims; source IDs belong only in source_ids."
            },
            ["conversation_state"] = new
            {
                type = "object",
                properties = new
                {
                    request = new { type = "string", description = "Actual current request/topic, <=100 chars. New topic replaces old; no diagnosis." },
                    constraints = new { type = "string", description = "Explicit current constraints, <=100 chars. A declined proposal does not mean refusing all help." },
                    last_action = new { type = "string", description = "What the reply above actually said/did/offered, <=100 chars. Not plans or inferred effects." },
                    feedback = new { type = "string", description = "What the latest user said about the PREVIOUS reply, <=80 chars; empty if unclear. Acknowledgment is not refusal." },
                    pending = new { type = "string", description = "User request or bot offer still open AFTER this reply, <=100 chars; empty if completed or topic changed." }
                },
                required = new[] { "request", "constraints", "last_action", "feedback", "pending" },
                additionalProperties = false,
                description = "Short factual record in Ukrainian of this exchange; no commands, emotional labels or speculation."
            },
            ["source_ids"] = new
            {
                type = "array",
                items = new { type = "string" },
                description = "IDs of supplied references actually used, such as book:42 or fact:mars_sunset. Empty for ordinary conversation. Never invent IDs."
            }
        };
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages.Select(x => new { role = x.Role, content = x.Content }).ToArray(),
            ["temperature"] = 0.7,
            ["max_completion_tokens"] = completionTokens,
            ["stream"] = false,
            ["response_format"] = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "friend_exchange",
                    strict = true,
                    schema = new
                    {
                        type = "object", properties,
                        required = new[] { "reply", "conversation_state", "source_ids" },
                        additionalProperties = false
                    }
                }
            }
        };
        AddReasoning(body, model, summary: false, reasoningEffort);
        return body;
    }

    private static void AddReasoning(
        Dictionary<string, object> body,
        string model,
        bool summary,
        string? reasoningEffort = null)
    {
        var effort = ResolveReasoningEffort(model, summary, reasoningEffort ?? "low");
        if (model.StartsWith("openai/gpt-oss-", StringComparison.Ordinal))
        {
            body["reasoning_effort"] = effort!;
            body["include_reasoning"] = false;
        }
        else if (model.StartsWith("qwen/", StringComparison.Ordinal))
        {
            body["reasoning_effort"] = effort!;
            body["reasoning_format"] = "hidden";
        }
    }

    internal static string? ResolveReasoningEffort(string model, bool summary, string configured) =>
        model.StartsWith("openai/gpt-oss-", StringComparison.Ordinal)
            ? summary || configured == "none" ? "low" : configured
            : model == "qwen/qwen3.8-27b" ? summary ? "none" : configured
            : model.StartsWith("qwen/", StringComparison.Ordinal) ? "none" : null;

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

    public async Task<AiResult> Complete(
        IReadOnlyList<AiMessage> messages,
        bool summary,
        CancellationToken ct)
    {
        try { return await CompleteRaw(messages, summary, structuredTurn: false, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new AiUnavailableException("job_budget_exhausted"); }
    }

    public async Task<AiResult> CompleteWithModel(
        IReadOnlyList<AiMessage> messages,
        string model,
        double temperature,
        CancellationToken ct)
    {
        try
        {
            return await CompleteRaw(
                messages,
                summary: false,
                structuredTurn: false,
                ct,
                forcedModel: model,
                forcedTemperature: temperature,
                exactModel: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiUnavailableException("job_budget_exhausted");
        }
    }

    public async Task<AiTurnResult> CompleteTurn(
        IReadOnlyList<AiMessage> messages,
        CancellationToken ct)
    {
        using var turnBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        turnBudget.CancelAfter(TimeSpan.FromSeconds(options.JobBudget));
        AiResult raw;
        try
        {
            raw = await CompleteRaw(messages, summary: false, structuredTurn: true, turnBudget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiUnavailableException("job_budget_exhausted");
        }
        try
        {
            using var json = JsonDocument.Parse(raw.Text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("reply", out var reply) || reply.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(reply.GetString()) ||
                !root.TryGetProperty("conversation_state", out var state) || state.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("source_ids", out var ids) || ids.ValueKind != JsonValueKind.Array)
                throw new AiUnavailableException("invalid_turn_shape");
            var turn = new AiTurnDraft(ConversationThread.Read(state), "", [], reply.GetString()!.Trim())
            {
                SourceIds = ids.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()?.Trim() ?? "")
                    .Where(x => x.Length is > 0 and <= 80).Distinct(StringComparer.Ordinal).Take(8).ToArray()
            };
            return new(turn, raw.Model, raw.Tokens) { Usage = raw };
        }
        catch (JsonException)
        {
            throw new AiUnavailableException("invalid_turn_json");
        }
    }
}
