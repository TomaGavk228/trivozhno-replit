using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Trivozhno.Features.Memory;
using Trivozhno.Features.Conversation;
using Trivozhno.Features.Navigation;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Infrastructure.Telegram;

namespace Trivozhno.Host;

public sealed class AiProcessor(IServiceScopeFactory scopes, UserLocks locks, IClock clock, BotOptions options, ILogger<AiProcessor> log)
{
    private readonly SemaphoreSlim claimGate = new(1, 1);

    public async Task<bool> Step(CancellationToken ct)
    {
        ChatMessage? job = null;
        BotUser? user = null;
        ConversationContext? context = null;
        var leaseAttempt = 0;

        await claimGate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDb>();
            var readyBefore = clock.UtcNow.AddMilliseconds(-300);
            job = await db.Messages.AsNoTracking().Where(x => x.Status == "queued" &&
                    x.CreatedAt <= readyBefore &&
                    !db.Messages.Any(y => y.UserId == x.UserId && (y.Status == "processing" || y.Status == "queued" && y.Id < x.Id)))
                .OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
            if (job is null) return false;

            user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == job.UserId, ct);
            if (user is null) return true;

            using var guard = await locks.Lock(user.TelegramId, ct);
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var currentUser = await db.Users.SingleOrDefaultAsync(x => x.Id == user.Id, ct);
            var current = await db.Messages.SingleOrDefaultAsync(x => x.Id == job.Id, ct);
            if (current is null || current.Status != "queued") return true;

            if (currentUser is null || currentUser.SessionId != job.SessionId || currentUser.MemoryVersion != job.MemoryVersion || !options.Conversation)
                current.Status = "cancelled";
            else if (clock.UtcNow - current.CreatedAt > TimeSpan.FromSeconds(options.QueueWait) || current.Attempts >= 3)
            {
                current.Status = "unanswered";
                scope.ServiceProvider.GetRequiredService<Ui>().Say(currentUser, "chat.expired");
            }
            else
            {
                current.Status = "processing";
                current.Attempts++;
                leaseAttempt = current.Attempts;
                current.LeaseUntil = clock.UtcNow.AddSeconds(options.JobBudget + 30);
                try
                {
                    context = await scope.ServiceProvider.GetRequiredService<IConversationMemory>().Build(currentUser, current, ct);
                }
                catch (ContextTooLargeException)
                {
                    current.Status = "unanswered";
                    scope.ServiceProvider.GetRequiredService<Ui>().Say(currentUser, "chat.large");
                }
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        finally
        {
            claimGate.Release();
        }

        if (context is null || user is null || job is null) return true;

        log.LogInformation("AI job {Operation}; queue wait {QueueMs} ms; input estimate {InputTokens}",
            job.Id, (clock.UtcNow - job.CreatedAt).TotalMilliseconds, TokenEstimate.Count(context.Messages));
        var watch = Stopwatch.StartNew();
        AiResult? result = null;
        var metadata = ConversationMemory.BuildMetadata("", []);
        var error = "chat.error";

        using (var scope = scopes.CreateScope())
        {
            try
            {
                // A typing indicator is cosmetic; its failure must not lose the reply.
                try
                {
                    using var typing = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    typing.CancelAfter(TimeSpan.FromSeconds(2));
                    await scope.ServiceProvider.GetRequiredService<ITelegramClient>().Typing(user.TelegramId, typing.Token);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    log.LogDebug("Typing indicator unavailable: {Category}", e.GetType().Name);
                }
                var reply = await scope.ServiceProvider.GetRequiredService<ChatResponder>().Reply(context, ct);
                result = reply.Result;
                metadata = ConversationMemory.BuildMetadata("", reply.Sources);
            }
            catch (ContextTooLargeException)
            {
                error = "chat.large";
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("AI job {Operation}: {Category}; reason {Reason}", job.Id, e.GetType().Name,
                    e is AiUnavailableException failure ? failure.Reason :
                    e is OperationCanceledException ? "job_budget_exhausted" : "unexpected");
            }
        }

        ct.ThrowIfCancellationRequested();

        using (var guard = await locks.Lock(user.TelegramId, ct))
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDb>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var u = await db.Users.SingleOrDefaultAsync(x => x.Id == user.Id, ct);
            var current = await db.Messages.SingleOrDefaultAsync(x => x.Id == job.Id, ct);
            if (u is null || current is null || current.Status != "processing" || current.Attempts != leaseAttempt ||
                u.SessionId != job.SessionId || u.MemoryVersion != job.MemoryVersion) return true;

            var ui = scope.ServiceProvider.GetRequiredService<Ui>();
            if (result is null || context.HasMood && !u.MoodContextEnabled)
            {
                current.Status = "unanswered";
                ui.Say(u, error);
            }
            else
            {
                current.Status = "done";
                db.Messages.Add(new()
                {
                    UserId = u.Id,
                    SessionId = job.SessionId,
                    Role = "assistant",
                    Text = result.Text,
                    Status = "done",
                    ReplyToId = job.Id,
                    MemoryVersion = job.MemoryVersion,
                    CreatedAt = clock.UtcNow,
                    MoodDerived = context.HasMood,
                    SourcesJson = metadata
                });
                ui.Text(u, result.Text, ui.Reply("chat.end"), "ai", job.SessionId, job.MemoryVersion);
            }

            current.LeaseUntil = null;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        log.LogInformation("AI job {Operation} finished in {ElapsedMs} ms", job.Id, watch.ElapsedMilliseconds);
        return true;
    }
}
