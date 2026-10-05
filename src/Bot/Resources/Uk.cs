using System.Text.Json;

namespace Trivozhno.Resources;

public sealed class Uk
{
    private readonly Dictionary<string, string> strings;
    private readonly Dictionary<string, string[]> aliases;
    public Uk()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Resources");
        strings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "uk.json")))!;
        aliases = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(Path.Combine(root, "button-aliases.json")))!;

    }

    public string this[string key] => strings[key];
    public string Format(string key, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.GetCultureInfo("uk-UA"), this[key], args);
    public bool Is(string key, string? text) =>
        text == this[key] || aliases.TryGetValue(key, out var old) && old.Contains(text);
    public string MoodName(int value) => this[$"mood.{value}"];
}
