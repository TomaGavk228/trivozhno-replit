using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trivozhno.Features.Dialogue;
using Trivozhno.Features.Memory;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Persistence;

namespace Trivozhno.Tests;

public sealed class LiveQualityFactAttribute : FactAttribute
{
    public LiveQualityFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_LIVE_QUALITY") != "1" ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GROQ_API_KEY")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEST_DATABASE_URL")))
            Skip = "Opt in with RUN_LIVE_QUALITY=1, GROQ_API_KEY and a disposable TEST_DATABASE_URL.";
    }
}
public sealed record QualityScenario(string Id, string SourceDesign, string Expectation, string[] Turns);
public sealed record CriterionRating(int? Score, string Evidence);
public sealed record ObservedTurn(int Turn, string User, string Reply, ConversationState State, double Milliseconds);

public static class ConversationQuality
{
    public static readonly string[] Criteria = ["naturalness", "nonRepetition", "questionFit", "adviceFit", "contextUse", "memory", "feedbackResponse", "progress", "telegramStyle"];
    public static QualityScenario[] Scenarios() => JsonSerializer.Deserialize<QualityScenario[]>(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Quality", "scenarios.json")), DialogueJson.Options)!;
    // Hard checks only. They do not purport to measure naturalness or semantic advice quality.
    public static string[] Check(ObservedTurn turn, IReadOnlyList<ObservedTurn> previous)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(turn.Reply)) issues.Add("empty_reply");
        if (turn.State.Questions == "avoid" && turn.Reply.Contains('?')) issues.Add("question_after_opt_out");
        if (turn.Reply.Contains("\"lastAction\"", StringComparison.Ordinal) || turn.Reply.Contains("<think>", StringComparison.OrdinalIgnoreCase)) issues.Add("metadata_leak");
        if (previous.Any(x => x.Reply.Trim() == turn.Reply.Trim())) issues.Add("exact_repetition");
        if (turn.Reply.Length > (FeedbackPolicy.WantsDetail(turn.User) ? 4000 : turn.State.Length == "short" ? 350 : 1000)) issues.Add("too_long");
        return issues.ToArray();
    }
}

public sealed class ConversationQualityTests
{
    [Fact]
    public void ScenariosAreMultiTurnAndSeparateFromProductionStyleBank()
    {
        var scenarios = ConversationQuality.Scenarios(); Assert.Equal(13, scenarios.Length);
        Assert.All(scenarios, x => Assert.True(x.Turns.Count(t => !t.StartsWith('/')) >= 3));
        var bank = JsonSerializer.Deserialize<StyleExample[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Resources", "StyleBank", "uk-v1.json")), DialogueJson.Options)!;
        var training = bank.SelectMany(x => x.Dialogue).Where(x => x.Role == "user").Select(x => x.Content).ToHashSet();
        Assert.All(scenarios.SelectMany(x => x.Turns).Where(x => x.Length > 20), x => Assert.DoesNotContain(x, training));
        Assert.Equal(9, ConversationQuality.Criteria.Length);
    }
    [Fact]
    public void HardChecksDetectRegressionsWithoutClaimingSubjectiveScores()
    {
        var turn = new ObservedTurn(2, "Не питай", "А чому?", new() { Questions = "avoid" }, 1);
        Assert.Contains("question_after_opt_out", ConversationQuality.Check(turn, []));
        Assert.Contains("exact_repetition", ConversationQuality.Check(turn, [turn]));
    }
    [LiveQualityFact]
    public async Task LiveScenariosProduceTranscriptsChecksAndOptionalNineCriterionRatings()
    {
        var filter = Environment.GetEnvironmentVariable("QUALITY_SCENARIO");
        var scenarios = ConversationQuality.Scenarios().Where(s => string.IsNullOrEmpty(filter) || s.Id == filter).ToArray();
        Assert.NotEmpty(scenarios);
        var directory = Path.GetFullPath(Environment.GetEnvironmentVariable("QUALITY_REPORT_DIR") ?? "TestResults/conversation-quality");
        Directory.CreateDirectory(directory);
        var failures = new List<string>();
        foreach (var scenario in scenarios)
        {
            await using var r = new TestRig(); await r.Init(liveAi: true, realClock: true); await r.StartChat();
            var observed = new List<ObservedTurn>(); var issues = new List<string>(); var boundaries = new List<int>();
            var ratings = ConversationQuality.Criteria.ToDictionary(x => x, _ => new CriterionRating(null, "not_evaluated"));
            string? evaluationError = null;
            try
            {
                foreach (var input in scenario.Turns)
                {
                    if (input == "/new_session") { await r.Text("/menu"); await r.Click("menu:talk"); boundaries.Add(observed.Count); continue; }
                    if (input == "/summarize")
                    {
                        var user = await r.User(); using var scope = r.Services.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<IConversationMemory>().Summarize(user.Id, user.MemoryVersion, default);
                        Assert.True(await r.Read(db => db.Summaries.AnyAsync())); continue;
                    }
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    await r.Text(input); await r.Processor.Step(default); watch.Stop();
                    var lastUser = await r.Read(db => db.Messages.Where(x => x.Role == "user").OrderByDescending(x => x.Id).FirstAsync());
                    var answer = await r.Read(db => db.Messages.SingleOrDefaultAsync(x => x.ReplyToId == lastUser.Id));
                    var row = await r.Read(db => db.Set<ConversationStateRow>().SingleOrDefaultAsync());
                    var state = row is null ? new() : JsonSerializer.Deserialize<ConversationState>(row.Json, DialogueJson.Options)!;
                    var turn = new ObservedTurn(observed.Count + 1, input, answer?.Text ?? "", state, watch.Elapsed.TotalMilliseconds);
                    issues.AddRange(ConversationQuality.Check(turn, observed).Select(x => $"turn {turn.Turn}: {x}"));
                    observed.Add(turn);
                    if (answer is null) break;
                }
                if (Environment.GetEnvironmentVariable("RUN_QUALITY_JUDGE") == "1" && issues.Count == 0)
                {
                    using var scope = r.Services.CreateScope();
                    var ai = scope.ServiceProvider.GetRequiredService<IAiClient>();
                    var prompt = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Quality", "judge.txt"));
                    var result = await ai.Complete([new("system", prompt), new("user", DialogueJson.Write(new { scenario.Expectation, boundaries, transcript = observed.Select(x => new { x.Turn, x.User, x.Reply }) }))], false, default);
                    var text = result.Text.Trim();
                    if (text.StartsWith("```")) { var newline = text.IndexOf('\n'); text = text[(newline + 1)..].TrimEnd().TrimEnd('`').Trim(); }
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, CriterionRating>>(text, DialogueJson.Options) ?? throw new JsonException();
                    foreach (var key in ConversationQuality.Criteria)
                    {
                        if (!parsed.TryGetValue(key, out var value) || value.Score is < 1 or > 5 || string.IsNullOrWhiteSpace(value.Evidence)) throw new JsonException();
                        ratings[key] = value;
                        if (value.Score is < 3) issues.Add("judge: " + key + "=" + value.Score);
                    }
                }
            }
            catch (Exception e) { evaluationError = e.GetType().Name; issues.Add("incomplete_run:" + evaluationError); }
            var report = new { scenario, observed, boundaries, issues, ratings, evaluationError,
                judge = Environment.GetEnvironmentVariable("RUN_QUALITY_JUDGE") == "1" ? "same-model exploratory; human review still required" : "not_run",
                productionStyleVersion = "uk-v1", model = "openai/gpt-oss-120b", createdAt = DateTimeOffset.UtcNow };
            await File.WriteAllTextAsync(Path.Combine(directory, scenario.Id + ".json"), JsonSerializer.Serialize(report, new JsonSerializerOptions(DialogueJson.Options) { WriteIndented = true }));
            failures.AddRange(issues.Select(x => scenario.Id + ": " + x));
        }
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }
}
