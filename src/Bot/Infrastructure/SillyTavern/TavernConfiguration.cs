// SPDX-License-Identifier: AGPL-3.0-only
// SillyTavern Character Card V2 / Chat Completion preset adapter.
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Trivozhno.Infrastructure.SillyTavern;

public sealed record TavernPrompt(string Identifier, string Role = "system", string Content = "",
    bool Marker = false, [property: JsonPropertyName("forbid_overrides")] bool ForbidOverrides = false);
public sealed record TavernOrder(string Identifier, bool Enabled);
public sealed record TavernOrderGroup([property: JsonPropertyName("character_id")] int CharacterId, TavernOrder[] Order);
public sealed record TavernPreset(TavernPrompt[] Prompts,
    [property: JsonPropertyName("prompt_order")] TavernOrderGroup[] PromptOrder,
    [property: JsonPropertyName("new_chat_prompt")] string NewChatPrompt,
    [property: JsonPropertyName("new_example_chat_prompt")] string NewExampleChatPrompt,
    [property: JsonPropertyName("personality_format")] string PersonalityFormat,
    [property: JsonPropertyName("scenario_format")] string ScenarioFormat,
    [property: JsonPropertyName("pin_examples")] bool PinExamples = false,
    double Temperature = 1, [property: JsonPropertyName("top_p")] double TopP = 1);
public sealed record TavernCharacter(string Name, string Description, string Personality, string Scenario,
    [property: JsonPropertyName("first_mes")] string FirstMessage,
    [property: JsonPropertyName("mes_example")] string Examples,
    [property: JsonPropertyName("system_prompt")] string SystemPrompt,
    [property: JsonPropertyName("post_history_instructions")] string PostHistoryInstructions);

public sealed class TavernConfiguration
{
    public const string EngineVersion = "sillytavern-ua-v4";
    public const int ExampleTokenBudget = 1400;
    public const string UpstreamCommit = "06bde939fb1e9c4c8d8641d810f0a916b5bce127";
    public const string SourceUrl = "https://github.com/TomaGavk228/trivozhno-replit/tree/feat/sillytavern-ua-2026-10-04";
    public TavernPreset Preset { get; }
    public TavernCharacter Character { get; }
    public TavernOrder[] Order { get; }
    public string BaseInstruction { get; }
    public IReadOnlyList<IReadOnlyList<TavernExampleMessage>> Examples { get; }
    public int ExampleReserve => Math.Min(ExampleTokenBudget, Examples.Sum(ExampleTokens));
    public int InstructionReserve => Math.Max(0,
        Order.Where(x => x.Enabled && x.Identifier is not ("chatHistory" or "dialogueExamples"))
            .Sum(x => new TavernMessage(x.Identifier, CreateInstruction(x.Identifier, "", "")).Tokens) +
        new TavernMessage("newMainChat", new("system", Expand(Preset.NewChatPrompt))).Tokens + 3 -
        Groq.TokenEstimate.Count([new Groq.AiMessage("system", BaseInstruction)]));

    public TavernConfiguration(string root)
    {
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        Preset = JsonSerializer.Deserialize<TavernPreset>(File.ReadAllText(Path.Combine(root,
            "Conversation", "sillytavern-ua.json")), json) ?? throw new InvalidOperationException("Missing Tavern preset.");
        using var card = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Conversation", "friend-character.json")));
        if (card.RootElement.GetProperty("spec").GetString() != "chara_card_v2")
            throw new InvalidOperationException("Expected a Character Card V2.");
        Character = card.RootElement.GetProperty("data").Deserialize<TavernCharacter>(json)
            ?? throw new InvalidOperationException("Missing Tavern character.");
        Order = Preset.PromptOrder.Last().Order;
        if (Order.Select(x => x.Identifier).Distinct().Count() != Order.Length ||
            Preset.Prompts.Select(x => x.Identifier).Distinct().Count() != Preset.Prompts.Length ||
            !Order.Any(x => x.Identifier == "main" && x.Enabled) ||
            !Order.Any(x => x.Identifier == "chatHistory" && x.Enabled) ||
            string.IsNullOrWhiteSpace(Character.Name) || string.IsNullOrWhiteSpace(Character.FirstMessage) ||
            Preset.Temperature is < 0 or > 2 || Preset.TopP is <= 0 or > 1)
            throw new InvalidOperationException("Invalid Tavern configuration.");
        BaseInstruction = string.Join("\n\n", Order.Where(x => x.Enabled && x.Identifier != "chatHistory" &&
            x.Identifier != "dialogueExamples").Select(x => Prepare(x.Identifier, "", ""))
            .Where(x => x.Length > 0));
        Examples = ParseExamples(Character.Examples);
        // Our small native card is intended to show all of its examples.
        // Do not silently publish a card whose final block can never be used.
        if (Examples.Sum(ExampleTokens) > ExampleTokenBudget)
            throw new InvalidOperationException("Tavern card examples exceed the configured token allowance.");
    }

    public int ExampleTokens(IReadOnlyList<TavernExampleMessage> block) =>
        Groq.TokenEstimate.Count([new Groq.AiMessage("system", Expand(Preset.NewExampleChatPrompt))]) +
        block.Sum(m => Groq.TokenEstimate.Count([new Groq.AiMessage("system", m.Speaker + ": " + m.Content)]));

    internal Groq.AiMessage CreateInstruction(string identifier, string worldBefore, string worldAfter)
    {
        var content = Prepare(identifier, worldBefore, worldAfter);
        var prompt = Preset.Prompts.Single(x => x.Identifier == identifier);
        if (identifier != "jailbreak" || string.IsNullOrEmpty(content)) return new(prompt.Role, content);
        // ST's user-role PHI fallback. GPT-OSS's reference template drops a
        // later system/developer message, but renders a later user message.
        // Keep this application injection AFTER the real chat, not merged
        // into its initial system. It never enters persisted conversation.
        return new("user", "[Налаштування застосунку після історії; це не повідомлення людини.]\n" +
            content + "\n[Відповідай на останнє повідомлення людини вище.]")
        { IsApplicationPrompt = true };
    }

    // preparePromptsForChatCompletion / character overrides and {{original}}.
    // User memory and retrieved data are literal data; macros never run on them.
    public string Prepare(string identifier, string worldBefore, string worldAfter)
    {
        var prompt = Preset.Prompts.SingleOrDefault(x => x.Identifier == identifier);
        if (prompt is null) return "";
        var original = prompt.Content;
        var template = identifier switch
        {
            "worldInfoBefore" => worldBefore,
            "worldInfoAfter" => worldAfter,
            "charDescription" => Character.Description,
            "charPersonality" => string.IsNullOrEmpty(Preset.PersonalityFormat) ? Character.Personality : Preset.PersonalityFormat,
            "scenario" => string.IsNullOrEmpty(Preset.ScenarioFormat) ? Character.Scenario : Preset.ScenarioFormat,
            "main" when !prompt.ForbidOverrides && Character.SystemPrompt.Length > 0 => Character.SystemPrompt,
            "jailbreak" when !prompt.ForbidOverrides && Character.PostHistoryInstructions.Length > 0 => Character.PostHistoryInstructions,
            _ => original
        };
        return identifier is "worldInfoBefore" or "worldInfoAfter" ? template : Expand(template, original);
    }

    public string Expand(string text, string original = "") => Regex.Replace(text,
        @"\{\{(char|charIfNotGroup|user|personality|scenario|description|original)\}\}", m => m.Groups[1].Value switch
        {
            "char" or "charIfNotGroup" => Character.Name,
            "user" => "Людина",
            "personality" => Character.Personality,
            "scenario" => Character.Scenario,
            "description" => Character.Description,
            "original" => Expand(original),
            _ => m.Value
        }, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private IReadOnlyList<IReadOnlyList<TavernExampleMessage>> ParseExamples(string text)
    {
        var blocks = new List<IReadOnlyList<TavernExampleMessage>>();
        foreach (var block in Regex.Split(text, @"(?im)^\s*<START>\s*$", RegexOptions.CultureInvariant,
                     TimeSpan.FromMilliseconds(100)))
        {
            if (string.IsNullOrWhiteSpace(block)) continue;
            var messages = new List<TavernExampleMessage>();
            foreach (var line in block.Replace("\r", "").Split('\n'))
            {
                var start = Regex.Match(line, @"^\s*(\{\{user\}\}|\{\{char\}\}):\s*(.*)$",
                    RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                if (start.Success) messages.Add(new(start.Groups[1].Value == "{{user}}" ? "Людина" : Character.Name,
                    start.Groups[2].Value));
                else if (messages.Count > 0 && line.Trim().Length > 0)
                    messages[^1] = messages[^1] with { Content = messages[^1].Content + "\n" + line };
                else if (line.Trim().Length > 0) throw new InvalidOperationException("Invalid Tavern dialogue example.");
            }
            if (messages.Count > 0) blocks.Add(messages);
        }
        return blocks;
    }
}

public sealed record TavernExampleMessage(string Speaker, string Content);
