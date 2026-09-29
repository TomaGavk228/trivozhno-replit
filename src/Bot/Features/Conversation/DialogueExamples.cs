using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        file = new(Path.Combine(AppContext.BaseDirectory, "Resources", "Conversation", "character-examples.json"),
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

    private static IEnumerable<(DialogueExample Example, int Index)> SelectPack(
        DialogueExample[] examples)
    {
        // A stable, diverse voice primer. Keyword selection previously treated
        // ordinary words as triggers and copied unrelated examples into turns.
        return examples.Select((example, index) => (Example: example, Index: index))
            .Where(x => x.Example.Enabled && x.Example.Core).Take(4);
    }

    public string Build(int tokenBudget)
    {
        var examples = file.Read();
        var text = new StringBuilder("ЗРАЗКИ МАНЕРИ. Кожен блок — окрема вигадана розмова. " +
            "Це не історія поточного користувача. Перенось лише спосіб реагувати, а не події, слова чи особисті факти.\n");
        var included = new List<int>();
        foreach (var item in SelectPack(examples))
        {
            var block = "\nОкремий приклад:\n" + string.Join('\n', item.Example.Messages.Select(m =>
                (m.Role == "user" ? "Людина: " : "Співрозмовник: ") + m.Content)) + "\n";
            if (TokenEstimate.Count(text.ToString() + block) > tokenBudget) continue;
            text.Append(block);
            included.Add(item.Index);
        }

        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(examples)))[..12];
        log.LogInformation("Dialogue examples: snapshot {Snapshot}; selected indexes {Indexes}; budget {Budget}; estimated {Tokens}",
            digest, string.Join(',', included), tokenBudget, included.Count == 0 ? 0 : TokenEstimate.Count(text.ToString()));
        return included.Count == 0 ? "" : text.ToString();
    }

    // Archive access for local copy telemetry only; never injected into chat.
    public IReadOnlyList<DialogueExample> Snapshot() => file.Read();
}
