using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Trivozhno.Features.Conversation;
using Trivozhno.Features.Memory;
using Trivozhno.Features.Navigation;
using Trivozhno.Infrastructure.Ai;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Infrastructure.Telegram;

namespace Trivozhno.Host;

public sealed class AiProcessor(IServiceScopeFactory scopes, UserLocks locks, IClock clock, BotOptions options, ActiveGenerations active, ILogger<AiProcessor> log)
{
    private readonly SemaphoreSlim claimGate = new(1, 1);

    public async Task<bool> Step(CancellationToken ct)
    {
        ChatMessage? job = null;
        BotUser? user = null;
        var leaseAttempt = 0;
        using var generationHolder = new GenerationHolder();

        await claimGate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDb>();
            var legacyReadyBefore = clock.UtcNow.AddMilliseconds(-options.ChatQuietMilliseconds);
            job = await db.Messages.AsNoTracking().Where(x => x.Status == "queued" &&
                    (x.ReadyAt <= clock.UtcNow || x.ReadyAt == null && x.CreatedAt <= legacyReadyBefore) &&
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

            var session = await db.Sessions.SingleOrDefaultAsync(x => x.Id == job.SessionId, ct);
            if (currentUser is null || session is null || session.Revision != job.TurnRevision ||
                currentUser.SessionId != job.SessionId || currentUser.MemoryVersion != job.MemoryVersion || !options.Conversation)
                current.Status = "cancelled";
            else if (clock.UtcNow - (current.TurnStartedAt ?? current.CreatedAt) > TimeSpan.FromSeconds(options.QueueWait) || current.Attempts >= 3)
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
                // The detached snapshots are read outside locks/transactions.
                user = currentUser;
                job = current;
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            if (leaseAttempt > 0) generationHolder.Value = active.Begin(user.Id, ct);
        }
        finally
        {
            claimGate.Release();
        }

        if (generationHolder.Value is null || user is null || job is null) return true;

        var watch = Stopwatch.StartNew();
        AiResult? result = null;
        var error = "chat.error";

        using (var scope = scopes.CreateScope())
        {
            try
            {
                var context = await scope.ServiceProvider.GetRequiredService<ChatHistory>()
                    .Build(user, job, generationHolder.Value.Token);
                log.LogInformation("AI job {Operation}; queue wait {QueueMs} ms; input estimate {InputTokens}",
                    job.Id, (clock.UtcNow - job.CreatedAt).TotalMilliseconds, TokenEstimate.Count(context.Messages));
                // A typing indicator is cosmetic; its failure must not lose the reply.
                try
                {
                    using var typing = CancellationTokenSource.CreateLinkedTokenSource(generationHolder.Value.Token);
                    typing.CancelAfter(TimeSpan.FromSeconds(2));
                    await scope.ServiceProvider.GetRequiredService<ITelegramClient>().Typing(user.TelegramId, typing.Token);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    log.LogDebug("Typing indicator unavailable: {Category}", e.GetType().Name);
                }
                result = await scope.ServiceProvider.GetRequiredService<ChatResponder>().Reply(context, generationHolder.Value!.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && generationHolder.Value!.Token.IsCancellationRequested)
            {
                log.LogInformation("AI job {Operation} cancelled; superseded {Superseded}", job.Id, generationHolder.Value!.Superseded);
            }
            catch (ContextTooLargeException)
            {
                error = "chat.large";
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                if (e is AiUnavailableException quota)
                    error = quota.Reason switch { "daily_quota" => "chat.quota", "rate_limit" => "chat.rate", _ => "chat.error" };
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
            var session = await db.Sessions.SingleOrDefaultAsync(x => x.Id == job.SessionId, ct);
            if (u is null || current is null || current.Status != "processing" || current.Attempts != leaseAttempt ||
                session?.Revision != job.TurnRevision || u.SessionId != job.SessionId || u.MemoryVersion != job.MemoryVersion) return true;

            var ui = scope.ServiceProvider.GetRequiredService<Ui>();
            if (result is null)
            {
                current.Status = "unanswered";
                var crisis = CrisisSupport.IfGenerationFailed(current.TurnText ?? current.Text);
                if (crisis is not null) ui.Text(u, crisis, ui.Reply("chat.end"));
                else ui.Say(u, error);
            }
            else
            {
                current.Status = "done";
                db.Messages.Add(new()
                {
                    UserId = u.Id,
                    SessionId = job.SessionId,
                    Role = "assistant",
                    Text = "",
                    Status = "pending_delivery",
                    ReplyToId = job.Id,
                    MemoryVersion = job.MemoryVersion,
                    CreatedAt = clock.UtcNow,
                    MoodDerived = false,
                    SourcesJson = JsonSerializer.Serialize(new { conversation_state = result.ConversationState, sources = result.Sources }, ChatReplyFormat.Json)
                });
                await scope.ServiceProvider.GetRequiredService<ChatMemory>().Save(u, current, result.MemoryUpdates, ct);
                ui.Text(u, result.Text, ui.Reply("chat.end"), "ai", job.SessionId, job.MemoryVersion,
                    turnRevision: job.TurnRevision, replyToId: job.Id);
            }

            current.LeaseUntil = null;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        log.LogInformation("AI job {Operation} finished in {ElapsedMs} ms", job.Id, watch.ElapsedMilliseconds);
        return true;
    }
    private sealed class GenerationHolder : IDisposable
    {
        public ActiveGenerations.Registration? Value { get; set; }
        public void Dispose() => Value?.Dispose();
    }

}
