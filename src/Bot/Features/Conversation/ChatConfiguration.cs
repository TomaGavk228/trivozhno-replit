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
    public required double Temperature { get; init; }
    public required double TopP { get; init; }
    public required int MaxCompletionTokens { get; init; }
    public required int InputTokenBudget { get; init; }
    public required int HistoryTurns { get; init; }
    public int MemoryTokenBudget { get; init; } = 1000;
}

public sealed record ChatConfigurationSnapshot(string Instruction, ChatGenerationSettings Generation, string Hash, int ExampleCount);

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
                var examplesText = ReadFile("examples.txt");
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
                    string.IsNullOrWhiteSpace(settings.Model) || settings.Model.Length > 100 ||
                    settings.ReasoningEffort is not (null or "low" or "medium" or "high" or "none" or "default") ||
                    (settings.Model.StartsWith("openai/gpt-oss-", StringComparison.Ordinal) &&
                        settings.ReasoningEffort is not ("low" or "medium" or "high")) ||
                    !double.IsFinite(settings.Temperature) || settings.Temperature is < 0 or > 2 ||
                    !double.IsFinite(settings.TopP) || settings.TopP is <= 0 or > 1 ||
                    settings.MaxCompletionTokens is < 256 or > 4000 ||
                    settings.InputTokenBudget is < 1000 or > 32_000 || settings.HistoryTurns is < 1 or > 40 ||
                    settings.MemoryTokenBudget is < 0 or > 2000 ||
                    examples.Length > 30 || examples.Any(e => !ValidExample(e)))
                    throw new InvalidDataException();
                var instruction = prompt;
                if (examples.Length > 0)
                {
                    instruction += "\n\nНижче окремі вигадані діалоги. Репліки «Друг» показують дружню манеру, ритм і мову. " +
                        "Це не ваша історія, не факти про співрозмовника і не твоя біографія. " +
                        "Учасники й ситуації різні; не перенось їхній досвід у поточну розмову. " +
                        "Фрази не потрібно повторювати. Реагуй на справжню розмову після цього блоку.\n";
                    foreach (var example in examples)
                        instruction += "\n[Окремий приклад]\n" + example + "\n";
                    instruction += "\n[Кінець прикладів. Далі — справжня поточна розмова.]";
                }
                instruction += "\n\n" + ChatReplyFormat.Instruction;
                current = new(instruction, settings, hash[..12], examples.Length);
                log.LogInformation("Chat configuration loaded; directory {Directory}; SHA {Hash}; examples {Examples}; model {Model}",
                    DirectoryPath, current.Hash, examples.Length, settings.Model);
                return current;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                if (current is null) throw new InvalidOperationException(
                    $"Invalid chat configuration in {DirectoryPath}. Check prompt.txt, examples.txt and generation.json.");
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
