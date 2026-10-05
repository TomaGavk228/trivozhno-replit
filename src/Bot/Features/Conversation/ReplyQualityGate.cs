using System.Text.RegularExpressions;

namespace Trivozhno.Features.Conversation;

public sealed record ReplyQualityResult(bool Accept, string Feedback);

// Only structural failures cost a second model call: reply too long for a chat
// turn, an unrequested exercise/lecture, a menu of offers, a closing formula
// when the person did not say goodbye, decorative emoji, or a list. The wording
// and voice of the reply are never rewritten by code.
public static class ReplyQualityGate
{
    private const int MaxWordsChat = 60;
    private const int MaxWordsWhenAsked = 150;
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(100);

    // The person explicitly asks for help, a method, an explanation or a story.
    private static readonly Regex AsksForHelp = new(
        @"порад|допоможи|допомож|що робити|шо робити|як бути|як (мені )?(поборот|подолат|впорат|позбут|заспоко|перестат|справлят)|" +
        @"вправ|технік|метод|поясни|розкажи|докладн|детальн|план\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);

    private static readonly Regex AsksForFactOrStory = new(
        @"факт|цікавинк|історі|розкажи|анекдот|жарт",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);

    // A consultation script that nobody asked for.
    private static readonly Regex Unrequested = new(
        @"вдихн|видихн|на \d+\s*рахун|рахунк\w+\s+\d|дихальн|психолог|психотерапевт|терапевт|ресурс\w*|план дій|зверн\w+ (до|по) (фахів|спеціаліст)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);

    // A menu where the person has to choose or invent the topic.
    private static readonly Regex Menu = new(
        @"(ти|сам|сама) (вирішуєш|вибираєш|обираєш|решаєш)|твій вибір|на твій вибір|що (обереш|вибереш)|" +
        @"якщо (хочеш|захочеш|треба|потрібно|бажаєш),? (я )?(можу|давай)|можу (розповісти|поділитися|підказати|запропонувати|підібрати)|" +
        @"розкажу (тобі )?(якусь|щось|цікав|кумедн|легк)|кумедн\w+ факт|цікав\w+ факт|цікавинк",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);

    // Closing formulas in the final sentence.
    private static readonly Regex Closing = new(
        @"(бажаю (тобі )?гарного|гарного (дня|вечора)|на все добре|будь здоров|звертайся|пиши, якщо|дай знати|я (тут|поруч|поряд)|побуду (поруч|поряд)|завжди (тут|поруч|поряд))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);

    private static readonly Regex Emoji = new(
        @"[\u2600-\u27BF]|\uD83C[\uDC00-\uDFFF]|\uD83D[\uDC00-\uDFFF]|\uD83E[\uDC00-\uDFFF]",
        RegexOptions.CultureInvariant, Limit);

    private static readonly Regex ListLine = new(
        @"^\s*(\d+[.)]|[-•*])\s+\S",
        RegexOptions.Multiline | RegexOptions.CultureInvariant, Limit);

    public static ReplyQualityResult Check(
        DialogueAct act,
        string current,
        string reply,
        IReadOnlyList<string>? recentUser = null)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return Fail("Сформулюй одну завершену коротку репліку.");

        var text = reply.Trim();
        current ??= "";
        recentUser ??= [];
        var userText = string.Join("\n", recentUser.TakeLast(4)) + "\n" + current;
        var asksHelp = act == DialogueAct.Advice || AsksForHelp.IsMatch(current);
        var asksFact = AsksForFactOrStory.IsMatch(current);

        if (WordCount(text) > (asksHelp || asksFact ? MaxWordsWhenAsked : MaxWordsChat))
            return Fail("Це надто довго для звичайної переписки. Відповідь має бути на одну думку, 1–3 коротких речення.");

        if (!asksHelp && Unrequested.IsMatch(text))
            return Fail("Людина не просила вправ, технік чи порад фахівця. Відгукнись на її слова своїми словами, " +
                "без дихальних вправ, психолога, ресурсів чи плану дій.");

        if (!asksFact && Menu.IsMatch(text))
            return Fail("Не пропонуй меню на вибір і не обіцяй цікавинок чи історій. " +
                "Зроби крок сам: відреагуй на сказане або підхопи тему й скажи щось своє.");

        if (act != DialogueAct.Goodbye && ClosesConversation(text))
            return Fail("Людина не прощалася. Не завершуй розмову й не пиши «дай знати», «я поруч», побажань. " +
                "Відповідай на її останню репліку по суті, розмова триває.");

        if (Emoji.IsMatch(text) && !Emoji.IsMatch(userText))
            return Fail("Людина не користується емодзі. Прибери їх.");

        if (ListLine.Matches(text).Count >= 2 && !asksHelp)
            return Fail("Це переписка, а не список. Напиши зв'язною розмовною мовою без пунктів.");

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
            "\nОстання репліка людини: " + current +
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

    // Only the tail matters: a closing formula in the middle is just a phrase.
    private static bool ClosesConversation(string text)
    {
        var sentences = Regex.Split(text, @"(?<=[.!?…])\s+", RegexOptions.CultureInvariant, Limit)
            .Where(x => x.Trim().Length > 0).ToArray();
        return sentences.Length > 0 && Closing.IsMatch(sentences[^1]);
    }

    private static int WordCount(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static ReplyQualityResult Pass() => new(true, "");
    private static ReplyQualityResult Fail(string feedback) => new(false, feedback);
}
