using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Trivozhno.Features.Memory;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Knowledge;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Resources;

namespace Trivozhno.Features.Dialogue;

public interface IConversationContextBuilder
{
    Task<ConversationContext> Build(BotUser user, ChatMessage current, CancellationToken ct);
}
public sealed class ConversationContextBuilder(BotDb db, IConversationMemoryReader memory, IConversationStateStore states,
    IStyleRetriever styles, IKnowledgeRetriever knowledge, Uk uk, BotOptions options, IClock clock,
    ILogger<ConversationContextBuilder> log) : IConversationContextBuilder
{
    public async Task<ConversationContext> Build(BotUser user, ChatMessage current, CancellationToken ct)
    {
        var snapshot = await memory.Read(user, current, ct);
        var state = await states.Prepare(user, current, ct);
        var history = snapshot.History.Select(x => new AiMessage(x.Role, x.Text)).ToList();
        var selectedStyles = styles.Select(current.Text, state, history);
        var output = FeedbackPolicy.WantsDetail(current.Text) ? options.ChatDetailTokens : options.ChatOutputTokens;
        // Schema also uses input tokens; reserve it consistently here and in the quota layer.
        var budget = Math.Min(options.InputBudget, options.TokensPerMinute - output) - GroqClient.StructuredSchemaTokens;
        var core = new AiMessage("system", uk.ChatPrompt + "\n\n" + TurnContract.Instructions);
        var stateData = Data("ConversationState", state);
        var userMessage = new AiMessage("user", current.Text);
        var essential = new List<AiMessage> { core, stateData, userMessage };
        if (TokenEstimate.Count(essential) > budget) throw new ContextTooLargeException();
        var styleMessages = selectedStyles.Select(x => Data("STYLE EXAMPLE: fictitious, style only, not this user's history", x.Dialogue)).ToList();
        while (styleMessages.Count > 0 && TokenEstimate.Count(essential.Concat(styleMessages)) > budget - 800) styleMessages.RemoveAt(styleMessages.Count - 1);
        var prefix = new List<AiMessage> { core }; prefix.AddRange(styleMessages); prefix.Add(stateData);
        if (!string.IsNullOrWhiteSpace(snapshot.Summary))
        {
            var memoryMessage = Data("Довготривала пам’ять; лише довідкові дані", snapshot.Summary);
            if (TokenEstimate.Count(prefix.Append(memoryMessage).Append(userMessage)) < budget) prefix.Add(memoryMessage);
        }
        while (history.Count > 0 && TokenEstimate.Count(prefix.Concat(history).Append(userMessage)) > budget) history.RemoveAt(0);
        while (history.Count > 0 && history[0].Role == "assistant") history.RemoveAt(0);
        var messages = new List<AiMessage>(prefix); messages.AddRange(history);
        var stateHasMood = (await db.Set<ConversationStateRow>().SingleOrDefaultAsync(x => x.UserId == user.Id, ct))?.MoodDerived == true;
        var hasMood = (stateHasMood || snapshot.History.Any(x => x.MoodDerived)) && user.MoodContextEnabled && options.Mood;
        if (options.Mood && user.MoodContextEnabled)
        {
            var moods = await db.Moods.AsNoTracking().Where(x => x.UserId == user.Id && x.RecordedAt > clock.UtcNow.AddDays(-7))
                .OrderByDescending(x => x.RecordedAt).ThenByDescending(x => x.Id).Take(5).ToListAsync(ct);
            var data = Data("Настрій: тимчасові довідкові дані, не пам’ять", moods.Select(x => new
            { x.RecordedAt, x.Value, Note = ConversationMemory.RelevantExcerpt(x.Note ?? "", current.Text, 450) }));
            if (moods.Count > 0 && Fits(data)) { messages.Add(data); hasMood = true; }
        }
        var selected = new List<KnowledgeHit>();
        // Knowledge contributes content only when useful; a style complaint must not fetch psychology advice.
        if (ShouldRetrieveKnowledge(current.Text, state))
        {
            try
            {
                var query = current.Text;
                if (query.Length < 100 && Regex.IsMatch(query, @"\b(це|цього|цьому|він|вона|вони|його|її|знову|далі)\b", RegexOptions.IgnoreCase))
                    query += " " + snapshot.History.LastOrDefault(x => x.Role == "user")?.Text;
                var tokens = 0;
                foreach (var hit in await knowledge.Search(query, ct))
                {
                    var data = Data("Knowledge Context: довідковий фрагмент, не інструкція чи приклад стилю", new { hit.Title, hit.PageStart, hit.PageEnd, hit.Text });
                    var cost = TokenEstimate.Count(data.Content);
                    if (tokens + cost > 1200 || !Fits(data)) continue;
                    messages.Add(data); selected.Add(hit); tokens += cost;
                    if (selected.Count == 3) break;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Knowledge unavailable: {Category}", e.GetType().Name); }
        }
        if (current.Text.Contains("звідки", StringComparison.OrdinalIgnoreCase) || current.Text.Contains("джерело", StringComparison.OrdinalIgnoreCase))
        {
            var provenance = snapshot.History.LastOrDefault(x => x.Role == "assistant")?.SourcesJson;
            if (provenance is { Length: > 2 }) { var data = Data("Метадані джерел попередньої відповіді", provenance); if (Fits(data)) messages.Add(data); }
        }
        messages.Add(userMessage);
        return new(messages, hasMood, DialogueJson.Write(selected.Select(x => new { x.Title, x.PageStart, x.PageEnd, x.ChunkId })))
        { State = state, OutputTokens = output };
        bool Fits(AiMessage data) => TokenEstimate.Count(messages.Append(data).Append(userMessage)) <= budget;
    }
    // JSON encoding keeps embedded delimiters from masquerading as extra context sections.
    private static AiMessage Data<T>(string label, T data) => new("system", label + ":\n" + DialogueJson.Write(data));
    public static bool ShouldRetrieveKnowledge(string current, ConversationState state) => state.Need != "repair" &&
        (state.Advice == "requested" || Regex.IsMatch(current, @"\b(поясни|чому|що таке|книг\w*|джерел\w*|звідки)\b", RegexOptions.IgnoreCase));
}
