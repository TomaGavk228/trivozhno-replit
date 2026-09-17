using System.Text.Json;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Features.Memory;

namespace Trivozhno.Features.Dialogue;

public sealed record ConversationTurn(ConversationState State, string Reply);
public sealed record ConversationAnswer(string Text, ConversationState State, string Model, int Tokens);
public interface IConversationModel
{
    Task<ConversationAnswer> Reply(ConversationContext context, CancellationToken ct);
}
public sealed class ConversationModel(IAiClient ai) : IConversationModel
{
    public async Task<ConversationAnswer> Reply(ConversationContext context, CancellationToken ct)
    {
        var raw = await ai.CompleteStructured(context.Messages, context.OutputTokens, ct);
        var turn = TurnContract.Parse(raw.Text);
        return new(turn.Reply, FeedbackPolicy.Merge(context.State, turn.State), raw.Model, raw.Tokens);
    }
}

public static class TurnContract
{
    // State is generated before the user-visible reply, allowing current feedback to guide the same turn.
    public const string Instructions = """
        Поверни JSON зі state і reply. state — короткі спостереження, не міркування: topic (тема), need (потреба),
        tone, questions (normal/avoid), advice (ask_first/requested/avoid), lastAction (твоя дія),
        feedback (важливе побажання користувача), openThread (незавершене), length (brief/short/detailed).
        Онови state за поточною реплікою до написання reply. Поля тексту — до 180 символів; невідоме — порожній рядок.
        Дотримуйся свіжого feedback вже в reply. questions=avoid зберігай до явного дозволу знову питати;
        advice=avoid — до нового прохання поради. requested і detailed стосуються поточного запиту.
        Питання — лише якщо допомагає саме зараз; враховуй останні дії, не повторюй одну стратегію.
        При зміні теми онови topic; закриту тему прибери з openThread. Минулу тему повертай за бажанням користувача.
        reply — готовий текст для Telegram, зазвичай 1–3 короткі речення до 500 символів, length=short — до 220.
        Коли людина просить пояснення/план, дай достатньо змісту, за потреби до 3000 символів.
        """;
    public static object ResponseFormat()
    {
        object Text() => new { type = "string" };
        object Choice(params string[] values) => new Dictionary<string, object> { ["type"] = "string", ["enum"] = values };
        var fields = new Dictionary<string, object>
        {
            ["topic"] = Text(), ["need"] = Text(), ["tone"] = Text(),
            ["questions"] = Choice("normal", "avoid"), ["advice"] = Choice("ask_first", "requested", "avoid"),
            ["lastAction"] = Text(), ["feedback"] = Text(), ["openThread"] = Text(), ["length"] = Choice("brief", "short", "detailed")
        };
        return new { type = "json_schema", json_schema = new { name = "conversation_turn", strict = true, schema = new
        {
            type = "object", properties = new
            {
                state = new { type = "object", properties = fields, required = fields.Keys.ToArray(), additionalProperties = false },
                reply = Text()
            },
            required = new[] { "state", "reply" }, additionalProperties = false
        } } };
    }
    public static ConversationTurn Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
                !root.TryGetProperty("reply", out var reply) || reply.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object || state.EnumerateObject().Count() != 9)
                throw new AiUnavailableException();
            foreach (var name in new[] { "topic", "need", "tone", "questions", "advice", "lastAction", "feedback", "openThread", "length" })
                if (!state.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String || field.GetString()!.Length > 180)
                    throw new AiUnavailableException();
            var turn = JsonSerializer.Deserialize<ConversationTurn>(json, DialogueJson.Options)!;
            var clean = GroqClient.Clean(turn.Reply);
            if (string.IsNullOrWhiteSpace(clean) || clean.Length > 6000 ||
                turn.State.Questions is not ("normal" or "avoid") || turn.State.Advice is not ("ask_first" or "requested" or "avoid") ||
                turn.State.Length is not ("brief" or "short" or "detailed")) throw new AiUnavailableException();
            return turn with { Reply = clean };
        }
        catch (JsonException) { throw new AiUnavailableException(); }
    }
}
