using Microsoft.EntityFrameworkCore;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;

namespace Trivozhno.Infrastructure.Groq;

public sealed class AiQuota(IServiceScopeFactory scopes, BotOptions options, IClock clock, ILogger<AiQuota>? log = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim slots = new(options.AiConcurrency, options.AiConcurrency);
    private readonly Dictionary<string, GroqRateWindow> windows = new(StringComparer.Ordinal);
    private readonly Dictionary<long, Reservation> pending = [];
    private sealed record Reservation(string Model, int Tokens, int InputEstimate);

    public async Task<IDisposable> Enter(CancellationToken ct)
    {
        await slots.WaitAsync(ct);
        return new Slot(slots);
    }

    private sealed class Slot(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }

    public async Task<long> Reserve(string model, int tokens, bool summary, CancellationToken ct, int inputEstimate = 0)
    {
        var requested = tokens;
        while (true)
        {
            TimeSpan delay;
            await gate.WaitAsync(ct);
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BotDb>();
                var now = clock.UtcNow;
                if (summary && await db.Messages.AnyAsync(x => x.Status == "queued" || x.Status == "processing", ct))
                    throw new AiUnavailableException();

                var recent = await db.Set<ApiUsage>().Where(x => x.Model == model && x.At > now.AddDays(-1)).ToListAsync(ct);
                var minute = recent.Where(x => x.At > now.AddMinutes(-1)).ToArray();
                if (!windows.TryGetValue(model, out var window))
                {
                    window = new(options.TokensPerMinute,
                        options.TokensPerMinute - minute.Sum(RateLimitTokens), now);
                    windows.Add(model, window);
                }

                tokens = inputEstimate > 0
                    ? checked(requested - inputEstimate + window.EstimateInput(inputEstimate))
                    : requested;
                if (tokens > options.TokensPerMinute || tokens > options.TokensPerDay)
                    throw new ContextTooLargeException();
                if (recent.Count >= options.RequestsPerDay || recent.Sum(RateLimitTokens) + tokens > options.TokensPerDay)
                    throw new AiUnavailableException("daily_quota");

                delay = window.WaitFor(tokens, now);
                if (minute.Length >= options.RequestsPerMinute)
                {
                    var requestWait = minute.Min(x => x.At).AddSeconds(61) - now;
                    if (requestWait > delay) delay = requestWait;
                }
                if (delay <= TimeSpan.Zero)
                {
                    var row = new ApiUsage { At = now, Model = model, Tokens = tokens, Summary = summary };
                    db.Add(row);
                    await db.SaveChangesAsync(ct);
                    window.Debit(tokens, clock.UtcNow);
                    pending.Add(row.Id, new(model, tokens, inputEstimate));
                    log?.LogInformation("Groq admission; model {Model}; rough reservation {Rough}; reserved {Reserved}", model, requested, tokens);
                    return row.Id;
                }
            }
            finally { gate.Release(); }

            // Re-evaluate when another in-flight request may have reconciled usage.
            // Do not sleep until an old row falls out of a 60-second window.
            var wait = TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 100, 1000));
            log?.LogDebug("Local Groq quota wait; model {Model}; estimated delay {DelayMs} ms; reservation {Tokens}",
                model, delay.TotalMilliseconds, tokens);
            await Task.Delay(wait, ct);
        }
    }

    public async Task Reconcile(long id, AiResult usage, CancellationToken ct, GroqRateSnapshot? snapshot = null)
    {
        if (usage.Tokens < 0) return;
        await gate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDb>();
            await db.Set<ApiUsage>().Where(x => x.Id == id).ExecuteUpdateAsync(x => x
                .SetProperty(y => y.Tokens, usage.Tokens)
                .SetProperty(y => y.PromptTokens, usage.PromptTokens)
                .SetProperty(y => y.CompletionTokens, usage.CompletionTokens)
                .SetProperty(y => y.ReasoningTokens, usage.ReasoningTokens)
                .SetProperty(y => y.CachedTokens, usage.CachedTokens), ct);
            if (!pending.Remove(id, out var reservation)) return;
            var window = windows[reservation.Model];
            var cached = Math.Clamp(usage.CachedTokens, 0, Math.Max(0, Math.Min(usage.PromptTokens, usage.Tokens)));
            window.Refund(reservation.Tokens - Math.Max(0, usage.Tokens - cached), clock.UtcNow);
            window.LearnInput(reservation.InputEstimate, usage.PromptTokens);
            if (snapshot is not null)
                window.Observe(snapshot, pending.Values.Where(x => x.Model == reservation.Model).Sum(x => x.Tokens), clock.UtcNow);
        }
        finally { gate.Release(); }
    }

    public async Task BackOff(string model, TimeSpan retryAfter, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (windows.TryGetValue(model, out var window)) window.Block(retryAfter, clock.UtcNow);
        }
        finally { gate.Release(); }
    }

    // A timeout does not prove that the provider generated no tokens. Keep the
    // debit and persisted estimate, but stop treating this as an in-flight hold.
    public async Task Abandon(long id)
    {
        await gate.WaitAsync();
        try { pending.Remove(id); }
        finally { gate.Release(); }
    }

    private static int RateLimitTokens(ApiUsage usage) => Math.Max(0, usage.Tokens -
        Math.Clamp(usage.CachedTokens, 0, Math.Max(0, Math.Min(usage.PromptTokens, usage.Tokens))));

    // Release is only for requests known not to have reached the provider.
    public async Task Release(long id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDb>();
            await db.Set<ApiUsage>().Where(x => x.Id == id).ExecuteDeleteAsync(ct);
            if (pending.Remove(id, out var reservation))
                windows[reservation.Model].Refund(reservation.Tokens, clock.UtcNow);
        }
        finally { gate.Release(); }
    }
}
