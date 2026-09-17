using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Trivozhno.Features.Dialogue;
using Trivozhno.Features.Memory;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Persistence;

namespace Trivozhno.Tests;

public sealed class DialogueTests
{
    [Fact]
    public void FeedbackPersistsUntilExplicitReleaseAndAdviceConsentIsPerTurn()
    {
        var a = FeedbackPolicy.Apply(new(), "Не хочу порад. Без питань. Коротше.");
        Assert.Equal("avoid", a.Advice); Assert.Equal("avoid", a.Questions); Assert.Equal("short", a.Length);
        var b = FeedbackPolicy.Merge(a, new());
        Assert.Equal("avoid", b.Advice); Assert.Equal("avoid", b.Questions);
        var c = FeedbackPolicy.Apply(b, "Добре, підкажи що робити. Можеш питати.");
        Assert.Equal("requested", c.Advice); Assert.Equal("normal", c.Questions);
        Assert.Equal("ask_first", FeedbackPolicy.Apply(c, "Дякую").Advice);
        Assert.Equal("normal", FeedbackPolicy.Apply(new(), "Вона сказала: «не питай».").Questions);
        Assert.Equal("normal", FeedbackPolicy.Apply(new(), "> Не питай\nЦе цитата з фільму.").Questions);
    }
    [Fact]
    public void RetrievalUsesCurrentIntentAndExcludesUnwantedAdviceAndQuestions()
    {
        var retriever = new StyleRetriever();
        var celebration = retriever.Select("Нарешті вдалося!", new() { Topic = "самотність" }, []);
        Assert.Equal("celebrate", celebration[0].Intent); Assert.InRange(celebration.Count, 1, 3);
        var feedback = retriever.Select("Не хочу порад, без питань", FeedbackPolicy.Apply(new(), "Не хочу порад, без питань"), []);
        Assert.All(feedback, x => { Assert.False(x.GivesAdvice); Assert.DoesNotContain("?", x.Dialogue.Last().Content); });
        Assert.Equal("feedback", feedback[0].Intent);
        Assert.False(ConversationContextBuilder.ShouldRetrieveKnowledge("Ти мене дратуєш", new() { Need = "repair" }));
        Assert.True(ConversationContextBuilder.ShouldRetrieveKnowledge("Що таке прокрастинація?", new()));
    }
    [Theory]
    [InlineData("{}")] [InlineData("{\"reply\":\"Привіт\",\"state\":{}}")] [InlineData("звичайний текст")]
    [InlineData("{\"reply\":\"Привіт\",\"state\":null}")]
    public void InvalidEnvelopeIsNeverDisplayed(string text) => Assert.Throws<AiUnavailableException>(() => TurnContract.Parse(text));
    [Fact]
    public void SchemaAndBudgetAreExplicitAndReplyIsSeparatedFromState()
    {
        var payload = GroqClient.Payload("openai/gpt-oss-120b", [new("user", "Привіт")], false, 900);
        Assert.Equal(900, payload["max_completion_tokens"]); Assert.Equal("low", payload["reasoning_effort"]);
        using var json = JsonDocument.Parse(DialogueJson.Write(payload));
        Assert.True(json.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
        var turn = TurnContract.Parse(DialogueJson.Write(new ConversationTurn(new() { Topic = "SECRET_STATE" }, "Привіт!")));
        Assert.Equal("Привіт!", turn.Reply); Assert.DoesNotContain("SECRET_STATE", turn.Reply);
    }
    [PostgresFact]
    public async Task StateSurvivesScopesAndFailedCallResetsForNewSessionAndClearDeletesIt()
    {
        await using var r = new TestRig(); await r.Init(); await r.StartChat();
        r.Ai.Fail = true; await r.Text("Не хочу порад. Без питань."); await r.Processor.Step(default);
        var row = await r.Read(db => db.Set<ConversationStateRow>().AsNoTracking().SingleAsync());
        Assert.Contains("avoid", row.Json); r.Ai.Fail = false;
        await r.Text("Мене це досі злить"); await r.Processor.Step(default);
        Assert.Contains(r.Ai.Requests.Last(), m => m.Content.StartsWith("ConversationState") && m.Content.Contains("avoid"));
        await r.Text("/menu"); await r.Click("menu:talk"); await r.Text("Нова розмова"); await r.Processor.Step(default);
        Assert.DoesNotContain("avoid", (await r.Read(db => db.Set<ConversationStateRow>().SingleAsync())).Json);
        Assert.Contains(r.Ai.Requests.Last(), m => m.Role == "user" && m.Content.Contains("Не хочу порад"));
        await r.Text("/menu"); await r.Click("menu:settings"); await r.Click("settings:clear"); await r.Click("settings:clear:yes");
        Assert.Equal(0, await r.Read(db => db.Set<ConversationStateRow>().CountAsync()));
    }
    [PostgresFact]
    public async Task IdleStateExpiresButLongTermMemoryIsPreserved()
    {
        await using var r = new TestRig(); await r.Init(); await r.StartChat(); var u = await r.User();
        await r.WithDb(async db => { db.Summaries.Add(new() { UserId = u.Id, Text = "Користувач працює над проєктом Орбіта." }); await db.SaveChangesAsync(); });
        await r.Text("Без питань"); await r.Processor.Step(default); r.Clock.Advance(TimeSpan.FromHours(7));
        await r.Text("Повернімося до Орбіти"); await r.Processor.Step(default);
        Assert.Contains(r.Ai.Requests.Last(), m => m.Content.Contains("проєктом Орбіта"));
        Assert.DoesNotContain(r.Ai.Requests.Last(), m => m.Content.StartsWith("ConversationState") && m.Content.Contains("avoid"));
    }
    [PostgresFact]
    public async Task ContextKeepsUserMessageLastAndDoesNotPretendStyleExamplesAreHistory()
    {
        await using var r = new TestRig(); await r.Init(); await r.StartChat();
        r.Ai.ReplyState = new() { Topic = "меню", LastAction = "celebrate" };
        await r.Text("Нарешті вдалося полагодити меню!"); await r.Processor.Step(default);
        Assert.Contains("celebrate", await r.Read(db => db.Set<ConversationStateRow>().Select(x => x.Json).SingleAsync()));
        var messages = r.Ai.Requests.Single();
        Assert.Equal("Нарешті вдалося полагодити меню!", messages.Last().Content);
        Assert.Equal("user", messages.Last().Role);
        Assert.Contains(messages, x => x.Content.StartsWith("STYLE EXAMPLE"));
        Assert.DoesNotContain(messages, x => x.Role == "assistant");
        Assert.True(TokenEstimate.Count(messages) + GroqClient.StructuredSchemaTokens + 900 <= 8000);
    }
    [PostgresFact]
    public async Task LateResponseCannotRestoreStateAfterDeletion()
    {
        await using var r = new TestRig(); await r.Init(); await r.StartChat(); await r.Text("Не хочу порад");
        r.Ai.Pause = new(TaskCreationOptions.RunContinuationsAsynchronously); var pending = r.Processor.Step(default);
        await r.Ai.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await r.Text("/menu"); await r.Click("menu:settings"); await r.Click("settings:delete"); await r.Click("settings:delete:yes");
        r.Ai.Pause.SetResult(new("Пізно", "fake", 1)); await pending;
        Assert.Equal(0, await r.Read(db => db.Set<ConversationStateRow>().CountAsync()));
    }
    [PostgresFact]
    public async Task MoodDerivedStateIsTaggedAndRemovedFromContextAfterConsentWithdrawal()
    {
        await using var r = new TestRig(); await r.Init(); await r.StartChat(); var user = await r.User();
        await r.WithDb(async db =>
        {
            var u = await db.Users.SingleAsync(); u.MoodContextEnabled = true;
            db.Add(new ConversationStateRow { UserId = u.Id, SessionId = u.SessionId!.Value, MemoryVersion = u.MemoryVersion,
                Json = DialogueJson.Write(new ConversationState { Topic = "MOOD_STATE_SECRET" }), UpdatedAt = r.Clock.UtcNow, MoodDerived = true });
            await db.SaveChangesAsync();
        });
        await r.Text("Привіт"); await r.Processor.Step(default);
        Assert.Contains(r.Ai.Requests.Last(), m => m.Content.Contains("MOOD_STATE_SECRET"));
        Assert.True(await r.Read(db => db.Messages.Where(x => x.Role == "assistant").Select(x => x.MoodDerived).SingleAsync()));
        await r.WithDb(async db =>
        {
            (await db.Users.SingleAsync()).MoodContextEnabled = false;
            (await db.Set<ConversationStateRow>().SingleAsync()).Json = DialogueJson.Write(new ConversationState { Topic = "MOOD_STATE_SECRET" });
            await db.SaveChangesAsync();
        });
        await r.Text("Далі"); await r.Processor.Step(default);
        Assert.DoesNotContain(r.Ai.Requests.Last(), m => m.Content.Contains("MOOD_STATE_SECRET"));
    }
    [PostgresFact]
    public async Task OldDatabaseUpgradePreservesUserHistoryAndSummary()
    {
        await using var r = new TestRig(); await r.Init();
        using var scope = r.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<BotDb>();
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20260912171513_Initial");
        var user = new BotUser { TelegramId = 456, CreatedAt = r.Clock.UtcNow }; db.Users.Add(user);
        db.Messages.Add(new() { UserId = user.Id, Text = "Збережена історія", CreatedAt = r.Clock.UtcNow });
        db.Summaries.Add(new() { UserId = user.Id, Text = "Збережена пам’ять" }); await db.SaveChangesAsync();
        await migrator.MigrateAsync();
        Assert.Equal("Збережена історія", await db.Messages.Select(x => x.Text).SingleAsync());
        Assert.Equal("Збережена пам’ять", await db.Summaries.Select(x => x.Text).SingleAsync());
        Assert.Equal(0, await db.Set<ConversationStateRow>().CountAsync());
    }
    [PostgresFact]
    public async Task GroqStructuredCallUsesOneRequestAndRejectsTruncationWithoutRetry()
    {
        await using var r = new TestRig(); await r.Init();
        foreach (var finish in new[] { "stop", "length" })
        {
            var handler = new EnvelopeHttp(finish);
            var client = new GroqClient(new HttpClient(handler), r.Services.GetRequiredService<BotOptions>(), r.Services.GetRequiredService<AiQuota>(), NullLogger<GroqClient>.Instance);
            if (finish == "stop") Assert.Equal("Привіт", TurnContract.Parse((await client.CompleteStructured([new("user", "Привіт")], 900, default)).Text).Reply);
            else await Assert.ThrowsAsync<AiUnavailableException>(() => client.CompleteStructured([new("user", "Привіт")], 900, default));
            Assert.Equal(1, handler.Calls);
        }
    }
    private sealed class EnvelopeHttp(string finish) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(DialogueJson.Write(new
            {
                choices = new[] { new { finish_reason = finish, message = new { content = DialogueJson.Write(new ConversationTurn(new(), "Привіт")) } } },
                usage = new { total_tokens = 12 }
            }), Encoding.UTF8, "application/json") });
        }
    }
}
