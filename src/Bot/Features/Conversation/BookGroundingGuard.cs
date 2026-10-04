using System.Text;
using System.Text.RegularExpressions;
using Trivozhno.Features.Memory;

namespace Trivozhno.Features.Conversation;

public sealed record BookReplyCheck(bool Accepted, string Reason);

// Evidence/provenance and numeric-parameter checks, not a semantic LLM judge.
// Failed advice is replaced with actual source text, never another guessed method.
public static class BookGroundingGuard
{
    public const string Marker = @"\{\{\s*book\s*:\s*(\d+)\s*(?:\|\s*([^{}]*?))?\s*\}\}";

    public static bool ValidQuote(BookContext context, long id, string quote) =>
        Normalize(quote).Length >= 20 && context.Evidence.TryGetValue(id, out var excerpt) &&
        Normalize(excerpt).Contains(Normalize(quote), StringComparison.Ordinal);

    public static BookReplyCheck Check(BookContext context, string reply)
    {
        if (context.Evidence.Count == 0) return new(false, "no_evidence");
        var proofs = Regex.Matches(reply, Marker, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (proofs.Count == 0) return new(false, "missing_quote");
        var quotes = new List<string>();
        foreach (Match proof in proofs)
        {
            if (!long.TryParse(proof.Groups[1].Value, out var id) ||
                !proof.Groups[2].Success || !ValidQuote(context, id, proof.Groups[2].Value))
                return new(false, "unverified_quote");
            quotes.Add(proof.Groups[2].Value);
        }
        var rendered = context.Render(reply);
        if (rendered.Used.Count == 0) return new(false, "missing_reference");
        var allowed = quotes.SelectMany(Numbers).ToHashSet(StringComparer.Ordinal);
        if (Numbers(rendered.Text).Any(x => !allowed.Contains(x)))
            return new(false, "unsupported_numeric_parameter");
        return new(true, "quote_and_parameters_verified");
    }

    public static (string Text, IReadOnlyList<SourceMetadata> Used) Fallback(BookContext context)
    {
        var source = context.Sources.FirstOrDefault(x => context.Evidence.ContainsKey(x.ChunkId));
        if (source is null)
            return ("Зараз не можу підтвердити конкретний спосіб. Розкажи, як ця тривога в тебе проявляється.", []);
        var excerpt = context.Evidence[source.ChunkId].Trim();
        if (excerpt.Length > 700)
        {
            var end = excerpt.LastIndexOfAny(['.', '!', '?'], 699);
            // Do not manufacture an incomplete step; retain the full first sentence
            // if the available text has no sentence boundary inside the short limit.
            if (end >= 80) excerpt = excerpt[..(end + 1)];
        }
        return ("Є ось така думка:\n\n«" + excerpt + "»", [source]);
    }

    private static IEnumerable<string> Numbers(string text) => Regex.Matches(Normalize(text),
        @"(?<!\d)\d+(?:[.,]\d+)?(?:\s*[-:]\s*\d+(?:[.,]\d+)?)*(?!\d)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
        .Select(m => Regex.Replace(m.Value, @"\s+", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));

    private static string Normalize(string text) => Regex.Replace(text.Normalize(NormalizationForm.FormKC)
        .Replace('‑', '-').Replace('–', '-').Replace('—', '-').Replace('−', '-')
        .Replace('’', '\'').Replace('ʼ', '\''), @"\s+", " ", RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100)).Trim();
}
