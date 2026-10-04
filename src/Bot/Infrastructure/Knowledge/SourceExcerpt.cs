using System.Text.RegularExpressions;
using Trivozhno.Features.Memory;
using Trivozhno.Infrastructure.Groq;

namespace Trivozhno.Infrastructure.Knowledge;

// Returns one contiguous window of original text, including nearby sentences.
// It never synthesizes or stitches separate statements into a new claim.
public static class SourceExcerpt
{
    public static string Select(string text, string query, int tokenBudget, bool completeSentences = false)
    {
        if (tokenBudget < 30 || string.IsNullOrWhiteSpace(text)) return "";
        if (TokenEstimate.Count(text) <= tokenBudget) return text;
        var boundaries = new List<int> { 0 };
        boundaries.AddRange(Regex.Matches(text, @"(?<=[.!?…])\s+|\n[ \t]*\n+",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
            .Select(m => m.Index + m.Length));
        if (boundaries[^1] != text.Length) boundaries.Add(text.Length);
        // Rank small contiguous windows, so an exercise heading and its topic
        // can live in neighbouring sentences. Keep conditions with the method.
        var center = Enumerable.Range(0, boundaries.Count - 1)
            .OrderByDescending(i => PassageRelevance.WindowScore(
                text[boundaries[Math.Max(0, i - 1)]..boundaries[Math.Min(boundaries.Count - 1, i + 3)]], query))
            .ThenBy(i => i).First();
        var first = center;
        var last = center + 1;
        var allowance = tokenBudget - 12;
        if (TokenEstimate.Count(text[boundaries[first]..boundaries[last]]) > allowance)
            return completeSentences ? "" :
                "…" + ConversationMemory.TrimToTokenBudget(text[boundaries[first]..boundaries[last]], allowance) + "…";
        while (true)
        {
            var grew = false;
            // For books, spend room on following conditions before preamble.
            if (!completeSentences && first > 0 && TokenEstimate.Count(text[boundaries[first - 1]..boundaries[last]]) <= allowance)
            { first--; grew = true; }
            if (last < boundaries.Count - 1 && TokenEstimate.Count(text[boundaries[first]..boundaries[last + 1]]) <= allowance)
            { last++; grew = true; }
            if (completeSentences && first > 0 && TokenEstimate.Count(text[boundaries[first - 1]..boundaries[last]]) <= allowance)
            { first--; grew = true; }
            if (!grew) break;
        }
        return (first > 0 ? "…" : "") + text[boundaries[first]..boundaries[last]].Trim() +
            (last < boundaries.Count - 1 ? "…" : "");
    }
}
