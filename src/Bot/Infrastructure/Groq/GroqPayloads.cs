using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Trivozhno.Features.Memory;

namespace Trivozhno.Infrastructure.Groq;

// Structured-turn API retained for existing callers; live chat uses plain Complete.
public sealed partial class GroqClient
{
    private const int ChatCompletionTokens = 1800;
    private const int TurnCompletionTokens = 1800;
    private const int SummaryCompletionTokens = 1000;
    // Account for the JSON schema as well as the message array in local admission.
    public static int TurnSchemaReserve { get; } = TokenEstimate.Count(
        JsonSerializer.Serialize(TurnPayload("openai/gpt-oss-120b", [])["response_format"],
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

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
        IReadOnlyList<AiMessage> messages,
        int completionTokens = TurnCompletionTokens)
    {
        var allowedProfile = ChatStyleProfile.AllowedValues.ToArray();
        var properties = new Dictionary<string, object>
        {
            ["conversation_state"] = new
            {
                type = "object",
                properties = new
                {
                    request = new { type = "string", description = "Current user request, <=100 chars." },
                    constraints = new { type = "string", description = "User refinements to this request, <=160 chars." },
                    last_action = new { type = "string", description = "What your reply does, <=100 chars." },
                    feedback = new { type = "string", description = "What user accepted/rejected about the previous response, <=160 chars. No speculation." },
                    pending = new { type = "string", description = "Still unfulfilled AFTER your reply, <=100 chars; otherwise empty." }
                },
                required = new[] { "request", "constraints", "last_action", "feedback", "pending" },
                additionalProperties = false,
                description = "Brief Ukrainian working notes, not instructions or diagnoses. Empty for unknown. Latest request supersedes stale state."
            },
            ["knowledge_query"] = new
            {
                type = "string",
                description = "Ukrainian book search only for psychological facts/methods. Usually empty. If nonempty, reply must be empty."
            },
            ["profile_delta"] = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        value = new { type = "string", @enum = allowedProfile },
                        evidence = new { type = "string", description = "Exact quote from latest user message explicitly requesting a general style preference." }
                    },
                    required = new[] { "value", "evidence" },
                    additionalProperties = false
                },
                description = "Usually []. Only explicit general style requests. A rejected suggestion/story, mood or brief acknowledgment is NOT a persistent preference."
            },
            ["reply"] = new
            {
                type = "string",
                description = "Complete natural Ukrainian reply. Empty only while requesting knowledge."
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
            ["temperature"] = 0.7,
            ["max_completion_tokens"] = completionTokens,
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
            // Live conversation must follow the user's meaning and previous refusals.
            // Keep summary/legacy costs unchanged; evaluate this setting by manual chat.
            body["reasoning_effort"] = summary || structuredTurn ? "low" : "medium";
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

    public async Task<AiResult> Complete(
        IReadOnlyList<AiMessage> messages,
        bool summary,
        CancellationToken ct)
    {
        try { return await CompleteRaw(messages, summary, structuredTurn: false, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new AiUnavailableException("job_budget_exhausted"); }
    }

    public async Task<AiTurnResult> CompleteTurn(
        IReadOnlyList<AiMessage> messages,
        CancellationToken ct)
    {
        using var turnBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        turnBudget.CancelAfter(TimeSpan.FromSeconds(options.JobBudget));
        ct = turnBudget.Token;
        AiResult raw;
        try
        {
            raw = await CompleteRaw(messages, summary: false, structuredTurn: true, ct);
        }
        catch (AiUnavailableException e) when (e.Reason == "json_validate_failed")
        {
            var fallback = await CompleteRaw(messages.Append(new AiMessage("system",
                "Відповідай тільки звичайним текстом, без JSON і службових полів.")).ToArray(),
                summary: false, structuredTurn: false, ct);
            return new(new AiTurnDraft("", "", [], fallback.Text), fallback.Model, fallback.Tokens);
        }
        using var json = JsonDocument.Parse(raw.Text);
        var root = json.RootElement;
        var currentUserText = messages.LastOrDefault(x => x.Role == "user")?.Content ?? "";
        var delta = root.GetProperty("profile_delta").EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.Object)
            .Where(x =>
            {
                var evidence = x.GetProperty("evidence").GetString()?.Trim() ?? "";
                return evidence.Length >= 4 &&
                    currentUserText.Contains(evidence, StringComparison.OrdinalIgnoreCase);
            })
            .Select(x => x.GetProperty("value").GetString() ?? "")
            .Where(x => ChatStyleProfile.AllowedValues.Contains(x))
            .Take(3)
            .ToArray();

        var state = root.GetProperty("conversation_state");
        var stateJson = JsonSerializer.Serialize(new
        {
            request = Bound(state.GetProperty("request").GetString(), 100),
            constraints = Bound(state.GetProperty("constraints").GetString(), 160),
            last_action = Bound(state.GetProperty("last_action").GetString(), 100),
            feedback = Bound(state.GetProperty("feedback").GetString(), 160),
            pending = Bound(state.GetProperty("pending").GetString(), 100)
        }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var turn = new AiTurnDraft(
            stateJson,
            Bound(root.GetProperty("knowledge_query").GetString(), 200),
            delta,
            root.GetProperty("reply").GetString()?.Trim() ?? "");

        if (string.IsNullOrWhiteSpace(turn.Reply) &&
            string.IsNullOrWhiteSpace(turn.KnowledgeQuery))
            throw new AiUnavailableException();

        return new(turn, raw.Model, raw.Tokens);
    }

    private static string Bound(string? value, int max)
    {
        var text = value?.Trim() ?? "";
        if (text.Length <= max) return text;
        var length = max;
        if (char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }

}
