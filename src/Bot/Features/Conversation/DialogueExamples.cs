using System.Text;
using Trivozhno.Infrastructure.Content;
using Trivozhno.Infrastructure.Groq;

namespace Trivozhno.Features.Conversation;

public sealed record ExampleMessage(string Role, string Content);
public sealed record DialogueExample(string Name, bool Enabled, ExampleMessage[] Messages);

public sealed class DialogueExamples
{
    private readonly ReloadingJsonFile<DialogueExample[]> file;

    public DialogueExamples(ILogger<DialogueExamples> log)
    {
        file = new(Path.Combine(AppContext.BaseDirectory, "Resources", "Conversation", "dialogue-examples.json"),
            [], Valid, log);
    }

    private static bool Valid(DialogueExample[] examples) => examples.Length <= 200 &&
        examples.All(e => e is not null && !string.IsNullOrWhiteSpace(e.Name) &&
            e.Messages is { Length: > 0 and <= 24 } &&
            e.Messages.Length % 2 == 0 &&
            e.Messages.Select((m, i) => m is not null &&
                m.Role == (i % 2 == 0 ? "user" : "assistant") &&
                !string.IsNullOrWhiteSpace(m.Content) && m.Content.Length <= 4000).All(x => x));

    public string Build(int tokenBudget)
    {
        var text = new StringBuilder("Зразки манери розмови, не факти про поточного користувача. " +
            "Не копіюй сюжети чи репліки; реагуй на реальну переписку.\n");
        var included = 0;
        foreach (var example in file.Read().Where(e => e.Enabled))
        {
            var block = "\n" + example.Name + "\n" + string.Join('\n', example.Messages.Select(m =>
                (m.Role == "user" ? "Людина: " : "Бот: ") + m.Content)) + "\n";
            // Keep complete dialogues. File order is explicit priority, no random sampling.
            if (TokenEstimate.Count(text.ToString() + block) > tokenBudget) continue;
            text.Append(block);
            included++;
        }
        return included == 0 ? "" : text.ToString();
    }
}
