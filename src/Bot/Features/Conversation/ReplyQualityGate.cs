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

    private static readonly Regex DeadEnd = new(
        @"(?i)^(добре|ок|окей|понятно|зрозуміло|угу|ага|ясно)[.! )]*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex DistressShare = new(
        @"(?i)\b(треш|погано|фігово|сил\s+нема|нема\s+сил|тривож\w*|куп[аи]\s+думок|втом\w*|виснаж\w*)\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "і","й","та","а","але","бо","що","шо","це","я","ти","в","у","на","до","з","із","зі","не","ні",
        "мені","тебе","мене","воно","вони","вона","він","ще","як","так","то","там","тут","просто","дуже",
        "реально","зараз","вже","ну","угу","ага","ок","окей","погано","добре","нічого","не знаю"
    };

    public static ReplyQualityResult Check(
        DialogueAct act,
        string current,
        string reply,
        IReadOnlyList<string>? recentUser = null)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return Fail("Сформулюй одну завершену коротку репліку.");

        recentUser ??= [];
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
                return Fail("Продовж розмову коротким твердженням без нового завдання або обов'язкового питання.");
            if (DeadEnd.IsMatch(text) || WordCount(text) < 4)
                return Fail("Не обривай діалог сухим підтвердженням. Додай одну маленьку думку з поточної теми, щоб бот теж ніс розмову.");
            if (!HasContextAnchor(text, current, recentUser))
                return Fail("Відповідь відірвана від переписки. Прив'яжи її до конкретної деталі з останніх реплік і не додавай нових фактів.");
        }

        if (act == DialogueAct.Sharing && DistressShare.IsMatch(current))
        {
            if (text.Contains('?'))
                return Fail("На цю репліку відгукнися твердженням по суті, без уточнювального питання.");
            if (PushyImperative.IsMatch(text))
                return Fail("Людина просто ділиться станом. Відгукнися на нього без поради або завдання.");
            if (!HasContextAnchor(text, current, recentUser))
                return Fail("Відповідь має бути прив'язана до того, що людина реально сказала, без нових деталей.");
        }

        if (act == DialogueAct.Advice)
        {
            if (CannedAdvice.IsMatch(text))
                return Fail("Це універсальна self-help вправа. Замість неї дай одну контекстну пораду, що випливає саме з цієї переписки.");
            if (CountImperatives(text) > 1)
                return Fail("Забагато завдань. Залиш одну конкретну пораду.");
            if (!HasContextAnchor(text, current, recentUser))
                return Fail("Порада має явно спиратися на контекст цієї переписки, а не бути універсальною.");
        }

        if (act == DialogueAct.Question && DistressShare.IsMatch(current) && CannedAdvice.IsMatch(text))
            return Fail("Не підміняй відповідь універсальною self-help технікою.");

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

    private static bool HasContextAnchor(
        string reply,
        string current,
        IReadOnlyList<string> recentUser)
    {
        var context = string.Join(' ', recentUser.Append(current));
        var contextTerms = Terms(context);
        if (contextTerms.Count == 0) return true;
        var replyTerms = Terms(reply);
        return replyTerms.Overlaps(contextTerms);
    }

    private static HashSet<string> Terms(string text)
    {
        return Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]{3,}")
            .Select(x => x.Value)
            .Where(x => !StopWords.Contains(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static int CountImperatives(string text) =>
        PushyImperative.Matches(text).Count;

    private static int WordCount(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static ReplyQualityResult Pass() => new(true, "");
    private static ReplyQualityResult Fail(string feedback) => new(false, feedback);
}
