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

    private static IEnumerable<(DialogueExample Example, int Index)> SelectPack(
        DialogueExample[] examples, IReadOnlyList<AiMessage> conversation)
    {
        var latest = conversation.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        var recent = conversation.TakeLast(6).Where(m => m.Role == "user")
            .SkipLast(1).Select(m => m.Content).ToArray();
        return examples.Select((example, index) => (Example: example, Index: index,
                Score: (example.Tags ?? []).Sum(tag =>
                    Matches(latest, tag) ? 10 : recent.Any(m => Matches(m, tag)) ? 1 : 0)))
            .Where(x => x.Example.Enabled && x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Example.Core)
            .ThenBy(x => x.Index)
            // Demonstrations are for edge cases, not a permanent voice primer.
            // One relevant example is enough; ordinary turns get zero.
            .Take(1)
            .Select(x => (x.Example, x.Index));
    }

    private static bool Matches(string message, string tag) =>
        Regex.IsMatch(message, @"(?<!\p{L})" + Regex.Escape(tag) + @"(?!\p{L})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public IReadOnlyList<AiMessage> BuildMessages(int tokenBudget, IReadOnlyList<AiMessage> conversation)
    {
        var examples = file.Read();
        var messages = new List<AiMessage>();
        var included = new List<int>();
        foreach (var item in SelectPack(examples, conversation))
        {
            // Mark every example-user message, including within a multi-turn
            // episode. Real user text is never prefixed, rewritten or replaced.
            var block = item.Example.Messages.Select(m => new AiMessage(m.Role,
                m.Role == "user" ? "[Зразок манери; окрема розмова]\n" + m.Content : m.Content)).ToArray();
            if (TokenEstimate.Count(messages.Concat(block)) > tokenBudget) continue;
            messages.AddRange(block);
            included.Add(item.Index);
        }
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(examples)))[..12];
        log.LogInformation("Dialogue examples: mode role-pairs; snapshot {Snapshot}; selected indexes {Indexes}; budget {Budget}; estimated {Tokens}",
            digest, string.Join(',', included), tokenBudget, TokenEstimate.Count(messages));
        return messages;
    }

    public string Build(int tokenBudget, IReadOnlyList<AiMessage> conversation)
    {
        var examples = file.Read();
        // The same relevance rule applies to the legacy text representation.
        var text = new StringBuilder("Це окремі знеособлені приклади манери переписки, не історія поточного користувача. " +
            "Перенось ритм і увагу до сказаного. Факти, теми й звертання з прикладів не перенось у справжню розмову.\n");
        var included = new List<int>();
        foreach (var item in SelectPack(examples, conversation))
        {
            var block = "\nОкремий приклад:\n" + string.Join('\n', item.Example.Messages.Select(m =>
                (m.Role == "user" ? "Людина: " : "Співрозмовник: ") + m.Content)) + "\n";
            // Keep a prefix of the same pack when space is tight, not a different pack.
            if (TokenEstimate.Count(text.ToString() + block) > tokenBudget) continue;
            text.Append(block);
            included.Add(item.Index);
        }

        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(examples)))[..12];
        log.LogInformation("Dialogue examples: mode fixed-text; snapshot {Snapshot}; selected indexes {Indexes}; budget {Budget}; estimated {Tokens}",
            digest, string.Join(',', included), tokenBudget, included.Count == 0 ? 0 : TokenEstimate.Count(text.ToString()));
        return included.Count == 0 ? "" : text.ToString();
    }
}
