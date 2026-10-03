using System.Text.RegularExpressions;
using Trivozhno.Features.Memory;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Telegram;

namespace Trivozhno.Features.Conversation;

// Observations only: no acceptance threshold, retry, or model request.
public sealed class ReplyDiagnostics(DialogueExamples examples, ILogger<ReplyDiagnostics> log)
{
    private readonly object gate = new();
    private IReadOnlyList<DialogueExample>? snapshot;
    private HashSet<string> archiveGrams = [];

    public void Record(ConversationContext context, AiResult result, int bookPassages, int citations)
    {
        HashSet<string> archive;
        var latest = examples.Snapshot();
        lock (gate)
        {
            if (!ReferenceEquals(snapshot, latest))
            {
                archiveGrams = latest.SelectMany(x => x.Messages).Where(x => x.Role == "assistant")
                    .SelectMany(x => Grams(Words(x.Content))).ToHashSet(StringComparer.Ordinal);
                snapshot = latest;
            }
            archive = archiveGrams;
        }
        var words = Words(result.Text);
        var grams = Grams(words).ToHashSet(StringComparer.Ordinal);
        var previous = context.Messages.Where(x => x.Role == "assistant").TakeLast(3)
            .SelectMany(x => Grams(Words(x.Content))).ToHashSet(StringComparer.Ordinal);
        var archiveOverlap = grams.Count(archive.Contains);
        var recentOverlap = grams.Count(previous.Contains);
        log.LogInformation("Reply observations; model {Model}; words {Words}; bubbles {Bubbles}; " +
            "5grams {Grams}; archive overlap {Archive}; recent overlap {Recent}; " +
            "book passages {Passages}; book references {Citations}; input tokens {Input}; output tokens {Output}",
            result.Model, words.Length, TextSplitter.SplitChat(result.Text).Count,
            grams.Count, archiveOverlap, recentOverlap, bookPassages, citations,
            result.PromptTokens, result.CompletionTokens);
    }

    private static string[] Words(string text) => Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Select(m => m.Value).Take(1200).ToArray();

    private static IEnumerable<string> Grams(string[] words)
    {
        for (var i = 0; i + 5 <= words.Length; i++) yield return string.Join(" ", words, i, 5);
    }
}
