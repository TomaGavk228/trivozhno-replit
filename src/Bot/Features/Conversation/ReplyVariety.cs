using System.Text.RegularExpressions;

namespace Trivozhno.Features.Conversation;

// A cheap local hint against the model settling into one template: repeated
// openers and reaction words, "X чи Y?" choices. Questions themselves are not
// limited: after a greeting, "шо сталося?" is often the right next reply.
// It only describes the bot's own recent replies; no extra model request.
public static class ReplyVariety
{
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "коли", "тебе", "тобі", "мене", "мені", "щось", "шось", "тому", "якщо", "тоді", "може", "просто", "дуже",
        "воно", "вона", "вони", "було", "буде", "який", "яка", "яке", "які", "також", "тільки", "лише", "ніби",
        "навіть", "зараз", "щоб", "шоб", "того", "цього", "цьому", "цей", "ця", "це", "там", "тут", "так",
        "мого", "твій", "твоя", "твоє", "твої", "свою", "собі", "себе", "його", "неї", "них", "вже", "ще", "бо",
        "буває", "можна", "треба", "хочеш", "знаєш", "взагалі", "реально", "отак", "такий", "така", "таке",
        "інколи", "іноді", "давай", "розкажи", "сьогодні"
    };

    public static string Hint(IReadOnlyList<string> recentReplies)
    {
        var replies = recentReplies.Where(x => !string.IsNullOrWhiteSpace(x)).TakeLast(6).ToArray();
        if (replies.Length < 2) return "";
        var notes = new List<string>();

        var openers = replies.SelectMany(r => r.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(line => Words(line).FirstOrDefault()).Where(w => w is { Length: >= 2 })
            .GroupBy(w => w!).Where(g => g.Count() >= 2).Select(g => g.Key).Take(5).ToArray();
        if (openers.Length > 0)
            notes.Add("останні відповіді часто починалися з " + Quote(openers) + ", почни інакше");

        var last = replies.TakeLast(4).ToArray();
        var repeated = last.SelectMany(r => Words(r).Where(w => w.Length >= 4 && !Common.Contains(w)).Distinct())
            .GroupBy(w => w).Where(g => g.Count() >= 2).Select(g => g.Key).Except(openers).Take(8).ToArray();
        if (repeated.Length > 0)
            notes.Add("слова " + Quote(repeated) + " вже повторювалися, обійдися без них");

        var choices = last.Count(r => Regex.IsMatch(r, @"\bчи\b[^?\n]*\?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));
        if (choices >= 2) notes.Add("не став питань з вибором «X чи Y?»");

        return notes.Count == 0 ? "" :
            "Підказка про різноманіття (стосується лише форми твоїх відповідей, зміст розмови важливіший): " + string.Join("; ", notes) + ".";
    }

    private static IEnumerable<string> Words(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[\p{L}'’]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
            .Select(m => m.Value.Trim('\'', '’')).Where(w => w.Length > 0);

    private static string Quote(IEnumerable<string> words) => string.Join(", ", words.Select(w => "«" + w + "»"));
}
