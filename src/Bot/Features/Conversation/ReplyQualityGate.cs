using System.Text.RegularExpressions;

namespace Trivozhno.Features.Conversation;

public sealed record ReplyQualityResult(bool Accept, string Feedback);

// Structure only: a draft is rejected when it is not shaped like a chat message
// (list, essay, interrogation, canned opener, repeat of the previous reply).
// Whether it is warm or funny is the prompt's and the examples' job, never a keyword list's.
public static class ReplyQualityGate
{
    private const int MaxWords = 75;
    private const int MaxWordsLong = 160;
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(150);

    private const string CannedOpener =
        @"^\s*(я\s+)?розумію\b|^\s*дякую,?\s+що\s+(поділи|розпов|написа|довір)|^\s*це\s+(цілком\s+)?нормально\b|" +
        @"^\s*мені\s+(дуже\s+)?шкода\b|^\s*звучить\s+(так,?\s+)?(ніби|як)\b|^\s*схоже,?\s+що\s+ти\b|" +
        @"^\s*як\s+(штучний|ші|мовна)\b|^\s*звичайно[!,.]|^\s*безумовно\b";

    public static ReplyQualityResult Check(
        DialogueAct act,
        string current,
        string reply,
        IReadOnlyList<string>? recentUser = null,
        IReadOnlyList<string>? recentAssistant = null,
        bool allowLong = false,
        bool crisis = false)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return Fail("Сформулюй одну завершену коротку репліку.");

        var text = reply.Trim();
        var words = WordCount(text);

        if (words > (allowLong ? MaxWordsLong : crisis ? 110 : MaxWords))
            return Fail("Це занадто довго для чату. Скороти до 1–3 коротких речень, одна головна думка.");

        if (Regex.IsMatch(text, @"(?m)^\s*(?:[-*•]\s+|\d+[.)]\s+|#{1,6}\s)", RegexOptions.CultureInvariant, Limit))
            return Fail("Без списків, нумерації й заголовків: пиши звичайними короткими реченнями, як у месенджері.");

        if (!crisis && Regex.IsMatch(text, CannedOpener, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit))
            return Fail("Не починай з готової фрази співчуття чи «розумію». Одразу відреагуй на конкретне, що сказала людина.");

        var questions = text.Count(c => c == '?');
        if (questions > (crisis ? 3 : 1))
            return Fail("Забагато питань підряд. Залиш щонайбільше одне, або жодного.");

        if (act is DialogueAct.ShortReply or DialogueAct.Refusal && questions > 0 && words > 30)
            return Fail("Людина відповіла коротко. Не допитуй: коротко відреагуй і залиш їй простір.");

        if (recentAssistant is { Count: > 0 } && Overlap(text, recentAssistant) >= 0.6)
            return Fail("Це майже повторює одну з твоїх попередніх реплік. Скажи іншими словами і з нової сторони.");

        return Pass();
    }

    // Deterministic cleanup that never changes the meaning: markdown, wrapping quotes, long dashes.
    public static string Polish(string text)
    {
        var t = (text ?? "").Trim();
        t = Regex.Replace(t, @"\*\*(.+?)\*\*", "$1", RegexOptions.CultureInvariant, Limit);
        t = Regex.Replace(t, @"(?m)^#{1,6}\s*", "", RegexOptions.CultureInvariant, Limit);
        t = t.Replace("\u00A0", " ").Replace(" — ", " - ").Replace("—", "-");
        if (t.Length > 1 && (t[0] == '"' && t[^1] == '"' || t[0] == '«' && t[^1] == '»'))
            t = t[1..^1].Trim();
        t = Regex.Replace(t, @"\n{3,}", "\n\n", RegexOptions.CultureInvariant, Limit);
        return t;
    }

    public static string RetryInstruction(
        ReplyQualityResult result,
        string draft,
        DialogueAct act,
        string current,
        IReadOnlyList<string>? recentUser = null)
    {
        recentUser ??= [];
        var context = string.Join("\n", recentUser.TakeLast(4).Select(x => "- " + x));

        return
            "Попередня чернетка не підходить для цього ходу. " + result.Feedback +
            "\nДія цього ходу: " + act + "." +
            "\nОстання репліка: " + current +
            (context.Length > 0 ? "\nОстанні репліки людини:\n" + context : "") +
            "\nСформулюй іншу репліку з нуля, як близький друг у месенджері. Використовуй тільки факти з переписки. Не пояснюй виправлення." +
            "\nЧернетка, яку НЕ треба повторювати: " + draft.Trim();
    }

    public static string EmergencyFallback(DialogueAct act) => act switch
    {
        DialogueAct.Greeting => "Привіт)",
        DialogueAct.Refusal => "Окей, тоді без цього.",
        DialogueAct.Goodbye => "Добраніч)",
        _ => "Я зараз невдало сформулював відповідь. Давай без цієї репліки."
    };

    // Share of this reply's word 4-grams already present in recent assistant replies.
    private static double Overlap(string text, IReadOnlyList<string> previous)
    {
        var grams = Grams(text);
        if (grams.Count < 3) return 0;
        var old = previous.SelectMany(Grams).ToHashSet(StringComparer.Ordinal);
        return grams.Count(old.Contains) / (double)grams.Count;
    }

    private static List<string> Grams(string text)
    {
        var words = Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+", RegexOptions.CultureInvariant, Limit)
            .Select(m => m.Value).Take(400).ToArray();
        var grams = new List<string>();
        for (var i = 0; i + 4 <= words.Length; i++) grams.Add(string.Join(' ', words, i, 4));
        return grams;
    }

    private static int WordCount(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static ReplyQualityResult Pass() => new(true, "");
    private static ReplyQualityResult Fail(string feedback) => new(false, feedback);
}
