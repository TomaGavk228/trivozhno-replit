using System.Text.RegularExpressions;

namespace Trivozhno.Features.Conversation;

public sealed record ReplyQualityResult(bool Accept, string Feedback);

// Deliberately thin. This used to be a word-blacklist censor (canned-advice
// stems, "unsupported physiology" phrases, imperative-verb counts, cliche
// phrases, stemmed context-anchor matching) that rejected natural replies for
// sounding too plain or too much like normal texting -- exactly the opposite
// of what a "talks like a friend" bot needs. The actual voice/style contract
// lives in chat-v1.txt and is enforced by the model, not by regex here.
// This gate only catches structural failures the prompt can't self-correct:
// an empty draft, a wall of text in a 1-2 sentence chat, or a dead silence
// right after the person opened up or set a boundary.
public static class ReplyQualityGate
{
    private static readonly Regex DeadEnd = new(
        @"(?i)^(добре|ок|окей|понятно|зрозуміло|угу|ага|ясно)[.! )]*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private const int MaxWords = 120;

    public static ReplyQualityResult Check(
        DialogueAct act,
        string current,
        string reply,
        IReadOnlyList<string>? recentUser = null)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return Fail("Сформулюй одну завершену коротку репліку.");

        var text = reply.Trim();

        if (WordCount(text) > MaxWords)
            return Fail("Це занадто довго для звичайного чату. Скороти до 1–3 коротких речень.");

        if (act is DialogueAct.Refusal or DialogueAct.ShortReply && DeadEnd.IsMatch(text))
            return Fail("Не обривай діалог сухим підтвердженням. Додай одну маленьку думку з поточної теми, щоб самому нести розмову далі.");

        return Pass();
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
            "\nСформулюй іншу репліку з нуля. Використовуй тільки факти з переписки. Не пояснюй виправлення." +
            "\nЧернетка, яку НЕ треба повторювати: " + draft.Trim();
    }

    public static string EmergencyFallback(DialogueAct act) => act switch
    {
        DialogueAct.Greeting => "Привіт)",
        DialogueAct.Refusal => "Окей, тоді без цього.",
        DialogueAct.Goodbye => "Добраніч)",
        _ => "Я зараз невдало сформулював відповідь. Давай без цієї репліки."
    };

    private static int WordCount(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static ReplyQualityResult Pass() => new(true, "");
    private static ReplyQualityResult Fail(string feedback) => new(false, feedback);
}
