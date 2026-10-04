using System.Text.RegularExpressions;

namespace Trivozhno.Infrastructure.Knowledge;

// A transparent lexical reranker, not embeddings or a semantic relevance claim.
// Keep the user's purpose separately from stop-word removal in BM25.
public static class PassageRelevance
{
    public static bool WantsPracticalHelp(string query) => Regex.IsMatch(query.ToLowerInvariant(),
        @"\b(побороти|подолати|позбутися|впоратис[яь]|заспокоїтис[яь]|допомож\p{L}*|порад\p{L}*|спосіб|вправ\p{L}*|технік\p{L}*)\b|\b(що|шо)\s+(мені\s+)?робити\b",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static int PracticalSignals(string passage) => Math.Min(4, Regex.Matches(passage.ToLowerInvariant(),
        @"\b(вправ\p{L}*|практик\p{L}*|технік\p{L}*|спробуйте|спробуй|запишіть|запиши|зверніть|зверни|назвіть|назви|зосередьтеся|зосередься|помічайте|помічай|виконайте|виконай)\b",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Select(m => m.Value).Distinct().Count());

    public static double WindowScore(string text, string query)
    {
        var terms = Lexicon.SearchTerms(query).ToHashSet();
        var words = Lexicon.Terms(text).ToHashSet();
        var overlap = terms.Count(words.Contains);
        return overlap + (WantsPracticalHelp(query) && overlap > 0 ? PracticalSignals(text) * 0.75 : 0);
    }
}
