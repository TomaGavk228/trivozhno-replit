namespace Trivozhno.Features.Conversation;

public sealed record ReplyQualityResult(bool Accept, string Feedback);

// Only structural failures need a second model call. Voice and choice of
// words are evaluated in dialogue, not rejected by brittle keyword rules.
public static class ReplyQualityGate
{
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
