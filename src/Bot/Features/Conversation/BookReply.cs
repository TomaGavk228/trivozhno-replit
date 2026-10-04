using System.Text.Json;
using System.Text.RegularExpressions;
using Trivozhno.Features.Memory;

namespace Trivozhno.Features.Conversation;

// This validates provenance, copied text and numeric parameters, NOT semantic
// entailment. A model's valid source ID is not proof that every claim is true.
public sealed record BookReply(string Kind, string Text, IReadOnlyList<SourceMetadata> Sources)
{
    public static bool TryRead(string raw, BookContext context, out BookReply? reply, out string reason,
        string userContext = "")
    {
        reply = null;
        reason = "invalid_book_reply";
        try
        {
            using var json = JsonDocument.Parse(raw);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("kind", out var kindValue) || kindValue.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("reply", out var textValue) || textValue.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("source_ids", out var ids) || ids.ValueKind != JsonValueKind.Array) return false;
            var kind = kindValue.GetString()!;
            var text = textValue.GetString()?.Trim() ?? "";
            if (text.Length == 0 || kind is not ("answer" or "clarify" or "insufficient" or "conversation")) return false;
            if (text.Contains("{{", StringComparison.Ordinal)) { reason = "internal_marker"; return false; }
            var sources = new List<SourceMetadata>();
            foreach (var value in ids.EnumerateArray())
            {
                if (!value.TryGetInt64(out var id)) return false;
                var source = context.Sources.FirstOrDefault(s => s.ChunkId == id);
                if (source is null) { reason = "unknown_source"; return false; }
                if (!sources.Any(s => s.ChunkId == id)) sources.Add(source);
            }
            if (kind == "answer" && sources.Count == 0) { reason = "missing_source"; return false; }
            if (kind is "clarify" or "conversation" && sources.Count > 0) { reason = "unexpected_source"; return false; }
            if (kind == "clarify" && !text.Contains('?')) { reason = "missing_clarification"; return false; }
            if (!context.SourceQuestion && context.Evidence.Values.Any(e => CopiesPassage(text, e)))
            { reason = "copied_passage"; return false; }
            if (!context.SourceQuestion)
            {
                var evidence = sources.Select(s => context.Evidence[s.ChunkId]).ToArray();
                // A clarification may repeat a duration/date the person supplied.
                var numbers = evidence.SelectMany(BookGroundingGuard.Numbers)
                    .Concat(BookGroundingGuard.Numbers(userContext)).ToHashSet(StringComparer.Ordinal);
                if (BookGroundingGuard.Numbers(text).Any(n => !numbers.Contains(n)))
                { reason = "unsupported_numeric_parameter"; return false; }
            }
            reply = new(kind, text, sources);
            reason = "accepted";
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool CopiesPassage(string reply, string source)
    {
        var words = Regex.Matches(reply.ToLowerInvariant(), @"[\p{L}\p{N}]+",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Select(m => m.Value).ToArray();
        var original = " " + string.Join(' ', Regex.Matches(source.ToLowerInvariant(), @"[\p{L}\p{N}]+",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Select(m => m.Value)) + " ";
        // Short shared terms are expected. Long verbatim runs should be rewritten.
        for (var i = 0; i + 18 <= words.Length; i++)
            if (original.Contains(" " + string.Join(' ', words.Skip(i).Take(18)) + " ", StringComparison.Ordinal)) return true;
        return false;
    }
}
