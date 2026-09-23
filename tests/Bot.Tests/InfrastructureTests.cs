using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Knowledge;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Infrastructure.Telegram;
using Xunit.Abstractions;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;

namespace Trivozhno.Tests;

public sealed class InfrastructureTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task PdfImportExtractsTextReportsBlankPagesAndDeduplicatesRenamedFile()
    {
        await using var r = new TestRig(); await r.Init();
        var folder = Path.Combine(Path.GetTempPath(), "trivozhno-pdf-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "original.pdf"); var renamed = Path.Combine(folder, "renamed.pdf");
            using var builder = new PdfDocumentBuilder(); var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            builder.AddPage(595, 842).AddText("Original test text about a calm conversation.", 12, new PdfPoint(50, 750), font);
            builder.AddPage(595, 842); File.WriteAllBytes(file, builder.Build()); File.Copy(file, renamed);
            using var scope = r.Services.CreateScope(); var importer = scope.ServiceProvider.GetRequiredService<BookImporter>();
            var report = await importer.Import(file, default);
            Assert.Equal("partial-no-ocr", report.Status); Assert.Equal(2, report.Pages); Assert.Equal(new[] { 2 }, report.EmptyPages);
            Assert.Equal(1, report.Chunks); Assert.Equal("duplicate-skipped", (await importer.Import(renamed, default)).Status);
            Assert.Equal(1, await r.Read(db => db.Sources.CountAsync()));
            var chunk = await r.Read(db => db.Chunks.SingleAsync()); Assert.Contains("Original test text", chunk.Text); Assert.Equal(1, chunk.PageStart); Assert.Equal(1, chunk.PageEnd);
        }
        finally { Directory.Delete(folder, true); }
    }
    [PostgresFact]
    public async Task GroqRetriesDoNotLeavePhantomTokenReservations()
    {
        await using var r = new TestRig(); await r.Init(realClock: true);
        var handler = new StubHttp((index, _) =>
        {
            if (index == 1) { var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new(TimeSpan.FromMilliseconds(1)); return response; }
            return index == 2 ? new(HttpStatusCode.ServiceUnavailable) : Success();
        });
        var client = new GroqClient(new HttpClient(handler), r.Services.GetRequiredService<BotOptions>(), r.Services.GetRequiredService<AiQuota>(), NullLogger<GroqClient>.Instance);
        var result = await client.Complete([new("user", "Привіт")], false, default);
        Assert.Equal("Привіт!", result.Text); Assert.Equal(3, handler.Bodies.Count);
        var usage = await r.Read(db => db.Set<ApiUsage>().ToListAsync());
        Assert.Single(usage);
        Assert.Equal(10, usage[0].Tokens);
    }
    [PostgresFact]
    public async Task DuplicateInboxIsIdempotentAndPoisonDoesNotBlockAnotherPerson()
    {
        await using var r = new TestRig(); await r.Init();
        var batch = new PollBatch([new(1, 1, "/start", null)], 2);
        await r.Inbox.Store(batch, default); await r.Inbox.Store(batch, default); await r.DrainInbox();
        Assert.Equal(1, await r.Read(db => db.Users.CountAsync()));
        await r.WithDb(async db =>
        {
            db.Inbox.Add(new() { Id = 2, TelegramId = 1, Payload = "{broken", CreatedAt = r.Clock.UtcNow, AvailableAt = r.Clock.UtcNow });
            await db.SaveChangesAsync();
        });
        await r.Inbox.Store(new([new(3, 2, "/start", null)], 4), default);
        await r.DrainInbox(); Assert.Equal(2, await r.Read(db => db.Users.CountAsync()));
        for (var i = 0; i < 3; i++) { r.Clock.Advance(TimeSpan.FromSeconds(20)); await r.DrainInbox(); }
        Assert.Equal("failed", await r.Read(db => db.Inbox.Where(x => x.Id == 2).Select(x => x.Status).SingleAsync()));
        Assert.Equal(4, await r.Inbox.Offset(default));
    }
    [PostgresFact]
    public async Task RestoredPendingAiAndDraftAreUsable()
    {
        await using var r = new TestRig(); await r.Init(); await r.StartChat(); await r.Text("Не загубитися");
        await r.WithDb(async db => { await db.Messages.ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, "processing")); });
        await r.Services.GetRequiredService<Maintenance>().Recover(default); await r.Processor.Step(default);
        Assert.Equal(1, await r.Read(db => db.Messages.CountAsync(x => x.Role == "assistant")));
        await r.Text("/menu"); await r.Click("menu:confession"); await r.Text("Збережена чернетка");
        // A fresh scope has no in-memory user state; all navigation comes from PostgreSQL.
        await r.Services.GetRequiredService<Maintenance>().Recover(default); await r.Text("✅ Готово");
        Assert.Equal(UserState.ConfessionConfirm, (await r.User()).State);
    }
    [PostgresFact]
    public async Task HistoryHasStablePagesAndOlderPeriods()
    {
        await using var r = new TestRig(); await r.Init(); await r.Text("/start"); var u = await r.User();
        await r.WithDb(async db =>
        {
            for (var i = 0; i < 8; i++) db.Moods.Add(new() { UserId = u.Id, Value = 3, Note = "Запис " + i, RecordedAt = r.Clock.UtcNow.AddMinutes(-i) });
            db.Moods.Add(new() { UserId = u.Id, Value = 2, Note = "Попередній тиждень", RecordedAt = r.Clock.UtcNow.AddDays(-8) }); await db.SaveChangesAsync();
        });
        await r.Click("menu:mood"); await r.Click("history:latest"); var first = (await r.User()).HistoryCursorId;
        await r.Click("history:older"); Assert.NotEqual(first, (await r.User()).HistoryCursorId);
        await r.Click("history:newer"); Assert.Equal(first, (await r.User()).HistoryCursorId);
        await r.Click("history:previous"); await r.DrainOutbox();
        Assert.Contains(r.Telegram.Sent, x => x.Text.Contains("Попередній тиждень"));
    }
    [PostgresFact]
    public async Task GroqFallbackUsesOwnProfileAnd401DoesNotRetry()
    {
        await using var r = new TestRig(); await r.Init();
        var configured = new BotOptions
        {
            Model = "qwen/qwen3.8-27b",
            FallbackModel = "openai/gpt-oss-120b",
            GroqKey = "test-only"
        };
        var handler = new StubHttp((index, _) => index == 1
            ? new(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":{\"code\":\"model_not_found\"}}") }
            : Success());
        var client = new GroqClient(new HttpClient(handler), configured, r.Services.GetRequiredService<AiQuota>(), NullLogger<GroqClient>.Instance);
        var result = await client.Complete([new("user", "Привіт")], false, default);
        Assert.Equal(configured.FallbackModel, result.Model); Assert.Equal(2, handler.Bodies.Count);
        var fallback = JsonDocument.Parse(handler.Bodies[1]).RootElement;
        Assert.Equal("medium", fallback.GetProperty("reasoning_effort").GetString());
        Assert.False(fallback.GetProperty("include_reasoning").GetBoolean());
        Assert.False(fallback.TryGetProperty("reasoning_format", out _));
        var denied = new StubHttp((_, _) => new(HttpStatusCode.Unauthorized));
        var deniedClient = new GroqClient(new HttpClient(denied), configured, r.Services.GetRequiredService<AiQuota>(), NullLogger<GroqClient>.Instance);
        await Assert.ThrowsAsync<AiUnavailableException>(() => deniedClient.Complete([new("user", "Привіт")], false, default)); Assert.Single(denied.Bodies);
    }
    [PostgresFact]
    public async Task Telegram429IsDeferredAndBlockedUserDisablesReminder()
    {
        await using var r = new TestRig(); await r.Init(); await r.Text("/start");
        r.Telegram.Fail = _ => new TelegramFailure(429, 7); await r.Outbox.Step(default);
        Assert.True(await r.Read(db => db.Outbox.AnyAsync(x => x.Status == "queued" && x.AvailableAt == r.Clock.UtcNow.AddSeconds(7))));
        r.Clock.Advance(TimeSpan.FromSeconds(8)); r.Telegram.Fail = _ => new TelegramFailure(403);
        var u = await r.User(); await r.WithDb(async db => { db.Reminders.Add(new() { UserId = u.Id, Enabled = true, NextDueAt = r.Clock.UtcNow.AddDays(1) }); await db.SaveChangesAsync(); });
        await r.Outbox.Step(default); Assert.True((await r.User()).Blocked); Assert.False(await r.Read(db => db.Reminders.Select(x => x.Enabled).SingleAsync()));
    }
    [PostgresFact]
    public async Task RetrievalFindsFifteenParaphrasesAndRejectsFiveUnrelatedQueries()
    {
        await using var r = new TestRig(); await r.Init();
        // Original test sentences only: this does not claim validation against the two absent books.
        var cases = new (string source, string query)[]
        {
            ("Тривога й страх можуть заважати почати нову справу.", "Боюся і хвилююся"),
            ("Самотність і підтримка: можливість звернутися до інших.", "Самотньо, потрібна допомога"),
            ("Стосунки потребують довіри й взаємної поваги.", "Хочу довіряти партнеру, важливі стосунки"),
            ("Межі допомагають відмовляти без зайвої провини.", "Незручно відмовити, почуваюся винним, провина"),
            ("Самооцінка та помилки: невдача не визначає цінність людини.", "Я нікчемний через невдачі"),
            ("Злість і конфлікт: як помітити гнів під час сварки.", "Злюся, ми сваримося"),
            ("Втома потребує відпочинку, а не нових вимог.", "Виснажений, хочу відпочити"),
            ("Ревнощі у стосунках можуть викликати непорозуміння.", "Ревную, стосунки напружені"),
            ("Розставання і почуття: поступове переживання змін.", "Ми розійшлися, складні емоції"),
            ("Спілкування та підтримка допомагають висловити потреби.", "Хочу поговорити й отримати допомогу"),
            ("Контроль і межі у взаємодії з іншими.", "Мною керує, як відмовити"),
            ("Робота і втома можуть потребувати перегляду навантаження.", "Працюю і вже виснажена"),
            ("Рішення і страх помилки: дозволити собі вибір.", "Боюсь обрати"),
            ("Прокрастинація і перфекціонізм: відкладання справ через високі вимоги.", "Відкладаю, бо все має бути ідеально"),
            ("Провина і почуття: розрізнення відповідальності та емоцій.", "Соромно за емоції")
        };
        await r.WithDb(async db =>
        {
            var source = new KnowledgeSource { Title = "Авторські тестові приклади", Hash = new string('A', 64), ImportedAt = r.Clock.UtcNow }; db.Sources.Add(source); await db.SaveChangesAsync();
            for (var i = 0; i < cases.Length; i++) db.Chunks.Add(new() { SourceId = source.Id, Ordinal = i, PageStart = i + 1, PageEnd = i + 1, Text = cases[i].source, Terms = Lexicon.Terms(cases[i].source) });
            await db.SaveChangesAsync();
        });
        using var scope = r.Services.CreateScope(); var retriever = scope.ServiceProvider.GetRequiredService<IKnowledgeRetriever>();
        var found = 0;
        for (var i = 0; i < cases.Length; i++)
        {
            var hits = await retriever.Search(cases[i].query, default); if (hits.Any(x => x.PageStart == i + 1)) found++;
            output.WriteLine($"query {i + 1}: expected page {i + 1}; returned {string.Join(',', hits.Select(x => x.PageStart))}");
        }
        var unrelated = new[] { "Рецепт борщу з буряком", "Unity NavMesh bake помилка компіляції", "Скільки супутників у Юпітера", "Знайди футбольний рахунок Барселони", "Як розв'язати квадратне рівняння" };
        foreach (var query in unrelated) Assert.Empty(await retriever.Search(query, default));
        Assert.Equal(15, found);
    }
    [LoadFact]
    public async Task ThousandAcceptedUpdatesReachTerminalStatusWithIsolatedContexts()
    {
        await using var r = new TestRig(); await r.Init(realClock: true); const int total = 1000;
        var now = DateTimeOffset.UtcNow;
        await r.WithDb(async db =>
        {
            for (var i = 1; i <= total; i++)
            {
                var u = new BotUser { TelegramId = i, State = UserState.ChatActive, CreatedAt = now, AiNoticeShown = true }; var session = new ChatSession { UserId = u.Id, StartedAt = now };
                u.SessionId = session.Id; db.Users.Add(u); db.Sessions.Add(session);
            }
            await db.SaveChangesAsync();
        });
        var process = Process.GetCurrentProcess(); var cpuBefore = process.TotalProcessorTime; var watch = Stopwatch.StartNew();
        for (var b = 0; b < 10; b++)
        {
            var inputs = Enumerable.Range(b * 100 + 1, 100).Select(i => new BotInput(i, i, "UNIQUE_USER_" + i, null)).ToArray();
            Assert.True(await r.Inbox.Store(new(inputs, (b + 1) * 100 + 1), default));
        }
        var peakInbox = await r.Read(db => db.Inbox.CountAsync(x => x.Status == "queued"));
        var times = new System.Collections.Concurrent.ConcurrentBag<double>(); var stop = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var inboxDone = false;
        var producer = Task.Run(async () => { while (await r.Inbox.Step(stop.Token)) { } Volatile.Write(ref inboxDone, true); });
        async Task Consumer()
        {
            while (!stop.IsCancellationRequested)
            {
                if (await r.Processor.Step(stop.Token)) { times.Add(watch.Elapsed.TotalMilliseconds); continue; }
                if (Volatile.Read(ref inboxDone) && !await r.Read(db => db.Messages.AnyAsync(x => x.Status == "queued" || x.Status == "processing"))) break;
                await Task.Delay(5, stop.Token);
            }
        }
        await Task.WhenAll(producer, Consumer(), Consumer()); await r.DrainOutbox(); watch.Stop();
        Assert.Equal(total, await r.Read(db => db.Inbox.CountAsync(x => x.Status == "done")));
        Assert.Equal(total, await r.Read(db => db.Messages.CountAsync(x => x.Role == "assistant" && x.Status == "done")));
        Assert.Equal(0, await r.Read(db => db.Outbox.CountAsync(x => x.Status == "queued" || x.Status == "sending")));
        var answers = await r.Read(db => db.Messages.Where(x => x.Role == "assistant").Join(db.Users, x => x.UserId, u => u.Id, (x, u) => new { u.TelegramId, x.Text }).ToListAsync());
        Assert.All(answers, x => Assert.Equal("Відповідь: UNIQUE_USER_" + x.TelegramId, x.Text));
        var sorted = times.Order().ToArray();
        output.WriteLine(JsonSerializer.Serialize(new { users = total, aiWorkers = 2, peakInbox, elapsedSeconds = watch.Elapsed.TotalSeconds,
            p95AiCompletionMs = sorted[(int)(sorted.Length * 0.95) - 1], cpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds,
            peakWorkingSetMb = process.PeakWorkingSet64 / 1048576d, processors = Environment.ProcessorCount, note = "Fake AI and Telegram. DB server RAM is separate. Completion measured from start of burst." }));
    }
    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Привіт!\"},\"finish_reason\":\"stop\"}],\"usage\":{\"total_tokens\":10}}", Encoding.UTF8, "application/json") };
    private sealed class StubHttp(Func<int, HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken)); return respond(Bodies.Count, request); }
    }
}
