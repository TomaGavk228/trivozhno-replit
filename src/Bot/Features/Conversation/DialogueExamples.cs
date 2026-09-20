using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Trivozhno.Infrastructure.Content;
using Trivozhno.Infrastructure.Groq;

namespace Trivozhno.Features.Conversation;

public sealed record ExampleMessage(string Role, string Content);
public sealed record DialogueExample(string Name, bool Enabled, ExampleMessage[] Messages,
    bool Core = false, string[]? Tags = null);

public sealed class DialogueExamples
{
    private readonly ReloadingJsonFile<DialogueExample[]> file;
    private readonly ILogger<DialogueExamples> log;

    public DialogueExamples(ILogger<DialogueExamples> log)
    {
        this.log = log;
        file = new(Path.Combine(AppContext.BaseDirectory, "Resources", "Conversation", "dialogue-examples.json"),
            [], Valid, log);
    }

    private static bool Valid(DialogueExample[] examples) => examples.Length <= 200 &&
        examples.All(e => e is not null && !string.IsNullOrWhiteSpace(e.Name) &&
            (e.Tags is null || e.Tags.Length <= 30 && e.Tags.All(t =>
                !string.IsNullOrWhiteSpace(t) && t.Length <= 100)) &&
            e.Messages is { Length: > 0 and <= 24 } &&
            e.Messages.Length % 2 == 0 &&
            e.Messages.Select((m, i) => m is not null &&
                m.Role == (i % 2 == 0 ? "user" : "assistant") &&
                !string.IsNullOrWhiteSpace(m.Content) && m.Content.Length <= 4000).All(x => x));

    public string Build(int tokenBudget) => Build(tokenBudget, []);

    public string Build(int tokenBudget, IReadOnlyList<AiMessage> conversation)
    {
        var examples = file.Read();
        var recent = conversation.Where(m => m.Role == "user").TakeLast(3).ToArray();
        var latest = recent.Length == 0 ? "" : Normalize(recent[^1].Content);
        var previous = Normalize(string.Join(' ', recent.SkipLast(1).Select(m => m.Content)));
        var ranked = examples.Select((example, index) => new
            { Example = example, Index = index, Score = Score(example, latest, previous) })
            .Where(x => x.Example.Enabled).OrderByDescending(x => x.Score).ThenBy(x => x.Index).ToArray();
        var text = new StringBuilder("Це знеособлені фрагменти окремих розмов, що показують манеру переписки. " +
            "Вони не описують поточного користувача. Перенось спосіб реагування, не сюжети, факти чи готові репліки.\n");
        var included = new List<int>();

        // At most two compact anchors, leaving at least half the budget for relevant episodes.
        foreach (var item in ranked.Where(x => x.Example.Core).OrderBy(x => x.Index).Take(2))
            Add(item.Example, item.Index, tokenBudget / 2);
        foreach (var item in ranked.Where(x => x.Score > 0))
        {
            if (included.Count >= 4) break;
            Add(item.Example, item.Index, tokenBudget);
        }
        // A new conversation needs no stored profile. Use an anchor if nothing matched.
        if (included.Count == 0)
            foreach (var item in ranked.OrderByDescending(x => x.Example.Core).ThenBy(x => x.Index))
                if (Add(item.Example, item.Index, tokenBudget)) break;

        // Never log user text or example contents. The digest identifies the loaded snapshot.
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(examples)))[..12];
        log.LogInformation("Dialogue examples: snapshot {Snapshot}; selected indexes {Indexes}; budget {Budget}; estimated {Tokens}",
            digest, string.Join(',', included), tokenBudget, included.Count == 0 ? 0 : TokenEstimate.Count(text.ToString()));
        return included.Count == 0 ? "" : text.ToString();

        bool Add(DialogueExample example, int index, int limit)
        {
            if (included.Contains(index)) return false;
            var block = "\nОкремий діалог:\n" + string.Join('\n', example.Messages.Select(m =>
                (m.Role == "user" ? "Людина: " : "Бот: ") + m.Content)) + "\n";
            if (TokenEstimate.Count(text.ToString() + block) > limit) return false;
            text.Append(block);
            included.Add(index);
            return true;
        }
    }

    private static int Score(DialogueExample example, string latest, string previous)
    {
        // Simple, editable lexical retrieval; no classifier call and no random sampling.
        var tags = (example.Tags ?? []).Select(Normalize).Where(t => t.Length > 0).Distinct().ToArray();
        var words = Words(string.Join(' ', example.Messages.Where(m => m.Role == "user").Select(m => m.Content)));
        return 12 * tags.Count(t => (" " + latest + " ").Contains(" " + t + " ")) +
            2 * tags.Count(t => (" " + previous + " ").Contains(" " + t + " ")) +
            3 * Words(latest).Intersect(words).Count() + Words(previous).Intersect(words).Count();
    }

    private static string Normalize(string text) => Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
    private static HashSet<string> Words(string text) => Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(w => w.Length >= 5 && w is not "просто" and not "нічого" and not "якийсь" and not "щось")
        .ToHashSet(StringComparer.Ordinal);
}
