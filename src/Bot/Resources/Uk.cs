using System.Text.Json;

namespace Trivozhno.Resources;

public sealed record StorySeed(string Id, string[] Tags, string Story);

public sealed class Uk
{
    private readonly Dictionary<string, string> strings;
    private readonly Dictionary<string, string[]> aliases;
    public string ChatPrompt { get; }
    public IReadOnlyList<StorySeed> StoryBank { get; }
    public string SummaryPrompt { get; }

    public Uk()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Resources");
        strings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "uk.json")))!;
        aliases = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(Path.Combine(root, "button-aliases.json")))!;
        ChatPrompt = File.ReadAllText(Path.Combine(root, "Prompts", "chat-v1.txt"));

        var storyPath = Path.Combine(root, "StoryBank", "stories.jsonl");
        var storyJson = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        StoryBank = File.Exists(storyPath)
            ? File.ReadLines(storyPath)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => JsonSerializer.Deserialize<StorySeed>(x, storyJson))
                .Where(x => x is not null)
                .Select(x => x!)
                .ToArray()
            : [];

        Console.WriteLine(
        $"[PROMPT] File={Path.Combine(root, "Prompts", "chat-v1.txt")} " +
        $"Chars={ChatPrompt.Length} " +
        $"SHA256={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ChatPrompt)))} " +
        $"StoryCount={StoryBank.Count}");
        SummaryPrompt = File.ReadAllText(Path.Combine(root, "Prompts", "summary-v1.txt"));
    }

    public string this[string key] => strings[key];
    public string Format(string key, params object[] args) => string.Format(System.Globalization.CultureInfo.GetCultureInfo("uk-UA"), this[key], args);
    public bool Is(string key, string? text) => text == this[key] || aliases.TryGetValue(key, out var old) && old.Contains(text);
    public string MoodName(int value) => this[$"mood.{value}"];
}
