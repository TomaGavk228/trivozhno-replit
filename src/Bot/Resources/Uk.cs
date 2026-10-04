using System.Text.Json;

namespace Trivozhno.Resources;

public sealed class Uk
{
    private readonly Dictionary<string, string> strings;
    private readonly Dictionary<string, string[]> aliases;
    public string ChatPrompt { get; }
    public string SummaryPrompt { get; }
    public string PlannerPrompt { get; }
    public string OpeningMessage { get; }

    public Uk()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Resources");
        strings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "uk.json")))!;
        aliases = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(Path.Combine(root, "button-aliases.json")))!;
        var characterPath = Path.Combine(root, "Conversation", "friend-character.json");
        using var character = JsonDocument.Parse(File.ReadAllText(characterPath));
        var data = character.RootElement.GetProperty("data");
        ChatPrompt = string.Join("\n\n", new[] { "description", "personality", "scenario" }
            .Select(key => data.GetProperty(key).GetString())) + "\n\n" +
            File.ReadAllText(Path.Combine(root, "Prompts", "chat-v1.txt"));
        OpeningMessage = data.GetProperty("first_mes").GetString() ?? "";
        PlannerPrompt = File.ReadAllText(Path.Combine(root, "Prompts", "plan-v1.txt"));
        SummaryPrompt = File.ReadAllText(Path.Combine(root, "Prompts", "summary-v1.txt"));

        Console.WriteLine(
            $"[PROMPT] File={Path.Combine(root, "Prompts", "chat-v1.txt")} " +
            $"Character={characterPath} Engine=friend-dialogue-v2 Schema=friend_exchange " +
            $"Chars={ChatPrompt.Length} " +
            $"SHA256={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ChatPrompt)))}");
    }

    public string this[string key] => strings[key];
    public string Format(string key, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.GetCultureInfo("uk-UA"), this[key], args);
    public bool Is(string key, string? text) =>
        text == this[key] || aliases.TryGetValue(key, out var old) && old.Contains(text);
    public string MoodName(int value) => this[$"mood.{value}"];
}
