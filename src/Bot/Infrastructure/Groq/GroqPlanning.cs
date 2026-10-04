using System.Text.Encodings.Web;
using System.Text.Json;

namespace Trivozhno.Infrastructure.Groq;

public sealed partial class GroqClient
{
    public const int PlanCompletionTokens = 900;
    public static int PlanSchemaReserve { get; } = TokenEstimate.Count(JsonSerializer.Serialize(
        PlanPayload("openai/gpt-oss-20b", [])["response_format"],
        new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

    public static Dictionary<string, object> PlanPayload(string model, IReadOnlyList<AiMessage> messages)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = GroqMessageLayout.Prepare(messages).Select(x => new { role = x.Role, content = x.Content }).ToArray(),
            ["temperature"] = 0.2,
            ["max_completion_tokens"] = PlanCompletionTokens,
            ["stream"] = false,
            ["response_format"] = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "friend_turn_plan", strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            request = new { type = "string", description = "What the user currently wants, in context. Ukrainian, <=100 characters." },
                            move = new { type = "string", description = "One concrete next conversational action responding to THIS exchange; not the actual reply. Ukrainian, <=160 characters." },
                            avoid = new { type = "string", description = "Specific rejected or already failed approach to avoid repeating; empty when none. Ukrainian, <=100 characters." },
                            book_mode = new
                            {
                                type = "string", @enum = new[] { "none", "search", "continue", "alternative", "source" },
                                description = "Books: none for ordinary chat; search for requested psychological help or explanation; continue to explain a previous method; alternative for another method; source for its origin."
                            },
                            book_query = new { type = "string", description = "Ukrainian search topic and need, 2-8 meaningful words. Empty if no book lookup. Do not search for the user's short acknowledgment or refusal literally." }
                        },
                        required = new[] { "request", "move", "avoid", "book_mode", "book_query" },
                        additionalProperties = false
                    }
                }
            }
        };
        AddReasoning(body, model, summary: false, reasoningEffort: "low");
        return body;
    }

    public async Task<AiPlanResult?> PlanTurn(IReadOnlyList<AiMessage> messages, CancellationToken ct)
    {
        // One bounded planning attempt. No fallback model, repair loop or memory call.
        var raw = await CompleteRaw(messages, summary: false, structuredTurn: true, ct,
            forcedModel: options.PlannerModel, exactModel: true, planning: true);
        try
        {
            using var json = JsonDocument.Parse(raw.Text);
            var root = json.RootElement;
            var mode = Read("book_mode", 20);
            if (mode is not ("none" or "search" or "continue" or "alternative" or "source"))
                throw new AiUnavailableException("invalid_plan_shape");
            var request = Read("request", 100);
            var move = Read("move", 160);
            if (request.Length == 0 || move.Length == 0) throw new AiUnavailableException("empty_plan");
            return new(new(request, move, Read("avoid", 100), mode, Read("book_query", 220)), raw);

            string Read(string name, int limit)
            {
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value) ||
                    value.ValueKind != JsonValueKind.String) throw new AiUnavailableException("invalid_plan_shape");
                var text = value.GetString()!.Trim();
                if (text.Length <= limit) return text;
                if (char.IsHighSurrogate(text[limit - 1])) limit--;
                return text[..limit];
            }
        }
        catch (JsonException) { throw new AiUnavailableException("invalid_plan_json"); }
    }
}
