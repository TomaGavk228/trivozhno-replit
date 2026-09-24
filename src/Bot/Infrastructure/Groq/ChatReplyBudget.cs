using System.Text.RegularExpressions;

namespace Trivozhno.Infrastructure.Groq;

// A request for advice is still an ordinary chat turn. Only an explicit request
// for detail changes the output allowance; this never classifies emotion.
public static class ChatReplyBudget
{
    public const int BriefTokens = 240;

    public static bool WantsDetail(IReadOnlyList<AiMessage> messages)
    {
        var latest = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        if (Regex.IsMatch(latest, @"(?i)(?<!\p{L})(коротко|стисло|briefly)(?!\p{L})|не\s+(пиши\s+)?(детально|докладно|розгорнуто)",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return false;
        return Regex.IsMatch(latest,
            @"(?i)(?<!\p{L})(детально|докладно|розгорнуто|розгорнуту|докладний|докладну|подробно|in detail)(?!\p{L})|(?i)(довгу історію|повний текст|великий список)",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    public static int Limit(IReadOnlyList<AiMessage> messages, int configuredMaximum) =>
        WantsDetail(messages) ? configuredMaximum : Math.Min(BriefTokens, configuredMaximum);

    public static IReadOnlyList<AiMessage> CompactRetry(IReadOnlyList<AiMessage> messages)
    {
        const string instruction = "Відповідь не вмістилася. Сформулюй її заново: одна головна думка, " +
            "1–2 короткі речення, без списку й вступу. Заверши речення. Не згадуй повторну спробу.";
        var copy = messages.ToList();
        if (copy.Count > 0 && copy[0].Role == "system")
            copy[0] = copy[0] with { Content = copy[0].Content + "\n\n" + instruction };
        else copy.Insert(0, new("system", instruction));
        return copy;
    }
}
