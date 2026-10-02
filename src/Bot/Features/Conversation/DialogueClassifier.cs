using System.Text.RegularExpressions;

namespace Trivozhno.Features.Conversation;

public enum DialogueAct
{
    Greeting,
    Sharing,
    Advice,
    Refusal,
    ShortReply,
    Question,
    Goodbye
}

// Local, model-free reading of what the latest message DOES in the dialogue.
// It never labels emotions or disorders; it only picks which manner hint applies.
public static class DialogueClassifier
{
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(100);

    public static DialogueAct Classify(string? current)
    {
        var text = (current ?? "").Trim();
        // A turn gathered from several quick messages is judged by its last line,
        // unless the whole text is more specific.
        var last = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? text;
        var whole = ClassifyLine(text);
        return whole != DialogueAct.Sharing || last == text ? whole : ClassifyLine(last);
    }

    private static DialogueAct ClassifyLine(string text)
    {
        var lower = text.ToLowerInvariant();

        if (Regex.IsMatch(lower, @"^(привіт|вітаю|хай|хей|добрий день|доброго (ранку|вечора)|здоров)[!. )]*$",
                RegexOptions.CultureInvariant, Limit))
            return DialogueAct.Greeting;

        if (Regex.IsMatch(lower, @"^(бувай|пака|пока|до побачення|на добраніч|гарних снів|йду спати)[!. )]*$",
                RegexOptions.CultureInvariant, Limit))
            return DialogueAct.Goodbye;

        if (Regex.IsMatch(lower,
                @"^(що|шо) (мені )?робити\b|^порадь\b|^підкажи\b|^(допоможи|допоможіть|поможи)\b|^як (мені )?позбутися\b|^як (мені )?(впоратися|впоратись|заспокоїтися|заспокоїтись|перестати)\b",
                RegexOptions.CultureInvariant, Limit))
            return DialogueAct.Advice;

        if (Regex.IsMatch(lower,
                @"^((ні|нє|ну)?[, ]*(не хочу( нічого( робити)?)?|нічого не хочу( робити)?|не буду|не треба|не хочу цього)|досить|ні|нє)[!. ,)(]*$",
                RegexOptions.CultureInvariant, Limit))
            return DialogueAct.Refusal;

        if (Regex.IsMatch(lower, @"^((та|ну)\s+)?(не знаю|хз|ніяка|ніяк|погано|фігово|так собі|таке собі|такe собі|таке|нічого)[!. )(]*$",
                RegexOptions.CultureInvariant, Limit))
            return DialogueAct.ShortReply;

        if (text.EndsWith('?') || Regex.IsMatch(lower,
                @"^(що|шо|чому|чого|як|де|коли|навіщо|скільки|хто)\b",
                RegexOptions.CultureInvariant, Limit))
            return DialogueAct.Question;

        return DialogueAct.Sharing;
    }
}
