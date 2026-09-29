using System.Text.RegularExpressions;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Knowledge;

namespace Trivozhno.Features.Conversation;

public sealed record BookAdviceRequest(string Query, bool ContinueSources, bool SourceQuestion);

// These signals only request a local lookup. They never prescribe a reply,
// diagnose a person, or change the bot's conversational character.
public static class BookAdviceIntent
{
    private const string AdvicePattern = @"\b(що|шо)\s+(мені\s+)?робити|\bяк\s+(мені\s+)?(це\s+зробити|бути|впоратися|впоратись|заспокоїтися|заспокоїтись|позбутися|перестати)|\b(порад\p{L}*|підкаж\p{L}*|помож\p{L}*|допомож\p{L}*)\b|\bє\s+(якийсь\s+)?спосіб|\bможна\s+щось\s+(з|із)\s+цим\s+зробити";
    private static readonly HashSet<string> Topics =
    [
        "тривога", "страх", "самотність", "стосунки", "межі", "самооцінка",
        "провина", "злість", "конфлікт", "втома", "довіра", "ревнощі",
        "розставання", "почуття", "підтримка", "прокрастинація", "перфекціонізм"
    ];

    public static string? Query(IReadOnlyList<AiMessage> conversation) => Plan(conversation)?.Query;

    public static BookAdviceRequest? Plan(IReadOnlyList<AiMessage> conversation, bool hasPreviousBooks = false,
        bool hasPreviousOtherSources = false)
    {
        var turns = conversation.Where(m => m.Role == "user").TakeLast(8)
            .Select(m => m.Content.Trim()).ToArray();
        if (turns.Length == 0) return null;
        var current = turns[^1];
        if (Matches(current, @"не\s+(радь|пропонуй)|(?:без|не хочу|не треба)\s+(порад|вправ|технік)"))
            return null;
        var asksSource = Matches(current, @"\b(звідки|джерел\p{L}*|книг\p{L}*|сторінк\p{L}*)\b");
        var asksAdvice = Matches(current, AdvicePattern);
        var followup = Matches(current,
            @"^(а\s+|і\s+)?(як\s+(саме|це)|чому|що далі|далі[.!? ]*$|поясни|детальніше|докладніше|покажи|наведи|дай\s+приклад|ще\s+приклад)");
        var currentTopic = Lexicon.Terms(current).Any(Topics.Contains);
        var earlier = turns.SkipLast(1).LastOrDefault(t => Lexicon.Terms(t).Any(Topics.Contains));
        var asksOriginalSource = asksSource && Matches(current, @"\bзвідки\b|\bджерел\p{L}*|\b(яка|якої|яку)\s+(це\s+)?книг");
        var explicitBook = Matches(current, @"\bкниг\p{L}*\b");
        if (asksOriginalSource && hasPreviousOtherSources && !hasPreviousBooks && !explicitBook)
            return null;
        asksSource &= explicitBook || hasPreviousBooks || currentTopic || earlier is not null;
        var continueSources = hasPreviousBooks && (asksOriginalSource || followup && !currentTopic && !asksAdvice);
        var continuesAdvice = followup && turns.SkipLast(1).TakeLast(3).Any(t => Matches(t, AdvicePattern));
        if (!asksSource && !continueSources && (!asksAdvice && !continuesAdvice || !currentTopic && earlier is null))
            return null;
        var query = current;
        if (!currentTopic && earlier is not null) query += "\n" + earlier;
        // Preserve the latest request at the front; old context is optional.
        if (query.Length > 900) query = query[..900];
        return new(query, continueSources, asksOriginalSource);
    }

    private static bool Matches(string text, string pattern) => Regex.IsMatch(text, pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}
