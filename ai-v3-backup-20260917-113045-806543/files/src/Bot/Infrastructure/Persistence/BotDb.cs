using Microsoft.EntityFrameworkCore;

namespace Trivozhno.Infrastructure.Persistence;

public sealed class BotDb(DbContextOptions<BotDb> options) : DbContext(options)
{
    public DbSet<BotUser> Users => Set<BotUser>();
    public DbSet<ChatSession> Sessions => Set<ChatSession>();
    public DbSet<ChatMessage> Messages => Set<ChatMessage>();
    public DbSet<ConversationSummary> Summaries => Set<ConversationSummary>();
    public DbSet<Draft> Drafts => Set<Draft>();
    public DbSet<DraftPart> DraftParts => Set<DraftPart>();
    public DbSet<ConfessionSubmission> Submissions => Set<ConfessionSubmission>();
    public DbSet<MoodEntry> Moods => Set<MoodEntry>();
    public DbSet<ReminderSetting> Reminders => Set<ReminderSetting>();
    public DbSet<ReminderOccurrence> Occurrences => Set<ReminderOccurrence>();
    public DbSet<InboxUpdate> Inbox => Set<InboxUpdate>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<PollCheckpoint> Checkpoints => Set<PollCheckpoint>();
    public DbSet<KnowledgeSource> Sources => Set<KnowledgeSource>();
    public DbSet<KnowledgeChunk> Chunks => Set<KnowledgeChunk>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<BotUser>().HasIndex(x => x.TelegramId).IsUnique();
        m.Entity<BotUser>().Property(x => x.State).HasConversion<string>();
        m.Entity<BotUser>().Property(x => x.ReturnState).HasConversion<string>();
        m.Entity<BotUser>().Property(x => x.HistoryEndLocal).HasColumnType("timestamp without time zone");
        m.Entity<ConversationSummary>().HasKey(x => x.UserId);
        m.Entity<ReminderSetting>().HasKey(x => x.UserId);
        Owned<ChatSession>(m); Owned<ChatMessage>(m); Owned<ConversationSummary>(m); Owned<Draft>(m);
        Owned<ConfessionSubmission>(m); Owned<MoodEntry>(m); Owned<ReminderSetting>(m); Owned<ReminderOccurrence>(m);
        m.Entity<Draft>().HasIndex(x => x.UserId).IsUnique();
        m.Entity<DraftPart>().HasOne<Draft>().WithMany().HasForeignKey(x => x.DraftId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<DraftPart>().HasIndex(x => new { x.DraftId, x.Id });
        m.Entity<ChatMessage>().HasIndex(x => x.UpdateId).IsUnique();
        m.Entity<ChatMessage>().HasIndex(x => new { x.UserId, x.Id });
        m.Entity<ChatMessage>().HasIndex(x => new { x.Status, x.Id });
        m.Entity<ChatMessage>().HasIndex(x => x.ReplyToId).IsUnique();
        m.Entity<ConfessionSubmission>().HasIndex(x => x.DraftId).IsUnique();
        m.Entity<ConfessionSubmission>().HasIndex(x => x.OperationId).IsUnique();
        m.Entity<MoodEntry>().HasIndex(x => new { x.UserId, x.RecordedAt, x.Id });
        m.Entity<MoodEntry>().ToTable(t => t.HasCheckConstraint("CK_Mood_Value", "\"Value\" BETWEEN 1 AND 5"));
        m.Entity<ReminderSetting>().HasIndex(x => new { x.Enabled, x.NextDueAt });
        m.Entity<ReminderSetting>().ToTable(t => t.HasCheckConstraint("CK_Reminder_Interval", "\"IntervalDays\" BETWEEN 1 AND 3"));
        m.Entity<ReminderOccurrence>().HasIndex(x => new { x.UserId, x.ScheduledAt }).IsUnique();
        m.Entity<InboxUpdate>().Property(x => x.Id).ValueGeneratedNever();
        m.Entity<InboxUpdate>().HasIndex(x => new { x.Status, x.AvailableAt, x.Id });
        m.Entity<OutboxMessage>().HasOne<BotUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<OutboxMessage>().HasIndex(x => new { x.OperationId, x.PartIndex }).IsUnique();
        m.Entity<OutboxMessage>().HasIndex(x => new { x.Status, x.AvailableAt, x.Id });
        m.Entity<OutboxMessage>().HasIndex(x => new { x.Destination, x.Id });
        m.Entity<KnowledgeSource>().HasIndex(x => x.Hash).IsUnique();
        m.Entity<KnowledgeChunk>().HasOne<KnowledgeSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<KnowledgeChunk>().HasIndex(x => new { x.SourceId, x.Ordinal }).IsUnique();
        m.Entity<KnowledgeChunk>().HasIndex(x => x.Terms).HasMethod("gin");
        m.Entity<PollCheckpoint>().Property(x => x.Id).ValueGeneratedNever();
        m.Entity<Trivozhno.Infrastructure.Groq.ApiUsage>().HasIndex(x => x.At);
    }
    private static void Owned<T>(ModelBuilder m) where T : OwnedEntity =>
        m.Entity<T>().HasOne<BotUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
}
