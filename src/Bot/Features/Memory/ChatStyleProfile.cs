using System.Text.RegularExpressions;

namespace Trivozhno.Features.Memory;

public static class ChatStyleProfile
{
    private static readonly Dictionary<string, string[]> Groups = new(StringComparer.Ordinal)
    {
        ["length"] = ["shorter", "fuller"],
        ["questions"] = ["fewer_questions", "more_questions"],
        ["humor"] = ["likes_humor", "less_humor"],
        ["tone"] = ["casual", "neutral_tone"],
        ["punctuation"] = ["light_punctuation", "normal_punctuation"],
        ["advice"] = ["minimal_advice", "direct_advice", "ask_before_advice"]
    };

    private static readonly HashSet<string> Allowed =
        Groups.Values.SelectMany(x => x).ToHashSet(StringComparer.Ordinal);

    // Persist only standalone, explicit requests about how to talk. Ordinary
    // refusals, quoted dialogue and one-off "not now" replies are not a profile.
    // This deliberately misses ambiguous wording; the model still sees it in chat.
    public static IReadOnlyList<string> ExplicitDelta(string text)
    {
        if (text.Length > 2000 || text.IndexOfAny(['«', '»', '"', '`', '>']) >= 0) return [];
        var result = new List<string>();
        foreach (var part in Regex.Split(text, @"[\r\n.!?;]+", RegexOptions.CultureInvariant,
                     TimeSpan.FromMilliseconds(100)))
        {
            var phrase = part.Trim().ToLowerInvariant();
            if (phrase.Length == 0) continue;
            var before = result.Count;
            phrase = Regex.Replace(phrase, @"^(будь ласка[,]?\s+)|(,?\s+будь ласка)$", "",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            var ongoing = phrase.StartsWith("надалі ", StringComparison.Ordinal) ||
                          phrase.StartsWith("завжди ", StringComparison.Ordinal);
            if (ongoing) phrase = phrase[7..];
            Add("shorter", @"(пиши|відповідай|говори)\s+(коротше|коротко|стисло)");
            Add("fuller", @"(пиши|відповідай)\s+(детальніше|докладніше|розгорнутіше)");
            Add("fewer_questions", @"менше\s+(питань|запитань)|не\s+став\s+стільки\s+(питань|запитань)");
            Add("more_questions", @"(став|задавай)\s+більше\s+(питань|запитань)");
            Add("likes_humor", @"(жартуй\s+більше|більше\s+жартуй)");
            Add("less_humor", @"(жартуй\s+менше|менше\s+жартуй)");
            Add("casual", @"(спілкуйся|пиши|говори)\s+(простіше|невимушено)|без\s+офіціозу");
            Add("neutral_tone", @"(спілкуйся|пиши|говори)\s+(нейтрально|без\s+сленгу)");
            if (ongoing)
            {
                Add("ask_before_advice", @"(давай\s+поради\s+лише\s+коли\s+я\s+прошу|питай\s+перед\s+порадами)");
                Add("direct_advice", @"давай\s+(прямі|конкретні)\s+поради");
            }
            // A narrative or an unquoted list of examples is not permission to
            // extract preferences from just one of its lines.
            if (result.Count == before) return [];

            void Add(string value, string pattern)
            {
                if (Regex.IsMatch(phrase, "^(?:" + pattern + ")$", RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(100))) result.Add(value);
            }
        }
        return result;
    }

    public static string Apply(string? current, IEnumerable<string> delta)
    {
        var values = Parse(current).ToHashSet(StringComparer.Ordinal);
        foreach (var item in delta.Where(Allowed.Contains))
        {
            var group = Groups.First(x => x.Value.Contains(item, StringComparer.Ordinal)).Value;
            values.RemoveWhere(x => group.Contains(x, StringComparer.Ordinal));
            values.Add(item);
        }
        return string.Join(',', values.Order(StringComparer.Ordinal));
    }

    public static string Prompt(string? stored)
    {
        var values = Parse(stored);
        if (values.Count == 0) return "";

        var parts = new List<string>();
        Add("shorter", "краще коротші репліки");
        Add("fuller", "нормально трохи розгорнутіше");
        Add("fewer_questions", "менше питань");
        Add("more_questions", "питання заходять нормально");
        Add("likes_humor", "гумор і дуркування заходять");
        Add("less_humor", "з гумором обережніше");
        Add("casual", "невимушений розмовний тон");
        Add("neutral_tone", "краще нейтральний тон");
        Add("light_punctuation", "легша пунктуація, без зайвої офіційності");
        Add("normal_punctuation", "звичайна пунктуація ок");
        Add("minimal_advice", "поради коротко і лише по суті");
        Add("direct_advice", "можна давати прямі конкретні поради");
        Add("ask_before_advice", "краще не лізти з порадами без запиту");
        return string.Join("; ", parts);

        void Add(string key, string text)
        {
            if (values.Contains(key)) parts.Add(text);
        }
    }

    public static IReadOnlySet<string> Parse(string? stored) =>
        (stored ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Allowed.Contains)
            .ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyList<string> AllowedValues => Allowed.Order(StringComparer.Ordinal).ToArray();
}
