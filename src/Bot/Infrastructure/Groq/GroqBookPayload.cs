using System.Text.Json;

namespace Trivozhno.Infrastructure.Groq;

public sealed partial class GroqClient
{
    public static object BookResponseFormat { get; } = new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "book_reply", strict = true,
            schema = new
            {
                type = "object",
                properties = new
                {
                    kind = new { type = "string", @enum = new[] { "answer", "clarify", "insufficient", "conversation" } },
                    reply = new { type = "string", description = "The natural Ukrainian reply, in your own words. No source markers or copied book passages." },
                    source_ids = new { type = "array", items = new { type = "integer" }, description = "IDs of supplied book passages actually supporting this reply. Empty for clarification or ordinary conversation." }
                },
                required = new[] { "kind", "reply", "source_ids" }, additionalProperties = false
            }
        }
    };

    public static int BookSchemaReserve { get; } = TokenEstimate.Count(JsonSerializer.Serialize(BookResponseFormat));

    public async Task<AiResult> CompleteBook(IReadOnlyList<AiMessage> messages, CancellationToken ct)
    {
        try { return await CompleteRaw(messages, false, false, ct, bookReply: true); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new AiUnavailableException("job_budget_exhausted"); }
    }
}
