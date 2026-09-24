using System.Text.RegularExpressions;

namespace Trivozhno.Features.Conversation;

public sealed record ReplyQualityResult(bool Accept, string Feedback);

public static class ReplyQualityGate
{
    private static readonly Regex CannedAdvice = new(
        @"(?i)\b(4\s*[-–]\s*7\s*[-–]\s*8|дихаль\w*|вдих\w*|видих\w*|запиш\w*|випиш\w*|нотат\w*|блокнот\w*|випий\w*|склянк\w*\s+вод\w*|пройд\w*|прогуля\w*|послухай\w*\s+.*муз|подкаст\w*|заземл\w*)\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex UnsupportedPhysiology = new(
        @"(?i)\b(серцебит\w*|кортизол\w*|нервов\w*\s+систем\w*|дає\s+мозку\s+сигнал|фізично\s+(сповільнює|знижує)|знижує\s+(рівень\s+)?тривог\w*)\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex PushyImperative = new(
        @"(?i)\b(спробуй|зроби|відклади|встань|випий|запиши|послухай|пройди|подихай|закрий|увімкни|включи)\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex Cliche = new(
        @"(?i)(якщо\s+захочеш.{0,25}я\s+тут|\bя\s+тут\b|\bтримайся\b|\bбез\s+тиску\b)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex DistressShare = new(
        @"(?i)\b(треш|погано|фігово|сил\s+нема|нема\s+сил|тривож\w*|куп[аи]\s+думок|втом\w*|виснаж\w*)\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static ReplyQualityResult Check(DialogueAct act, string current, string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return Fail("Сформулюй одну завершену коротку репліку.");

        var text = reply.Trim();

        if (Cliche.IsMatch(text))
            return Fail("Заміни службове кліше на конкретну реакцію по цій переписці.");

        if (UnsupportedPhysiology.IsMatch(text))
            return Fail("Прибери фізіологічне або медичне пояснення. Говори побутово й не обіцяй ефект.");

        if (act == DialogueAct.Greeting)
        {
            if (text.Contains('?') || WordCount(text) > 8)
                return Fail("Залиш тільки коротке привітання без питання й без нового змісту.");
            return Pass();
        }

        if (act is DialogueAct.Refusal or DialogueAct.ShortReply)
        {
            if (text.Contains('?') || PushyImperative.IsMatch(text))
                return Fail("Дай коротке твердження, яке приймає сказане й не просить людину щось пояснювати або робити.");
        }

        if (act == DialogueAct.Sharing && DistressShare.IsMatch(current))
        {
            if (text.Contains('?'))
                return Fail("На цю репліку відгукнися твердженням по суті, без уточнювального питання.");
            if (PushyImperative.IsMatch(text))
                return Fail("Людина просто ділиться станом. Відгукнися на нього без поради або завдання.");
        }

        if (act == DialogueAct.Advice && CannedAdvice.IsMatch(text))
            return Fail("Це універсальна self-help вправа. Замість неї дай одну контекстну пораду, що випливає саме з цієї переписки.");

        return Pass();
    }

    public static string RetryInstruction(ReplyQualityResult result, string draft) =>
        "Попередня чернетка не підходить для цього ходу. " + result.Feedback +
        "\nСформулюй іншу репліку з нуля. Не пояснюй виправлення.\nЧернетка, яку НЕ треба повторювати: " +
        draft.Trim();

    private static int WordCount(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static ReplyQualityResult Pass() => new(true, "");
    private static ReplyQualityResult Fail(string feedback) => new(false, feedback);
}
