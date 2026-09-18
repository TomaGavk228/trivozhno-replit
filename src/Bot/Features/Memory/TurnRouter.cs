using System.Text.RegularExpressions;

namespace Trivozhno.Features.Memory;

public enum TurnMode
{
    Chat,
    Advice,
    Story,
    Recommendation,
    Info
}

public static class TurnRouter
{
    public static TurnMode Classify(string text, IReadOnlyList<string>? previousUserMessages = null)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return TurnMode.Chat;

        if (IsStoryRequest(value) || IsStoryContinuation(value, previousUserMessages))
            return TurnMode.Story;

        if (Regex.IsMatch(value,
            @"\b(порадь|порекомендуй|підкинь)\b.{0,30}\b(фільм|серіал|музик|пісн|трек|гру|гейм|аніме|книг)|\b(що|який|яку|яке)\b.{0,20}\b(подивитись|подивитися|послухати|пограти|увімкнути)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            return TurnMode.Recommendation;

        if (IsAdviceRequest(value))
            return TurnMode.Advice;

        if (Regex.IsMatch(value,
            @"^(поясни|що таке\b|чому\b|як працює\b|розкажи про\b|розповіси про\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            return TurnMode.Info;

        return TurnMode.Chat;
    }

    public static bool UsesBooks(TurnMode mode) => mode is TurnMode.Advice or TurnMode.Info;

    public static string Instruction(TurnMode mode) => mode switch
    {
        TurnMode.Advice =>
            "Режим цієї репліки: ПОРАДА. Людина прямо просить допомоги або конкретних варіантів. " +
            "Не ухиляйся відповіддю на кшталт «нічого не роби» чи «просто побудь». Дай 1–2 конкретні, реалістичні варіанти саме під ситуацію. " +
            "Якщо є довідкові книжкові фрагменти, використовуй їх як знання, але говори простою мовою й без лекції. Не завалюй списком.",

        TurnMode.Story =>
            "Режим цієї репліки: ІСТОРІЯ. Розкажи одну коротку цікаву життєву історію тільки з переданих нижче сюжетів. " +
            "Не вигадуй нову історію, не додавай мораль, джерело чи службові пояснення. Розкажи так, ніби переказуєш знайомому в чаті.",

        TurnMode.Recommendation =>
            "Режим цієї репліки: РЕКОМЕНДАЦІЯ. Людина просить щось конкретне подивитися, послухати або пограти. " +
            "Запропонуй небагато конкретних варіантів під її настрій/умови. Якщо бракує однієї справді важливої деталі — можна коротко уточнити її.",

        TurnMode.Info =>
            "Режим цієї репліки: ПОЯСНЕННЯ. Відповідай по суті й простою українською. " +
            "Якщо є книжкові фрагменти — використовуй їх лише як довідку. Не перетворюй відповідь на терапевтичну промову.",

        _ =>
            "Режим цієї репліки: ЗВИЧАЙНА РОЗМОВА. Не намагайся вирішити людину або обов'язково дати пораду. " +
            "Природно відреагуй на останню репліку й підтримай хід розмови. Якщо людина не хоче говорити про тему — не зависай у «просто посидимо», а м'яко руш розмову далі."
    };

    public static bool IsAdviceRequest(string text)
    {
        var value = text.Trim();

        if (Regex.IsMatch(value,
            @"^(порадь|підкажи|допоможи( мені)? (розібратися|зрозуміти)|що (мені )?робити\b|як (мені )?(заспокоїтися|заспокоїтись|впоратися|з цим бути|краще (зробити|вчинити))\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            return true;

        return Regex.IsMatch(value,
            @"\b((потрібна|треба) порада|можеш (щось )?(порадити|підказати)|є (якісь )?(ідеї|варіанти|способи).{0,25}(заспокоїтися|заспокоїтись|впоратися))\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    public static bool IsStoryRequest(string text) =>
        Regex.IsMatch(text,
            @"(\b(розкажи|розкажеш|розповіси|розповідай)\b.{0,40}\b(історі\w*|випадок\w*)\b)|(\bможеш\b.{0,20}\b(розказати|розповісти)\b.{0,30}\b(історі\w*|випадок\w*)\b)|(\bрозкажи\b.{0,30}\b(щось )?(цікаве|смішне|дивне|життєве)\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static bool IsContinuationPhrase(string text) =>
        Regex.IsMatch(text.Trim(),
            @"^(да|так|ага|угу|давай|окей|можна|розказуй|розповідай|ще|ще одну|давай ще|давай ще одну)[!. ]*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static bool IsStoryContinuation(string text, IReadOnlyList<string>? previousUserMessages)
    {
        if (!IsContinuationPhrase(text) || previousUserMessages is null || previousUserMessages.Count == 0)
            return false;

        for (var i = previousUserMessages.Count - 1; i >= Math.Max(0, previousUserMessages.Count - 4); i--)
        {
            var previous = previousUserMessages[i];
            if (IsStoryRequest(previous)) return true;
            if (!IsContinuationPhrase(previous)) break;
        }
        return false;
    }

    public static string ResolveStoryQuery(string current, IReadOnlyList<string> previousUserMessages)
    {
        if (IsStoryRequest(current)) return current;
        for (var i = previousUserMessages.Count - 1; i >= Math.Max(0, previousUserMessages.Count - 4); i--)
        {
            if (IsStoryRequest(previousUserMessages[i])) return previousUserMessages[i];
        }
        return current;
    }
}
