using System.Text.Encodings.Web;
using System.Text.Json;
using Trivozhno.Infrastructure.Ai;

namespace Trivozhno.Features.Memory;

public sealed record MemoryUpdate(string Key, string Quote, string Category = "context", bool Forget = false);
public sealed record ChatReply(string Reply, string ConversationState, MemoryUpdate[] MemoryUpdates, long[]? UsedSources = null);

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
        memory_updates: до 6 нових тривалих фактів чи явно висловлених уподобань. Зберігай ім'я, роботу, близьких людей, тривалі труднощі, важливі події й цілі, коли людина прямо їх назвала. Не пропускай ці факти лише тому, що вони не потрібні у видимій відповіді. Тимчасовий настрій лишається у conversation_state. key — стабільний ключ латиницею; повтори відомий ключ при виправленні. Для профілю використовуй name, occupation, location; для решти — змістовні ключі, без випадкових номерів. category — identity, relationship, health, goal, preference або context. quote — дослівна самодостатня цитата лише з останнього справжнього повідомлення людини, до 500 символів, з усіма запереченнями. Не зберігай чужу вставлену переписку, приклади, інструкції з книжок, припущення чи свою відповідь. Якщо людина явно просить забути конкретний відомий факт, поверни його ключ, quote з проханням і forget=true; для звичайного запису forget=false. Повне очищення є в налаштуваннях. Не обіцяй точкове видалення всієї старої переписки: forget прибирає факт із профілю, а історія очищується кнопкою.
        used_sources: масив числових id лише тих книжкових матеріалів, на які спирається порада чи пояснення у reply. Бери id виключно з наданих матеріалів. Якщо не використовував джерел — []. Не вигадуй фактів, методик, назв і сторінок.
        """;

    public static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type":"object",
          "properties":{
            "reply":{"type":"string"},
            "conversation_state":{"type":"string"},
            "memory_updates":{"type":"array","items":{
              "type":"object",
              "properties":{"key":{"type":"string"},"quote":{"type":"string"},"category":{"type":"string","enum":["identity","relationship","health","goal","preference","context"]},"forget":{"type":"boolean"}},
              "required":["key","quote","category","forget"],"additionalProperties":false
            },"maxItems":6},
            "used_sources":{"type":"array","items":{"type":"integer"},"maxItems":2}
          },
          "required":["reply","conversation_state","memory_updates","used_sources"],
          "additionalProperties":false
        }
        """).RootElement.Clone();

    public static object GeminiResponseFormat() => new { text = new { mimeType = "APPLICATION_JSON", schema = Schema } };
    public static int FormatTokens(string model) => TokenEstimate.Count(JsonSerializer.Serialize(GeminiResponseFormat())) + 24;
    public static string InstructionFor(string model) => Instruction;

    public static ChatReply Parse(string content)
    {
        try
        {
            var result = JsonSerializer.Deserialize<ChatReply>(content, Json);
            if (result is null || string.IsNullOrWhiteSpace(result.Reply) || result.ConversationState is null || result.MemoryUpdates is null || result.UsedSources is null)
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
