using System.Text.RegularExpressions;
using Trivozhno.Features.Memory;
using Trivozhno.Infrastructure.Groq;

namespace Trivozhno.Infrastructure.Knowledge;

// Returns one contiguous window of original text, including nearby sentences.
// It never synthesizes or stitches separate statements into a new claim.
public static class SourceExcerpt
{
    public static string Select(string text, string query, int tokenBudget)
    {
        if (tokenBudget < 30 || string.IsNullOrWhiteSpace(text)) return "";
        if (TokenEstimate.Count(text) <= tokenBudget) return text;
        var boundaries = new List<int> { 0 };
        boundaries.AddRange(Regex.Matches(text, @"(?<=[.!?…])\s+|\n[ \t]*\n+",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
            .Select(m => m.Index + m.Length));
        if (boundaries[^1] != text.Length) boundaries.Add(text.Length);
        var terms = Lexicon.Terms(query).ToHashSet();
        var center = Enumerable.Range(0, boundaries.Count - 1)
            .OrderByDescending(i => Lexicon.Terms(text[boundaries[i]..boundaries[i + 1]])
                .Distinct().Count(terms.Contains)).ThenBy(i => i).First();
        var first = center;
        var last = center + 1;
        var allowance = tokenBudget - 12;
        if (TokenEstimate.Count(text[boundaries[first]..boundaries[last]]) > allowance)
            return "…" + ConversationMemory.TrimToTokenBudget(text[boundaries[first]..boundaries[last]], allowance) + "…";
        while (true)
        {
            var grew = false;
            if (first > 0 && TokenEstimate.Count(text[boundaries[first - 1]..boundaries[last]]) <= allowance)
            { first--; grew = true; }
            if (last < boundaries.Count - 1 && TokenEstimate.Count(text[boundaries[first]..boundaries[last + 1]]) <= allowance)
            { last++; grew = true; }
            if (!grew) break;
        }
        return (first > 0 ? "…" : "") + text[boundaries[first]..boundaries[last]].Trim() +
            (last < boundaries.Count - 1 ? "…" : "");
    }
}
