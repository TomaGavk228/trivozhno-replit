using System.Text.RegularExpressions;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Knowledge;

namespace Trivozhno.Features.Conversation;

public sealed record BookAdviceRequest(string Query, bool ContinueSources, bool SourceQuestion,
    bool Alternative = false);

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
        var turns = conversation.Where(m => m.Role == "user").TakeLast(5)
            .Select(m => m.Content.Trim()).ToArray();
        if (turns.Length == 0) return null;
        var current = turns[^1];
        if (Matches(current, @"не\s+(радь|пропонуй)|(?:без|не хочу|не треба)\s+(порад|вправ|технік)"))
            return null;
        var shortText = current.TrimEnd(' ', '.', '!', '?', ')', '(');
        var alternative = Matches(shortText,
            @"^(а\s+)?(щось\s+інше|інший\s+(спосіб|варіант)|є\s+щось\s+інше|давай\s+(щось\s+)?інше|а\s+ще)$");
        var asksOriginalSource = Matches(current,
            @"^(а\s+)?звідки[.!? ]*$|\bзвідки\s+(це|ти|інформація|порада|метод)\b|\bджерел\p{L}*|\b(яка|якої|яку)\s+(це\s+)?книг|\b(якій|яка)\s+сторін");
        var explicitBook = Matches(current, @"\bкниг\p{L}*\b");
        var asksAdvice = Matches(current, AdvicePattern);
        var currentTopic = HasTopic(current);
        var followup = RefersToAdvice(shortText) || currentTopic &&
            Matches(current, @"^(а\s+|і\s+)?(поясни|як саме|чому)\b");
        var genericHelp = Matches(shortText,
            @"^(а\s+)?((що|шо)\s+(мені\s+)?робити|допоможи(\s+мені)?|підкажи|порадь|як\s+(мені\s+)?бути)$");
        var agrees = Matches(shortText, @"^(так|ага|давай|добре|ок|хочу|спробуймо)$");
        var previousAssistant = conversation.LastOrDefault(m => m.Role == "assistant")?.Content ?? "";
        var acceptsOffer = agrees && Matches(previousAssistant, @"\b(хочеш|можу|спробу\p{L}*)\b") &&
            (hasPreviousBooks || Matches(previousAssistant, @"\b(вправ\p{L}*|технік\p{L}*|спосіб|метод\p{L}*|заспокої\p{L}*)\b"));

        // Inherit a topic through short replies, but stop at an unrelated user
        // turn. Yesterday's anxiety must not turn "порадь фільм" into therapy.
        string? earlier = null;
        foreach (var turn in turns.SkipLast(1).Reverse())
        {
            if (HasTopic(turn)) { earlier = turn; break; }
            if (!RefersToAdvice(turn.TrimEnd(' ', '.', '!', '?', ')', '(')) && !Matches(turn,
                    @"^(а\s+)?(не знаю|хз|так|ага|давай|добре|ок|ні[, ]*не хочу|ні|не хочу|допоможи(\s+мені)?)[.!? ()]*$")) break;
        }
        if (asksOriginalSource && hasPreviousOtherSources && !hasPreviousBooks && !explicitBook)
            return null;
        var asksSource = asksOriginalSource && (explicitBook || hasPreviousBooks || currentTopic || earlier is not null);
        var continueSources = hasPreviousBooks && !alternative &&
            (asksSource || acceptsOffer || followup && !currentTopic && !asksAdvice);
        var contextualRequest = earlier is not null && (genericHelp || followup || alternative || acceptsOffer);
        if (!asksSource && !continueSources && !(currentTopic && (asksAdvice || followup)) && !contextualRequest)
            return null;
        var query = current;
        if (!currentTopic && earlier is not null) query += "\n" + earlier;
        // Preserve the latest request at the front; old context is optional.
        if (query.Length > 900) query = query[..900];
        return new(query, continueSources, asksSource, alternative);
    }

    private static bool HasTopic(string text) => Lexicon.Terms(text).Any(Topics.Contains);

    private static bool RefersToAdvice(string text) => Matches(text,
        @"^(а\s+|і\s+)?(як саме|як це(\s+(працює|робити|зробити|допомагає))?|чому|що далі|далі|поясни(\s+(це|детальніше|докладніше))?|детальніше|докладніше|покажи(\s+(як|приклад))?|наведи приклад|дай приклад|ще приклад)$");

    private static bool Matches(string text, string pattern) => Regex.IsMatch(text, pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}
