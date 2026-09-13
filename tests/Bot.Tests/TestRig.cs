using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Infrastructure.Telegram;

namespace Trivozhno.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEST_DATABASE_URL"))) Skip = "Set TEST_DATABASE_URL to a disposable PostgreSQL database."; }
}
public sealed class LoadFactAttribute : FactAttribute
{
    public LoadFactAttribute() { if (Environment.GetEnvironmentVariable("RUN_LOAD_TEST") != "1" || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEST_DATABASE_URL"))) Skip = "Set RUN_LOAD_TEST=1 and TEST_DATABASE_URL."; }
}
public sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.Parse("2026-09-12T12:00:00Z");
    public void Advance(TimeSpan by) => UtcNow += by;
}
public sealed class FakeAi : IAiClient
{
    public ConcurrentQueue<IReadOnlyList<AiMessage>> Requests { get; } = new();
    public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<AiResult>? Pause { get; set; }
    public bool Fail { get; set; }
    public async Task<AiResult> Complete(IReadOnlyList<AiMessage> messages, bool summary, CancellationToken ct)
    {
        Requests.Enqueue(messages.ToArray()); Entered.TrySetResult(true);
        if (Fail) throw new AiUnavailableException();
        if (Pause is not null) return await Pause.Task.WaitAsync(ct);
        return new(summary ? "Користувач хоче уважного спілкування." : "Відповідь: " + messages.Last().Content, "fake-ai", 30);
    }
}
public sealed record SentMessage(long Destination, string Text, string? Markup, long Id);
public sealed class FakeTelegram : ITelegramClient
{
    private long id;
    public ConcurrentQueue<SentMessage> Sent { get; } = new();
    public Func<long, Exception?>? Fail { get; set; }
    public Task<PollBatch> Poll(long offset, CancellationToken ct) => Task.FromResult(new PollBatch([], offset));
    public Task<long> Send(long destination, string text, string? markup, CancellationToken ct)
    {
        var error = Fail?.Invoke(destination); if (error is not null) throw error;
        var messageId = Interlocked.Increment(ref id); Sent.Enqueue(new(destination, text, markup, messageId)); return Task.FromResult(messageId);
    }
    public Task Typing(long destination, CancellationToken ct) => Task.CompletedTask;
    public Task Validate(CancellationToken ct) => Task.CompletedTask;
}

public sealed class TestRig : IAsyncDisposable
{
    public ServiceProvider Services { get; private set; } = null!;
    public TestClock Clock { get; } = new();
    public FakeAi Ai { get; } = new();
    public FakeTelegram Telegram { get; } = new();
    public string Schema { get; } = "test_" + Guid.NewGuid().ToString("N");
    private string connection = "";
    private long update;
    public InboxProcessor Inbox => Services.GetRequiredService<InboxProcessor>();
    public AiProcessor Processor => Services.GetRequiredService<AiProcessor>();
    public OutboxProcessor Outbox => Services.GetRequiredService<OutboxProcessor>();
    public async Task Init(bool mood = true, bool realClock = false)
    {
        connection = ConnectionStrings.Parse(Environment.GetEnvironmentVariable("TEST_DATABASE_URL")!);
        await using (var conn = new NpgsqlConnection(connection))
        {
            await conn.OpenAsync(); await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{Schema}\"", conn); await command.ExecuteNonQueryAsync();
        }
        var b = new NpgsqlConnectionStringBuilder(connection) { SearchPath = Schema, MaxPoolSize = 20 };
        var config = new BotOptions { Database = b.ConnectionString, TelegramToken = "test-only", GroqKey = "test-only", ChannelId = -100123,
            TelegramPerSecond = int.MaxValue, TelegramChatMilliseconds = 0, TelegramChannelMilliseconds = 0, Mood = mood, QueueWait = 3600 };
        var services = new ServiceCollection(); services.AddLogging(); services.AddBot(config);
        // Explicit SET also supports test wire-protocol servers that ignore startup SearchPath.
        services.AddDbContext<BotDb>(o => o.AddInterceptors(new TestSchemaInterceptor(Schema)));
        services.AddSingleton<IClock>(realClock ? new SystemClock() : Clock); services.AddSingleton<IAiClient>(Ai); services.AddSingleton<ITelegramClient>(Telegram);
        Services = services.BuildServiceProvider();
        await WithDb(async db => await db.Database.MigrateAsync());
    }
    public async Task WithDb(Func<BotDb, Task> action)
    { using var s = Services.CreateScope(); await action(s.ServiceProvider.GetRequiredService<BotDb>()); }
    public async Task<T> Read<T>(Func<BotDb, Task<T>> action)
    { using var s = Services.CreateScope(); return await action(s.ServiceProvider.GetRequiredService<BotDb>()); }
    public Task<BotUser> User(long id = 1) => Read(db => db.Users.AsNoTracking().SingleAsync(x => x.TelegramId == id));
    public async Task Text(string text, long id = 1)
    { var key = Interlocked.Increment(ref update); await Inbox.Store(new([new(key, id, text, null)], key + 1), default); await DrainInbox(); }
    public async Task Click(string action, long id = 1, string? token = null)
    {
        token ??= (await User(id)).UiToken; var key = Interlocked.Increment(ref update);
        await Inbox.Store(new([new(key, id, null, action + "|" + token)], key + 1), default); await DrainInbox();
    }
    public async Task DrainInbox() { for (var i = 0; i < 20000 && await Inbox.Step(default); i++) { } }
    public async Task DrainOutbox() { for (var i = 0; i < 20000 && await Outbox.Step(default); i++) { } }
    public async Task StartChat(long id = 1) { await Text("/start", id); await Click("menu:talk", id); }
    public async Task Tick() => await Services.GetRequiredService<Maintenance>().Tick(default);
    public async ValueTask DisposeAsync()
    {
        if (Services is not null) await Services.DisposeAsync();
        await using var conn = new NpgsqlConnection(connection); await conn.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE", conn); await command.ExecuteNonQueryAsync();
    }
}

public sealed class TestSchemaInterceptor(string schema) : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SET search_path TO \"{schema}\"";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
