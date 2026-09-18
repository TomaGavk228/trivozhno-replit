using Npgsql;

namespace Trivozhno.Host;

public sealed class BotOptions
{
    public string Database { get; init; } = "";
    public string TelegramToken { get; init; } = "";
    public string GroqKey { get; init; } = "";
    public long ChannelId { get; init; }
    public string Model { get; init; } = "qwen/qwen3.8-27b";
    public string FallbackModel { get; init; } = "openai/gpt-oss-120b";
    public string SummaryModel { get; init; } = "openai/gpt-oss-20b";
    public int AiConcurrency { get; init; } = 2;
    public int AiTimeout { get; init; } = 45;
    public int JobBudget { get; init; } = 100;
    public int QueueWait { get; init; } = 300;
    public int RequestsPerMinute { get; init; } = 25;
    public int TokensPerMinute { get; init; } = 8000;
    public int RequestsPerDay { get; init; } = 900;
    public int TokensPerDay { get; init; } = 190000;
    public int InputBudget { get; init; } = 6000;
    public int MaxQueue { get; init; } = 10000;
    public int MaxUserAiQueue { get; init; } = 20;
    public int TelegramPerSecond { get; init; } = 20;
    public int TelegramChatMilliseconds { get; init; } = 1100;
    public int TelegramChannelMilliseconds { get; init; } = 3100;
    public int ChunkSize { get; init; } = 1800;
    public int ChunkOverlap { get; init; } = 180;
    public string Timezone { get; init; } = "Europe/Kyiv";
    public bool Conversation { get; init; } = true;
    public bool Confessions { get; init; } = true;
    public bool Mood { get; init; } = true;
    public bool Reminders { get; init; } = true;

    public static BotOptions Load(IConfiguration c) => new()
    {
        Database = c["DATABASE_URL"] ?? "", TelegramToken = c["TELEGRAM_BOT_TOKEN"] ?? "",
        GroqKey = c["GROQ_API_KEY"] ?? "", ChannelId = long.TryParse(c["CONFESSIONS_CHANNEL_ID"], out var id) ? id : 0,
        Model = c["GROQ_MODEL"] ?? "qwen/qwen3.8-27b", FallbackModel = c["GROQ_FALLBACK_MODEL"] ?? "openai/gpt-oss-120b",
        SummaryModel = c["GROQ_SUMMARY_MODEL"] ?? "openai/gpt-oss-20b",
        AiConcurrency = Int(c, "AI_MAX_CONCURRENCY", 2, 1, 16), AiTimeout = Int(c, "AI_TIMEOUT_SECONDS", 45, 1, 120),
        JobBudget = Int(c, "AI_JOB_BUDGET_SECONDS", 100, 10, 300), QueueWait = Int(c, "AI_MAX_QUEUE_WAIT_SECONDS", 300, 10, 3600),
        RequestsPerMinute = Int(c, "GROQ_RPM", 25, 1, 100000), TokensPerMinute = Int(c, "GROQ_TPM", 8000, 2000, 10000000),
        RequestsPerDay = Int(c, "GROQ_RPD", 900, 1, 10000000), TokensPerDay = Int(c, "GROQ_TPD", 190000, 2000, 100000000),
        InputBudget = Int(c, "AI_INPUT_TOKEN_BUDGET", 6000, 2000, 32000), MaxQueue = Int(c, "MAX_INBOX_QUEUE", 10000, 100, 100000),
        MaxUserAiQueue = Int(c, "MAX_USER_AI_QUEUE", 20, 1, 100),
        ChunkSize = Int(c, "BOOK_CHUNK_CHARS", 1800, 1200, 2200), ChunkOverlap = Int(c, "BOOK_CHUNK_OVERLAP", 180, 0, 300),
        Timezone = c["BOT_TIMEZONE"] ?? "Europe/Kyiv", Conversation = Flag(c, "FEATURE_CONVERSATION"),
        Confessions = Flag(c, "FEATURE_CONFESSIONS"), Mood = Flag(c, "FEATURE_MOOD"), Reminders = Flag(c, "FEATURE_REMINDERS")
    };
    private static int Int(IConfiguration c, string key, int fallback, int min, int max) =>
        c[key] is null ? fallback : int.TryParse(c[key], out var x) && x >= min && x <= max ? x : throw new InvalidOperationException($"Invalid {key} ({min}..{max}).");
    private static bool Flag(IConfiguration c, string key) => c[key] is null || c[key] is "1" or "true";

    public void Validate(bool live)
    {
        if (string.IsNullOrWhiteSpace(Database)) throw new InvalidOperationException("Set DATABASE_URL in Secrets.");
        _ = ConnectionStrings.Parse(Database);
        _ = TimeZoneInfo.FindSystemTimeZoneById(Timezone);
        if (!live) return;
        if (string.IsNullOrWhiteSpace(TelegramToken)) throw new InvalidOperationException("Set TELEGRAM_BOT_TOKEN in Secrets.");
        if (Conversation && string.IsNullOrWhiteSpace(GroqKey)) throw new InvalidOperationException("Set GROQ_API_KEY in Secrets.");
        if (Confessions && ChannelId >= 0) throw new InvalidOperationException("Set negative CONFESSIONS_CHANNEL_ID in Secrets.");
    }
}

public static class ConnectionStrings
{
    public static string Parse(string value)
    {
        try
        {
            if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
                return new NpgsqlConnectionStringBuilder(value) { IncludeErrorDetail = false }.ConnectionString;
            var uri = new Uri(value);
            var user = uri.UserInfo.Split(':', 2);
            var b = new NpgsqlConnectionStringBuilder
            {
                Host = uri.Host, Port = uri.Port > 0 ? uri.Port : 5432,
                Username = Uri.UnescapeDataString(user[0]), Password = user.Length > 1 ? Uri.UnescapeDataString(user[1]) : "",
                Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')), IncludeErrorDetail = false
            };
            foreach (var item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = item.Split('=', 2); var key = Uri.UnescapeDataString(pair[0]).ToLowerInvariant();
                var val = pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "";
                if (key == "sslmode") b.SslMode = val.ToLowerInvariant() switch
                { "disable" => SslMode.Disable, "allow" => SslMode.Allow, "prefer" => SslMode.Prefer, "require" => SslMode.Require, "verify-ca" => SslMode.VerifyCA, "verify-full" => SslMode.VerifyFull, _ => throw new FormatException() };
                else if (key == "sslrootcert") b.RootCertificate = val;
                else if (key == "connect_timeout") b.Timeout = int.Parse(val);
                else if (key == "application_name") b.ApplicationName = val;
                else if (key == "channel_binding") b.ChannelBinding = val.ToLowerInvariant() switch
                { "disable" => ChannelBinding.Disable, "prefer" => ChannelBinding.Prefer, "require" => ChannelBinding.Require, _ => throw new FormatException() };
                else throw new FormatException();
            }
            return b.ConnectionString;
        }
        catch { throw new InvalidOperationException("DATABASE_URL has an invalid format or unsupported parameter. Credentials are hidden."); }
    }
}

public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

// Fixed-size locks keep RAM bounded even when many different Telegram users arrive.
public sealed class UserLocks
{
    private readonly SemaphoreSlim[] locks = Enumerable.Range(0, 1024).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    public async Task<IDisposable> Lock(long id, CancellationToken ct)
    {
        var gate = locks[(int)((ulong)id % (ulong)locks.Length)]; await gate.WaitAsync(ct); return new Releaser(gate);
    }
    private sealed class Releaser(SemaphoreSlim gate) : IDisposable { public void Dispose() => gate.Release(); }
}
