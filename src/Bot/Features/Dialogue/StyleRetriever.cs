using System.Text.Json;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Knowledge;

namespace Trivozhno.Features.Dialogue;

public sealed record StyleExample(string Id, string Intent, string[] Tags, AiMessage[] Dialogue, bool GivesAdvice, string Origin);
public interface IStyleRetriever
{
    IReadOnlyList<StyleExample> Select(string current, ConversationState state, IReadOnlyList<AiMessage> history, int count = 3);
}

// Local BM25 + dialogue-act signals. No embedding API or extra LLM request.
public sealed class StyleRetriever : IStyleRetriever
{
    private readonly StyleExample[] examples;
    private readonly string[][] terms;
    public StyleRetriever() : this(JsonSerializer.Deserialize<StyleExample[]>(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Resources", "StyleBank", "uk-v1.json")), DialogueJson.Options)!) { }
    public StyleRetriever(StyleExample[] bank)
    {
        if (bank.Length == 0 || bank.Select(x => x.Id).Distinct().Count() != bank.Length || bank.Any(x => x.Origin != "original" || x.Dialogue.Length < 2))
            throw new InvalidOperationException("Invalid Style Bank metadata.");
        examples = bank;
        terms = bank.Select(x => Lexicon.Terms(string.Join(' ', x.Tags) + " " + string.Join(' ', x.Dialogue.Where(m => m.Role == "user").Select(m => m.Content)))).ToArray();
    }
    public IReadOnlyList<StyleExample> Select(string current, ConversationState state, IReadOnlyList<AiMessage> history, int count = 3)
    {
        var query = Lexicon.Terms(current).Distinct().ToArray();
        // Only inherit topic for an elliptical reply; a new explicit topic wins.
        if (query.Length < 2 && current.Length < 50)
            query = query.Concat(Lexicon.Terms(state.Topic + " " + history.LastOrDefault(m => m.Role == "user")?.Content)).Distinct().ToArray();
        var intent = InferIntent(current, state);
        var avg = Math.Max(1, terms.Average(x => x.Length));
        double Score(int i)
        {
            double score = examples[i].Intent == intent ? 4 : 0;
            foreach (var term in query)
            {
                var tf = terms[i].Count(x => x == term); if (tf == 0) continue;
                var df = terms.Count(x => x.Contains(term));
                var idf = Math.Log(1 + (terms.Length - df + 0.5) / (df + 0.5));
                score += idf * tf * 2.2 / (tf + 1.2 * (0.25 + 0.75 * terms[i].Length / avg));
            }
            return score;
        }
        var selected = Enumerable.Range(0, examples.Length)
            .Where(i => !(state.Advice != "requested" && examples[i].GivesAdvice))
            .Where(i => state.Questions != "avoid" || !examples[i].Dialogue.Last().Content.Contains('?'))
            .Select(i => (Example: examples[i], Score: Score(i)))
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Example.Id)
            .Take(Math.Clamp(count, 0, 3)).Select(x => x.Example).ToArray();
        return selected;
    }
    public static string InferIntent(string current, ConversationState state)
    {
        var text = current.ToLowerInvariant();
        if (state.Need == "repair" || text.Contains("забагато питань") || text.Contains("не хочу порад")) return "feedback";
        if (state.Advice == "requested") return "advice";
        if (text.Trim(' ', '.', '!') is "не знаю" or "хз" or "угу" or "ок") return "short";
        if (new[] { "вийшло", "вдалося", "нарешті", "хороша новина", "ура" }.Any(text.Contains)) return "celebrate";
        if (new[] { "жарт", "ахах", "сміш" }.Any(text.Contains)) return "humor";
        if (new[] { "пам'ятаєш", "пам’ятаєш", "повернімося", "минулого разу" }.Any(text.Contains)) return "return";
        if (new[] { "сумно", "погано", "страшно", "тривож", "самот", "втом", "важко" }.Any(text.Contains)) return "support";
        return "chat";
    }
}
