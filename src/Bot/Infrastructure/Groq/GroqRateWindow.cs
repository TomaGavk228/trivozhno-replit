using System.Globalization;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace Trivozhno.Infrastructure.Groq;

public sealed record GroqRateSnapshot(int Limit, int Remaining, TimeSpan? Reset)
{
    public static GroqRateSnapshot? Read(HttpResponseHeaders headers)
    {
        string? Header(string name) => headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
        if (!int.TryParse(Header("x-ratelimit-limit-tokens"), out var limit) || limit <= 0 ||
            !int.TryParse(Header("x-ratelimit-remaining-tokens"), out var remaining) || remaining < 0)
            return null;
        return new(limit, Math.Min(limit, remaining), ParseDuration(Header("x-ratelimit-reset-tokens")));
    }

    private static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 80) return null;
        var matches = Regex.Matches(text, @"(\d+(?:\.\d+)?)(ms|s|m|h|d)",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (matches.Count == 0 || string.Concat(matches.Select(m => m.Value)) != text) return null;
        double seconds = 0;
        foreach (Match match in matches)
        {
            if (!double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var value)) return null;
            seconds += value * (match.Groups[2].Value switch
            { "ms" => .001, "s" => 1, "m" => 60, "h" => 3600, _ => 86400 });
        }
        return double.IsFinite(seconds) && seconds is > 0 and <= 86400 ? TimeSpan.FromSeconds(seconds) : null;
    }
}

// All access is serialized by AiQuota's gate. This is an admission estimate, not
// a claim about Groq's internal algorithm. Headers cap it; 429 remains authoritative.
internal sealed class GroqRateWindow(int configuredLimit, double initialBalance, DateTimeOffset now)
{
    private double capacity = configuredLimit;
    private double balance = Math.Min(configuredLimit, initialBalance);
    private double refillPerSecond = configuredLimit / 60d;
    private DateTimeOffset updatedAt = now;
    private DateTimeOffset blockedUntil = DateTimeOffset.MinValue;
    private bool observed;
    private double highestInputRatio;
    private int inputSamples;

    public int EstimateInput(int rough)
    {
        if (inputSamples < 2) return rough;
        // Learn only counts, never message contents. Retain a margin and the
        // highest observed ratio, including after changes of language/content.
        var factor = Math.Clamp(highestInputRatio * 1.2, .6, 3);
        return (int)Math.Min(int.MaxValue, Math.Ceiling(rough * factor) + 64);
    }

    public void LearnInput(int rough, int actual)
    {
        if (rough < 100 || actual <= 0) return;
        highestInputRatio = Math.Max(highestInputRatio, (double)actual / rough);
        inputSamples++;
    }

    public TimeSpan WaitFor(int tokens, DateTimeOffset at)
    {
        Refill(at);
        if (tokens > capacity) throw new ContextTooLargeException();
        var seconds = Math.Max(0, (tokens - balance) / refillPerSecond);
        return TimeSpan.FromSeconds(Math.Max(seconds, Math.Max(0, (blockedUntil - at).TotalSeconds)));
    }

    public void Debit(int tokens, DateTimeOffset at) { Refill(at); balance -= tokens; }
    public void Refund(int tokens, DateTimeOffset at) { Refill(at); balance = Math.Min(capacity, balance + tokens); }

    public void Observe(GroqRateSnapshot snapshot, int otherReservations, DateTimeOffset at)
    {
        Refill(at);
        capacity = Math.Min(configuredLimit, snapshot.Limit);
        var rate = snapshot.Limit / 60d;
        if (snapshot.Reset is { } reset && snapshot.Remaining < snapshot.Limit)
            rate = Math.Min(rate, (snapshot.Limit - snapshot.Remaining) / reset.TotalSeconds);
        refillPerSecond = Math.Max(.01, Math.Min(configuredLimit / 60d, rate));
        var available = Math.Min(capacity, snapshot.Remaining) - otherReservations;
        // First observation reconciles restart history with the provider. Later
        // out-of-order responses cannot raise the balance; usage refunds can.
        balance = observed ? Math.Min(balance, available) : available;
        observed = true;
    }

    public void Block(TimeSpan retryAfter, DateTimeOffset at)
    {
        var until = at + (retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromSeconds(1));
        if (until > blockedUntil) blockedUntil = until;
    }

    private void Refill(DateTimeOffset at)
    {
        if (at <= updatedAt) return;
        balance = Math.Min(capacity, balance + (at - updatedAt).TotalSeconds * refillPerSecond);
        updatedAt = at;
    }
}
