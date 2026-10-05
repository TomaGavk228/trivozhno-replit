using System.Text.Encodings.Web;
using System.Text.Json;
using Trivozhno.Infrastructure.Groq;

namespace Trivozhno.Features.Memory;

public sealed record MemoryUpdate(string Key, string Quote);
public sealed record ChatReply(string Reply, string ConversationState, MemoryUpdate[] MemoryUpdates);

// One completion contains the visible reply and a small continuity update.
// This is a transport contract, not another behavioral prompt or model call.
public static class ChatReplyFormat
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public const string Instruction = """
        Формат API: поверни JSON за схемою. Лише поле reply побачить людина; у ньому звичайна дружня репліка за правилами вище.
        conversation_state: стислий фактичний стан розмови, до 70 слів: тема, поточне прохання, ставлення до порад, остання пропозиція, реакція на неї й незавершене питання. Це довідка для наступного ходу, не міркування чи діагноз. Нове прохання змінює попередній стан; відмова від однієї дії не означає відмову від спілкування.
        memory_updates: зазвичай порожній масив; до 3 нових тривалих фактів або явно висловлених уподобань. key — короткий стабільний ключ латиницею; для виправлення вже відомого факту повтори його ключ. quote — дослівна самодостатня цитата ТІЛЬКИ з останнього справжнього повідомлення людини, до 350 символів, зі збереженням заперечень. Зберігай те, що вона розповіла про себе, важливих людей або свої справи. Тимчасовий настрій належить до conversation_state. Приклади, чужа вставлена переписка, припущення та власна відповідь не є фактами про людину. Не заповнюй пам'ять заради заповнення. Не обіцяй точкове видалення даних: для очищення пам'яті є кнопка в налаштуваннях.
        """;

    public static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type":"object",
          "properties":{
            "reply":{"type":"string"},
            "conversation_state":{"type":"string"},
            "memory_updates":{"type":"array","items":{
              "type":"object",
              "properties":{"key":{"type":"string"},"quote":{"type":"string"}},
              "required":["key","quote"],"additionalProperties":false
            }}
          },
          "required":["reply","conversation_state","memory_updates"],
          "additionalProperties":false
        }
        """).RootElement.Clone();

    public static object ResponseFormat(string model) =>
        model.StartsWith("openai/gpt-oss-", StringComparison.Ordinal) ||
        string.Equals(model, "qwen/qwen3.8-27b", StringComparison.Ordinal)
        ? new { type = "json_schema", json_schema = new { name = "friend_chat", strict = true, schema = Schema } }
        : (object)new { type = "json_object" };

    public static int FormatTokens(string model) => TokenEstimate.Count(JsonSerializer.Serialize(ResponseFormat(model))) + 24;

    // Z.ai JSON mode does not take json_schema; describe the contract in the prompt.
    public static string InstructionFor(string model) => model.StartsWith("glm-", StringComparison.Ordinal)
        ? Instruction + "\nОбов’язкова структура JSON (усі три поля потрібні; memory_updates може бути []):\n" + Schema.GetRawText()
        : Instruction;

    public static ChatReply Parse(string content)
    {
        try
        {
            var result = JsonSerializer.Deserialize<ChatReply>(content, Json);
            if (result is null || string.IsNullOrWhiteSpace(result.Reply) || result.ConversationState is null || result.MemoryUpdates is null)
                throw new AiUnavailableException("invalid_chat_envelope");
            if (result.MemoryUpdates.Any(m => m is null || string.IsNullOrWhiteSpace(m.Key) || string.IsNullOrWhiteSpace(m.Quote)))
                throw new AiUnavailableException("invalid_chat_envelope");
            return result with { Reply = result.Reply.Trim(), ConversationState = Clip(result.ConversationState.Trim(), 900) };
        }
        catch (JsonException) { throw new AiUnavailableException("invalid_chat_envelope"); }
    }

    public static string Clip(string text, int length)
    {
        if (text.Length <= length) return text;
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..Math.Max(0, length)];
    }

    public static string ToTokenBudget(string text, int budget)
    {
        if (budget <= 8) return "";
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (TokenEstimate.Count(text[..mid]) <= budget) low = mid;
            else high = mid - 1;
        }
        return Clip(text, low);
    }
}
