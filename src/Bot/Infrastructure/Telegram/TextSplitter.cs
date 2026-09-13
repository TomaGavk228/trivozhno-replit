using System.Globalization;

namespace Trivozhno.Infrastructure.Telegram;

public static class TextSplitter
{
    // Lossless: whitespace belongs to one of the parts; surrogate pairs are never split.
    public static IReadOnlyList<string> Split(string text, int max = 4000)
    {
        if (max < 2) throw new ArgumentOutOfRangeException(nameof(max));
        if (text.Length == 0) return [];
        var result = new List<string>(); var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(start + max, text.Length);
            if (end < text.Length)
            {
                var segment = text.Substring(start, end - start);
                var boundaries = StringInfo.ParseCombiningCharacters(segment);
                if (boundaries.Length > 1) end = start + boundaries[^1];
                if (end <= start) end = start + max;
                if (char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end--;
                var paragraph = text.LastIndexOf('\n', end - 1, end - start);
                var space = text.LastIndexOf(' ', end - 1, end - start);
                var preferred = paragraph >= start + max / 2 ? paragraph + 1 : space >= start + max / 2 ? space + 1 : end;
                end = preferred;
            }
            result.Add(text[start..end]); start = end;
        }
        return result;
    }
}
