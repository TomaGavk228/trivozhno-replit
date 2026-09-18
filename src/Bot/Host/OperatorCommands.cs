using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Knowledge;
using Trivozhno.Infrastructure.Persistence;

namespace Trivozhno.Host;

public static class OperatorCommands
{
    public static async Task<int> Run(IServiceProvider services, string[] args)
    {
        using var scope = services.CreateScope(); var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<BotDb>(); var options = provider.GetRequiredService<BotOptions>(); var ct = CancellationToken.None;
        switch (args[0])
        {
            case "channel-id":
            case "delete-webhook":
                if (options.TelegramToken.Length == 0) throw new InvalidOperationException("Set TELEGRAM_BOT_TOKEN in Secrets.");
                await using (var lease = await BotLease.Acquire(options, ct))
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(35) })
                {
                    var method = args[0] == "channel-id" ? "getUpdates" : "deleteWebhook";
                    object body = args[0] == "channel-id" ? new { timeout = 0, allowed_updates = new[] { "channel_post" } } : new { drop_pending_updates = false };
                    using var response = await http.PostAsJsonAsync($"https://api.telegram.org/bot{options.TelegramToken}/{method}", body, ct);
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    if (!json.RootElement.GetProperty("ok").GetBoolean()) { Console.Error.WriteLine("Telegram відхилив запит. Перевір токен і зупини інші poller."); return 1; }
                    if (args[0] == "delete-webhook") Console.WriteLine("Webhook вимкнено, pending updates збережено.");
                    else
                    {
                        var ids = json.RootElement.GetProperty("result").EnumerateArray().Where(x => x.TryGetProperty("channel_post", out _))
                            .Select(x => x.GetProperty("channel_post").GetProperty("chat").GetProperty("id").GetInt64()).Distinct().ToArray();
                        Console.WriteLine(JsonSerializer.Serialize(new { ChannelIds = ids }));
                        if (ids.Length == 0) Console.WriteLine("Напиши нове повідомлення у своєму службовому каналі й повтори команду.");
                    }
                    return 0;
                }
            case "migrate":
                await using (var lease = await BotLease.Acquire(options, ct, migration: true)) await db.Database.MigrateAsync(ct);
                Console.WriteLine("Міграції застосовано."); return 0;
            case "import-books":
                var path = Option(args, "--path") ?? Environment.GetEnvironmentVariable("BOOKS_PATH") ?? "./data/books";
                var files = File.Exists(path) ? new[] { path } : Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly).Where(x => x.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (files.Length == 0) { Console.Error.WriteLine("PDF-файлів не знайдено. Додай їх до data/books."); return 2; }
                var partial = false;
                foreach (var file in files)
                {
                    var report = await provider.GetRequiredService<BookImporter>().Import(file, ct);
                    Console.WriteLine(JsonSerializer.Serialize(report)); partial |= report.Status.StartsWith("partial"); db.ChangeTracker.Clear();
                }
                return partial ? 2 : 0;
            case "list-books":
                Console.WriteLine(JsonSerializer.Serialize(await db.Sources.Select(x => new { x.Id, x.Title, x.Hash, x.Active, Chunks = db.Chunks.Count(c => c.SourceId == x.Id) }).ToListAsync(ct))); return 0;
            case "deactivate-book":
                var source = long.Parse(args[1]);
                Console.WriteLine(await db.Sources.Where(x => x.Id == source).ExecuteUpdateAsync(x => x.SetProperty(y => y.Active, false), ct)); return 0;
            case "search-books":
                var query = Option(args, "--query") ?? throw new InvalidOperationException("Supply --query.");
                var hits = await provider.GetRequiredService<IKnowledgeRetriever>().Search(query, ct);
                Console.WriteLine(JsonSerializer.Serialize(hits.Select(x => new { x.ChunkId, x.Title, x.PageStart, x.PageEnd, x.Score }))); return 0;
            case "inspect-outbox":
                Console.WriteLine(JsonSerializer.Serialize(await db.Outbox.Where(x => x.Status == "delivery_unknown" || x.Status == "failed")
                    .OrderBy(x => x.Id).Take(100).Select(x => new { x.Id, x.OperationId, x.PartIndex, x.Kind, x.Status, x.Attempts, x.ErrorCode, x.TelegramMessageId }).ToListAsync(ct))); return 0;
            case "mark-delivered":
            case "retry-delivery":
                if (options.TelegramToken.Length == 0) throw new InvalidOperationException("Set TELEGRAM_BOT_TOKEN to verify exclusive access.");
                await using (var lease = await BotLease.Acquire(options, ct))
                {
                    await using var tx = await db.Database.BeginTransactionAsync(ct);
                    var id = long.Parse(args[1]); var row = await db.Outbox.SingleAsync(x => x.Id == id, ct);
                    if (row.Kind != "confession" || row.Status is not ("delivery_unknown" or "failed")) throw new InvalidOperationException("Only failed/unknown confession parts can be resolved.");
                    if (args[0] == "mark-delivered")
                    {
                        row.TelegramMessageId = long.Parse(Option(args, "--message-id") ?? throw new InvalidOperationException("Supply --message-id."));
                        row.Status = "sent"; OutboxProcessor.Scrub(row);
                    }
                    else
                    {
                        if (row.Status == "delivery_unknown" && !args.Contains("--accept-duplicate-risk"))
                            throw new InvalidOperationException("Check the channel first; --accept-duplicate-risk is required for an uncertain send.");
                        row.Status = "queued"; row.Attempts = 0; row.AvailableAt = DateTimeOffset.UtcNow;
                    }
                    var submission = await db.Submissions.SingleOrDefaultAsync(x => x.OperationId == row.OperationId, ct);
                    if (submission is not null) submission.Status = "sending";
                    await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); Console.WriteLine("Статус доставки оновлено."); return 0;
                }
            case "metrics":
            {
                var since = DateTimeOffset.UtcNow.AddDays(-1);
                var usage = await db.Set<ApiUsage>().Where(x => x.At > since).AsNoTracking().ToListAsync(ct);
                var byModel = usage
                    .GroupBy(x => x.Model)
                    .Select(g => new
                    {
                        model = string.IsNullOrWhiteSpace(g.Key) ? "legacy" : g.Key,
                        requests = g.Count(),
                        total = g.Sum(x => x.Tokens),
                        prompt = g.Sum(x => x.PromptTokens),
                        completion = g.Sum(x => x.CompletionTokens),
                        reasoning = g.Sum(x => x.ReasoningTokens),
                        cached = g.Sum(x => x.CachedTokens),
                        summaries = g.Count(x => x.Summary)
                    })
                    .OrderByDescending(x => x.total)
                    .ToArray();

                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    inbox = await db.Inbox.CountAsync(x => x.Status == "queued", ct),
                    ai = await db.Messages.CountAsync(x => x.Status == "queued" || x.Status == "processing", ct),
                    outbox = await db.Outbox.CountAsync(x => x.Status == "queued", ct),
                    unknown = await db.Outbox.CountAsync(x => x.Status == "delivery_unknown", ct),
                    errors = await db.Inbox.CountAsync(x => x.Status == "failed", ct) +
                             await db.Outbox.CountAsync(x => x.Status == "failed", ct),
                    groq24h = new
                    {
                        total = usage.Sum(x => x.Tokens),
                        prompt = usage.Sum(x => x.PromptTokens),
                        completion = usage.Sum(x => x.CompletionTokens),
                        reasoning = usage.Sum(x => x.ReasoningTokens),
                        cached = usage.Sum(x => x.CachedTokens),
                        byModel
                    }
                }));
                return 0;
            }
            case "groq-smoke":
                if (!args.Contains("--live")) throw new InvalidOperationException("Supply --live to consume a small amount of Groq quota.");
                var result = await provider.GetRequiredService<IAiClient>().Complete([new("system", "Відповідай українською одним коротким реченням."), new("user", "Привіт!")], false, ct);
                Console.WriteLine(JsonSerializer.Serialize(new { result.Model, result.Tokens, Answer = result.Text })); return 0;
            default:
                Console.Error.WriteLine("Commands: serve, migrate, channel-id, delete-webhook, import-books --path DIR, list-books, deactivate-book ID, search-books --query TEXT, inspect-outbox, mark-delivered ID --message-id ID, retry-delivery ID [--accept-duplicate-risk], metrics, groq-smoke --live"); return 2;
        }
    }
    private static string? Option(string[] args, string option)
    { var i = Array.IndexOf(args, option); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
}
