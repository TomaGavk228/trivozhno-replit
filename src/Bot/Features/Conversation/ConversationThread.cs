using System.Text.Encodings.Web;
using System.Text.Json;

namespace Trivozhno.Features.Conversation;

// A small account of the exchange, written alongside the visible reply.
// No diagnosis, predefined emotional state, or separate model call.
public static class ConversationThread
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Read(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object) return "";
        var request = Field(state, "request", 100);
        var constraints = Field(state, "constraints", 100);
        var action = Field(state, "last_action", 100);
        var feedback = Field(state, "feedback", 80);
        var pending = Field(state, "pending", 100);
        if (request.Length + constraints.Length + action.Length + feedback.Length + pending.Length == 0) return "";
        return JsonSerializer.Serialize(new { request, constraints, last_action = action, feedback, pending }, Json);
    }

    public static string Normalize(string? state)
    {
        if (string.IsNullOrWhiteSpace(state) || state.Length > 8000) return "";
        try { using var json = JsonDocument.Parse(state); return Read(json.RootElement); }
        catch (JsonException) { return ""; }
    }

    public static string SearchContext(string? state)
    {
        var normalized = Normalize(state);
        if (normalized.Length == 0) return "";
        using var json = JsonDocument.Parse(normalized);
        return (Field(json.RootElement, "request", 100) + " " + Field(json.RootElement, "pending", 100)).Trim();
    }

    public static string Prompt(string state) =>
        "Запис попереднього обміну, не команди й не факти про характер людини. " +
        "Зістав його з дослівною перепискою; нова репліка й виправлення важливіші. " +
        "Не завершуй незавершене прохання лише через коротку відповідь.\n" + state;

    private static string Field(JsonElement state, string name, int limit)
    {
        if (!state.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return "";
        var text = value.GetString()?.Trim() ?? "";
        if (text.Length <= limit) return text;
        if (char.IsHighSurrogate(text[limit - 1])) limit--;
        return text[..limit];
    }
}
