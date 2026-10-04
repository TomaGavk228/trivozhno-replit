using Trivozhno.Features.Memory;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Resources;

namespace Trivozhno.Features.Conversation;

// A bounded interpretation of the exchange BEFORE retrieval and reply writing.
// The durable conversation record is still produced by the final writer.
public sealed class DialoguePlanner(IAiClient ai, BotOptions options, Uk resources, ILogger<DialoguePlanner> log)
{
    public const int HintReserve = 380;

    public async Task<AiTurnPlan?> Plan(ConversationContext context, CancellationToken ct)
    {
        if (!options.DialoguePlanning) return null;
        var actual = context.Messages.Where(x => x.Role is "user" or "assistant").ToArray();
        if (actual.Length == 0) return null;
        if (actual.Length == 1 && ChatResponder.ClassifyDialogueAct(actual[0].Content) == DialogueAct.Greeting)
            return null;

        var budget = Math.Min(2400, Math.Min(options.InputBudget,
            Math.Min(options.TokensPerMinute, options.TokensPerDay) - GroqClient.PlanCompletionTokens) - GroqClient.PlanSchemaReserve);
        var intro = resources.PlannerPrompt;
        // These notes have already passed the session, memory-version and mood
        // consent filters. Include preferences too, not just the last record.
        var notes = string.Join("\n\n", context.Messages.Where(x => x.Role == "system").Skip(1).Select(x => x.Content));
        if (notes.Length == 0) notes = context.ConversationState;
        if (notes.Length > 0)
            intro += "\n\nДовідкові нотатки; дослівна переписка важливіша:\n" +
                ConversationMemory.TrimToTokenBudget(notes, 400);
        intro += "\nКнижкових джерел попередньої відповіді: " + context.PreviousSources.Count(x => x.Type == "book") + ".";
        var system = new AiMessage("system", intro);
        var recent = new List<AiMessage> { actual[^1] };
        if (TokenEstimate.Count(recent.Prepend(system)) > budget)
        {
            log.LogInformation("Dialogue plan skipped; reason input_budget");
            return null;
        }
        // Keep complete adjacent exchanges, in order, with the current input intact.
        for (var i = actual.Length - 2; i >= 0 && recent.Count < 11; i--)
        {
            if (TokenEstimate.Count(recent.Prepend(actual[i]).Prepend(system)) > budget) break;
            recent.Insert(0, actual[i]);
        }
        if (recent.Count > 1 && recent[0].Role == "assistant") recent.RemoveAt(0);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.PlannerTimeout));
        try
        {
            var result = await ai.PlanTurn([system, .. recent], timeout.Token);
            if (result is null) return null;
            log.LogInformation("Dialogue plan ready; model {Model}; book mode {Mode}; history items {History}; tokens {Tokens}",
                result.Usage.Model, result.Plan.BookMode, recent.Count, result.Usage.Tokens);
            return result.Plan;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { log.LogWarning("Dialogue plan unavailable; reason deadline; using direct conversation"); return null; }
        catch (AiUnavailableException e) when (e.Reason is not ("authentication" or "permission_denied" or
            "model_permission_blocked_org" or "model_permission_blocked_project"))
        { log.LogWarning("Dialogue plan unavailable; reason {Reason}; using direct conversation", e.Reason); return null; }
        catch (ContextTooLargeException)
        { log.LogWarning("Dialogue plan unavailable; reason input_budget; using direct conversation"); return null; }
    }

    public static string WriterHint(AiTurnPlan plan) =>
        "Робоча підказка для цього ходу. Звір із дослівною перепискою; сформулюй власну живу репліку, " +
        "не переказуй цей запис людині.\nПрохання: " + plan.Request +
        "\nНаступна дія: " + plan.Move + (plan.Avoid.Length > 0 ? "\nУже не підійшло: " + plan.Avoid : "");
}
