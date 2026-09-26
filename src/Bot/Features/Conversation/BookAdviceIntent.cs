using System.Text.RegularExpressions;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Knowledge;

namespace Trivozhno.Features.Conversation;

// Search is deterministic and local: a direct request for advice plus a topic
// already mentioned by this user. The model gets one generation, not a lookup
// generation followed by another reply generation.
public static class BookAdviceIntent
{
    private static readonly HashSet<string> Topics =
    [
        "тривога", "страх", "самотність", "стосунки", "межі", "самооцінка",
        "провина", "злість", "конфлікт", "втома", "довіра", "ревнощі",
        "розставання", "почуття", "підтримка", "прокрастинація", "перфекціонізм"
    ];

    public static string? Query(IReadOnlyList<AiMessage> conversation)
    {
        var userTurns = conversation.Where(m => m.Role == "user").TakeLast(3)
            .Select(m => m.Content.Trim()).ToArray();
        if (userTurns.Length == 0) return null;
        var current = userTurns[^1];
        var asksBook = Regex.IsMatch(current,
            @"\b(книг\p{L}*|джерел\p{L}*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        var asksAdvice = Regex.IsMatch(current,
            @"(що|шо)\s+(мені\s+)?робити|як\s+(мені\s+)?(це\s+зробити|бути|впоратися|впоратись|заспокоїтися|заспокоїтись|позбутися)|\b(порад\p{L}*|підкаж\p{L}*|помож\p{L}*|допомож\p{L}*)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!asksBook && !asksAdvice) return null;

        var query = string.Join(" ", userTurns);
        if (!asksBook && !Lexicon.Terms(query).Any(Topics.Contains)) return null;
        return query.Length <= 600 ? query : query[^600..];
    }
}
