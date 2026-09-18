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

    public static string Apply(string? current, IEnumerable<string> delta)
    {
        var values = Parse(current).ToHashSet(StringComparer.Ordinal);
        foreach (var item in delta.Where(Allowed.Contains).Distinct(StringComparer.Ordinal))
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
