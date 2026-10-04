// SPDX-License-Identifier: AGPL-3.0-only
// Adapted from SillyTavern 1.19.0 public/scripts/openai.js:
// preparePromptsForChatCompletion, populateChatCompletion, populateChatHistory,
// populateDialogueExamples, prepareOpenAIMessages. See third_party/SillyTavern/NOTICE.md.
// Copyright SillyTavern contributors. C# text-only adaptation, 2026-10-04.
using Trivozhno.Host;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Resources;

namespace Trivozhno.Infrastructure.SillyTavern;

public sealed class TavernPromptBuilder(Uk resources, BotOptions options, ILogger<TavernPromptBuilder> log)
{
    public string FirstMessage => resources.Tavern.Character.FirstMessage;
    public int ExampleReserve => resources.Tavern.ExampleReserve;
    public int InstructionReserve => resources.Tavern.InstructionReserve;
    public int InputLimit => Math.Min(options.InputBudget,
        Math.Min(options.TokensPerMinute, options.TokensPerDay) - options.TurnOutputBudget);

    public IReadOnlyList<AiMessage> Build(IReadOnlyList<AiMessage> history,
        IReadOnlyList<AiMessage> memory, IReadOnlyList<string> references)
    {
        if (history.Count == 0 || history[^1].Role != "user") throw new InvalidOperationException("Tavern needs the current user turn.");
        var config = resources.Tavern;
        var completion = new ChatCompletion();
        completion.SetTokenBudget(InputLimit + options.TurnOutputBudget, options.TurnOutputBudget);
        completion.ReserveBudget(3); // upstream assistant priming allowance
        var worldBefore = string.Join("\n\n", memory.Select(x => x.Content));
        var worldAfter = string.Join("\n\n", references);
        var newChat = new TavernMessage("newMainChat", new("system", config.Expand(config.Preset.NewChatPrompt)));
        completion.ReserveBudget(newChat.Tokens);

        for (var i = 0; i < config.Order.Length; i++)
        {
            var entry = config.Order[i];
            if (!entry.Enabled) continue;
            var collection = new MessageCollection(entry.Identifier);
            if (entry.Identifier is not ("chatHistory" or "dialogueExamples"))
            {
                collection.Collection.Add(new(entry.Identifier,
                    config.CreateInstruction(entry.Identifier, worldBefore, worldAfter)));
            }
            completion.Add(collection, i);
        }

        var exampleCount = 0;
        var historyCount = 0;
        if (config.Preset.PinExamples) { PopulateExamples(); PopulateHistory(); }
        else { PopulateHistory(); PopulateExamples(); }
        completion.FreeBudget(newChat);
        completion.Insert(newChat, "chatHistory", atStart: true);

        // Merge initial instructions/examples for GPT-OSS. PHI uses ST's
        // user-role fallback, retaining its actual post-history position.
        var messages = GroqMessageLayout.Prepare(completion.GetChat());
        var realHistory = messages.Where(m => m.Role is "user" or "assistant" && !m.IsApplicationPrompt).ToArray();
        var hasTail = messages[^1].IsApplicationPrompt;
        if (realHistory.Length == 0 || realHistory[^1] != history[^1] || TokenEstimate.Count(messages) > InputLimit)
            throw new ContextTooLargeException();
        log.LogInformation("SillyTavern prompt; upstream {Version}; history {History}; example blocks {Examples}; " +
            "examples available {Available}; memory blocks {Memory}; reference blocks {References}; " +
            "post-history instruction {Tail}; estimated tokens {Tokens}; current preserved True",
            "1.19.0", historyCount, exampleCount, config.Examples.Count, memory.Count, references.Count,
            hasTail, TokenEstimate.Count(messages));
        return messages;

        void PopulateHistory()
        {
            foreach (var message in history.Reverse())
            {
                var item = new TavernMessage("chatHistory-" + historyCount, message);
                if (!completion.CanAfford(item))
                {
                    if (historyCount == 0) throw new ContextTooLargeException();
                    break;
                }
                completion.Insert(item, "chatHistory", atStart: true);
                historyCount++;
            }
        }

        void PopulateExamples()
        {
            if (!config.Order.Any(x => x.Identifier == "dialogueExamples" && x.Enabled)) return;
            var used = 0;
            foreach (var block in config.Examples)
            {
                var items = new[] { new TavernMessage("newChat", new AiMessage("system", config.Expand(config.Preset.NewExampleChatPrompt))) }
                    .Concat(block.Select((x, i) => new TavernMessage($"dialogueExamples-{exampleCount}-{i}",
                        new AiMessage("system", x.Speaker + ": " + x.Content)))).ToArray();
                var cost = items.Sum(x => x.Tokens);
                if (used + cost > TavernConfiguration.ExampleTokenBudget || !completion.CanAffordAll(items)) break;
                foreach (var item in items) completion.Insert(item, "dialogueExamples");
                used += cost;
                exampleCount++;
            }
        }
    }
}
