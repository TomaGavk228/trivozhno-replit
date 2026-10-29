using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trivozhno.Features.Memory;

namespace Trivozhno.Features.Conversation;

public sealed record ChatGenerationSettings
{
    public required string Model { get; init; }
    public required string? ReasoningEffort { get; init; }
    // Accepted only for older CHAT_CONTENT_PATH files; this model ignores sampling controls.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TopP { get; init; }
    public required int MaxCompletionTokens { get; init; }
    public required int InputTokenBudget { get; init; }
    public required int HistoryTurns { get; init; }
    public int MemoryTokenBudget { get; init; } = 3000;
}

public sealed record ChatConfigurationSnapshot(string Prompt, IReadOnlyList<string> Examples, string Format,
    ChatGenerationSettings Generation, string Hash)
{
    public const int ExamplesPerTurn = 4;
    public int ExampleCount => Examples.Count;

    // Each turn sees a different small subset, so the model learns the tone
    // instead of replaying the same few scripted lines. The seed keeps one
    // turn's retries on the same subset.
    public string Instruction(long seed)
    {
        var instruction = Prompt;
        if (Examples.Count > 0)
        {
            var random = new Random(unchecked((int)seed ^ (int)(seed >> 32)));
            var chosen = Examples.OrderBy(_ => random.Next()).Take(ExamplesPerTurn);
            instruction += "\n\nПРИКЛАДИ\n\nКілька окремих вигаданих діалогів, щоб показати тон і різну форму відповідей. Це не сценарії: не повторюй їхніх слів, фактів і ситуацій, відповідай по-своєму на справжню розмову.";
            foreach (var example in chosen)
                instruction += "\n\n" + example;
            instruction += "\n\nДалі справжня розмова. Відповідай на конкретні слова людини, як відповів би її друг.";
        }
        return instruction + "\n\n" + Format;
    }
}

// All files form one validated snapshot. Bad live edits retain the last valid
// snapshot; bad startup files fail explicitly instead of using a hidden prompt.
public sealed class ChatConfiguration
{
    private readonly object gate = new();
    private readonly ILogger<ChatConfiguration> log;
    public string DirectoryPath { get; }
    private ChatConfigurationSnapshot? current;
    private string? observedHash;
    private bool readFailureReported;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public ChatConfiguration(IConfiguration configuration, ILogger<ChatConfiguration> logger)
    {
        log = logger;
        var supplied = configuration["CHAT_CONTENT_PATH"];
        var source = Path.Combine(Environment.CurrentDirectory, "src", "Bot", "Resources", "Chat");
        DirectoryPath = Path.GetFullPath(!string.IsNullOrWhiteSpace(supplied) ? supplied :
            Directory.Exists(source) ? source : Path.Combine(AppContext.BaseDirectory, "Resources", "Chat"));
        _ = Read();
    }

    public ChatConfigurationSnapshot Read()
    {
        lock (gate)
        {
            try
            {
                string ReadFile(string name)
                {
                    var path = Path.Combine(DirectoryPath, name);
                    if (new FileInfo(path).Length > 200_000) throw new InvalidDataException();
                    return File.ReadAllText(path);
                }
                var prompt = ReadFile("prompt.txt").Trim();
                // Optional bank; a missing file means no examples.
                var examplesText = File.Exists(Path.Combine(DirectoryPath, "examples.txt")) ? ReadFile("examples.txt") : string.Empty;
                var generationText = ReadFile("generation.json");
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new[] { prompt, examplesText, generationText }))));
                readFailureReported = false;
                if (hash == observedHash && current is not null) return current;
                observedHash = hash;
                var settings = JsonSerializer.Deserialize<ChatGenerationSettings>(generationText, Json);
                var examples = examplesText.Replace("\r\n", "\n").Replace('\r', '\n')
                    .Split("\n---\n", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (prompt.Length is < 40 or > 20_000 || settings is null ||
                    settings.Model != "gemini-3.5-flash-lite" ||
                    settings.ReasoningEffort is not ("minimal" or "low" or "medium" or "high") ||
                    settings.MaxCompletionTokens is < 256 or > 4000 ||
                    settings.InputTokenBudget is < 1000 or > 64_000 || settings.HistoryTurns is < 1 or > 40 ||
                    settings.MemoryTokenBudget is < 500 or > 6000 ||
                    examples.Length > 40 || examples.Any(e => !ValidExample(e)))
                    throw new InvalidDataException();
                current = new(prompt, examples, ChatReplyFormat.InstructionFor(settings.Model), settings, hash[..12]);
                if (settings.Temperature is not null || settings.TopP is not null)
                    log.LogWarning("Legacy temperature/topP settings are ignored for gemini-3.5-flash-lite; remove them from generation.json");
                log.LogInformation("Chat configuration loaded; directory {Directory}; SHA {Hash}; examples {Examples}; model {Model}",
                    DirectoryPath, current.Hash, examples.Length, settings.Model);
                return current;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                if (current is null) throw new InvalidOperationException(
                    $"Invalid chat configuration in {DirectoryPath}. Check prompt.txt and generation.json.");
                if (!readFailureReported)
                    log.LogWarning("Chat configuration edit rejected; directory {Directory}; category {Category}; keeping SHA {Hash}",
                        DirectoryPath, e.GetType().Name, current.Hash);
                readFailureReported = true;
                return current;
            }
        }
    }

    private static bool ValidExample(string example)
    {
        var lines = example.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return lines.Length is >= 2 and <= 12 && lines[0].StartsWith("Людина: ", StringComparison.Ordinal) &&
            lines[^1].StartsWith("Друг: ", StringComparison.Ordinal) && lines.All(line =>
                line.Length <= 3000 &&
                (line.StartsWith("Людина: ", StringComparison.Ordinal) && line.Length > "Людина: ".Length ||
                 line.StartsWith("Друг: ", StringComparison.Ordinal) && line.Length > "Друг: ".Length));
    }
}
