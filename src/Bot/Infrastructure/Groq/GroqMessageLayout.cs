namespace Trivozhno.Infrastructure.Groq;

// GPT-OSS's reference template extracts one initial system/developer message.
// Later instruction messages are not rendered by that template. Do not depend
// on a provider combining them: assemble the application context explicitly.
public static class GroqMessageLayout
{
    public static IReadOnlyList<AiMessage> Prepare(IReadOnlyList<AiMessage> messages)
    {
        var instructions = messages.Where(IsInstruction).ToArray();
        if (instructions.Length == 0 ||
            instructions.Length == 1 && messages[0].Role == "system")
            return messages;

        var combined = string.Join("\n\n", instructions.Select(m => m.Content));
        // Preserve contents, roles and chronological order of actual conversation.
        // Context blocks already label examples/memory/sources as reference data.
        return [new AiMessage("system", combined), .. messages.Where(m => !IsInstruction(m))];
    }

    private static bool IsInstruction(AiMessage message) => message.Role is "system" or "developer";
}
