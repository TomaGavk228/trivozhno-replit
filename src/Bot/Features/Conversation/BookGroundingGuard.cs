using System.Text;
using System.Text.RegularExpressions;
using Trivozhno.Features.Memory;

namespace Trivozhno.Features.Conversation;

// Legacy marker reader and shared numeric normalization. New book replies use
// BookReply; there is deliberately no source-extract response fallback.
public static class BookGroundingGuard
{
    public const string Marker = @"\{\{\s*book\s*:\s*(\d+)\s*(?:\|\s*([^{}]*?))?\s*\}\}";

    public static bool ValidQuote(BookContext context, long id, string quote) =>
        Normalize(quote).Length >= 20 && context.Evidence.TryGetValue(id, out var excerpt) &&
        Normalize(excerpt).Contains(Normalize(quote), StringComparison.Ordinal);

    internal static IEnumerable<string> Numbers(string text) => Regex.Matches(Normalize(Regex.Replace(text,
        @"(?m)^\s*\d+[.)]\s+", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))),
        @"(?<!\d)\d+(?:[.,]\d+)?(?:\s*[-:]\s*\d+(?:[.,]\d+)?)*(?!\d)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
        .Select(m => Regex.Replace(m.Value, @"\s+", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));

    private static string Normalize(string text) => Regex.Replace(text.Normalize(NormalizationForm.FormKC)
        .Replace('‑', '-').Replace('–', '-').Replace('—', '-').Replace('−', '-')
        .Replace('’', '\'').Replace('ʼ', '\''), @"\s+", " ", RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100)).Trim();
}
