using Microsoft.EntityFrameworkCore;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;

namespace Trivozhno.Infrastructure.Groq;

public sealed class AiQuota(IServiceScopeFactory scopes, BotOptions options, IClock clock, ILogger<AiQuota>? log = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim slots = new(options.AiConcurrency, options.AiConcurrency);

    public async Task<IDisposable> Enter(CancellationToken ct)
    {
        await slots.WaitAsync(ct);
        return new Slot(slots);
    }

    private sealed class Slot(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }

    public async Task<long> Reserve(string model, int tokens, bool summary, CancellationToken ct)
    {
        if (tokens > options.TokensPerMinute || tokens > options.TokensPerDay)
            throw new ContextTooLargeException();

        while (true)
        {
            var delay = TimeSpan.FromSeconds(1);
            await gate.WaitAsync(ct);
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BotDb>();
                var now = clock.UtcNow;
                var minute = now.AddMinutes(-1);
                var day = now.AddDays(-1);

                // Background memory never competes with a live queued/processing reply.
                if (summary && await db.Messages.AnyAsync(
                        x => x.Status == "queued" || x.Status == "processing", ct))
                    throw new AiUnavailableException();

                // Groq limits are model-scoped. Do not make gpt-oss summary traffic consume
                // the local Qwen bucket.
                var recent = await db.Set<ApiUsage>()
                    .Where(x => x.Model == model && x.At > day)
                    .ToListAsync(ct);
                var shortWindow = recent.Where(x => x.At > minute).ToArray();

                if (recent.Count >= options.RequestsPerDay ||
                    recent.Sum(RateLimitTokens) + tokens > options.TokensPerDay)
                    throw new AiUnavailableException();

                if (shortWindow.Length < options.RequestsPerMinute &&
                    shortWindow.Sum(RateLimitTokens) + tokens <= options.TokensPerMinute)
                {
                    var row = new ApiUsage
                    {
                        At = now,
                        Model = model,
                        Tokens = tokens,
                        Summary = summary
                    };
                    db.Add(row);
                    await db.SaveChangesAsync(ct);
                    return row.Id;
                }

                if (shortWindow.Length > 0)
                    delay = shortWindow.Min(x => x.At).AddSeconds(61) - now;
            }
            finally
            {
                gate.Release();
            }

            log?.LogInformation("Local Groq quota wait; model {Model}; delay {DelayMs} ms; reservation {Tokens}", model, delay.TotalMilliseconds, tokens);
            await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1), ct);
        }
    }

    public async Task Reconcile(long id, AiResult usage, CancellationToken ct)
    {
        if (usage.Tokens < 0) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDb>();
        await db.Set<ApiUsage>().Where(x => x.Id == id).ExecuteUpdateAsync(x => x
            .SetProperty(y => y.Tokens, usage.Tokens)
            .SetProperty(y => y.PromptTokens, usage.PromptTokens)
            .SetProperty(y => y.CompletionTokens, usage.CompletionTokens)
            .SetProperty(y => y.ReasoningTokens, usage.ReasoningTokens)
            .SetProperty(y => y.CachedTokens, usage.CachedTokens), ct);
    }

    // Groq excludes cached prompt tokens from rate limits. Keep original usage in
    // the database for telemetry; unreconciled reservations still count in full.
    private static int RateLimitTokens(ApiUsage usage) => Math.Max(0, usage.Tokens -
        Math.Clamp(usage.CachedTokens, 0, Math.Max(0, Math.Min(usage.PromptTokens, usage.Tokens))));

    public async Task Release(long id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDb>();
        await db.Set<ApiUsage>().Where(x => x.Id == id).ExecuteDeleteAsync(ct);
    }
}
