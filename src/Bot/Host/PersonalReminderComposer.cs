using Microsoft.EntityFrameworkCore;
using Trivozhno.Features.Conversation;
using Trivozhno.Features.Friend;
using Trivozhno.Features.Memory;
using Trivozhno.Features.Reminders;
using Trivozhno.Infrastructure.Ai;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Resources;

namespace Trivozhno.Host;

// «Друг пише першим» (Друг+). Maintenance marks a due personal reminder as
// "compose"; this worker asks the AI for one short opener based on memory and
// the last conversation, then enqueues it under the user lock. Any failure
// falls back to the ordinary reminder text. The chat prompt is not modified.
public sealed class PersonalReminderComposer(IServiceScopeFactory scopes, UserLocks locks, IClock clock, Uk uk, ILogger<PersonalReminderComposer> log)
{
    private const string Assignment = """
        Окреме завдання, не відповідь у чаті. Людина ввімкнула нагадування «Друг пише першим». Напиши в поле reply одне коротке повідомлення (1–2 речення), яким ти першим пишеш людині, щоб спитати, як вона.
        Можеш м'яко згадати одну доречну деталь із пам'яті чи минулої розмови, наприклад подію, про яку вона розповідала. Не вигадуй фактів, не став діагнозів, не давай порад і не тисни. Якщо деталей немає або вони болючі, просто тепло спитай, як справи.
        Без привітання з ім'ям, якщо ім'я невідоме. conversation_state залиш порожнім, memory_updates і used_sources — порожні масиви.
        """;

    public async Task<bool> Step(CancellationToken ct)
    {
        ReminderOccurrence? job;
        BotUser? user;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDb>();
            job = await db.Occurrences.AsNoTracking().Where(x => x.Status == "compose").OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
            if (job is null) return false;
            user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == job.UserId, ct);
        }
        string? text = null;
        if (user is not null && clock.UtcNow - job.ScheduledAt <= TimeSpan.FromHours(1))
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(60));
            try { text = await Compose(user, budget.Token); }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Personal reminder {Operation}: fallback text; {Category}", job.Id, e is AiUnavailableException a ? a.Reason : e.GetType().Name);
            }
        }
        if (user is null) return true;
        using var guard = await locks.Lock(user.TelegramId, ct);
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDb>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var current = await db.Occurrences.SingleOrDefaultAsync(x => x.Id == job.Id && x.Status == "compose", ct);
            if (current is null) return true;
            var u = await db.Users.SingleOrDefaultAsync(x => x.Id == job.UserId, ct);
            var r = await db.Reminders.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == job.UserId, ct);
            var valid = u is not null && !u.Blocked && u.State == UserState.MainMenu && r is { Enabled: true } && r.Version == current.Version &&
                clock.UtcNow - current.ScheduledAt <= TimeSpan.FromHours(1);
            current.Status = valid ? "queued" : "skipped";
            if (valid) await scope.ServiceProvider.GetRequiredService<ReminderHandler>().Send(u!, text ?? uk["reminder.message"], current.Version, ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        return true;
    }

    private async Task<string?> Compose(BotUser user, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDb>();
        var snapshot = scope.ServiceProvider.GetRequiredService<ChatConfiguration>().Read();
        // A synthetic "latest" message makes the memory reader consider all earlier history.
        var probe = new ChatMessage { Id = long.MaxValue, UserId = user.Id, MemoryVersion = user.MemoryVersion, Text = "", CreatedAt = clock.UtcNow };
        var memory = await scope.ServiceProvider.GetRequiredService<ChatMemory>().Build(user, probe, [], 1500, ct);
        var recent = await db.Messages.AsNoTracking().Where(x => x.UserId == user.Id && x.MemoryVersion == user.MemoryVersion && !x.MoodDerived &&
                (x.Role == "user" && (x.Status == "done" || x.Status == "unanswered") || x.Role == "assistant" && x.Status == "done" && x.Text != ""))
            .OrderByDescending(x => x.Id).Take(6).ToListAsync(ct);
        recent.Reverse();
        var dialogue = recent.Count == 0 ? "Минулої розмови немає." : "Кінець минулої розмови:\n" + string.Join('\n', recent.Select(x =>
            (x.Role == "user" ? "Людина: " : "Ти: ") + ChatReplyFormat.Clip(x.TurnText ?? x.Text, 300)));
        var messages = new List<AiMessage> { new("system", snapshot.Prompt), new("system", Assignment) };
        if (FriendHandler.Note(user, clock.UtcNow) is { Length: > 0 } note) messages.Add(new("system", note));
        if (memory.Text.Length > 0) messages.Add(new("system", memory.Text));
        messages.Add(new("user", dialogue + "\n\nНапиши своє повідомлення першим."));
        var settings = snapshot.Generation with { ReasoningEffort = "low", MaxCompletionTokens = Math.Min(snapshot.Generation.MaxCompletionTokens, 1200) };
        var result = await scope.ServiceProvider.GetRequiredService<IAiClient>().Complete(messages, settings, ct);
        var reply = result.Text.Trim();
        return reply.Length is > 0 and <= 700 ? reply : null;
    }
}
