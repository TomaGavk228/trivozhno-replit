namespace Trivozhno.Infrastructure.Persistence;

public enum UserState { MainMenu, ChatActive, ConfessionDraft, ConfessionConfirm, ConfessionSending, MoodSelect, MoodNote, MoodHistory, Settings, ReminderFrequency, ReminderTime, ConfirmClearMemory, ConfirmDeleteData, ConfirmDiscard, Habits, HabitAdd, FriendSetup, FriendName }

public sealed class BotUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long TelegramId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public UserState State { get; set; }
    public UserState ReturnState { get; set; }
    public string PendingAction { get; set; } = "";
    public string UiToken { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public long MemoryVersion { get; set; }
    public Guid? SessionId { get; set; }
    public bool AiNoticeShown { get; set; }
    public bool ReminderPromptShown { get; set; }
    public string ChatStyleProfile { get; set; } = "";
    public bool Blocked { get; set; }
    public int PendingFrequency { get; set; } = 1;
    public bool CustomTime { get; set; }
    public DateTime HistoryEndLocal { get; set; }
    public DateTimeOffset? HistoryCursorTime { get; set; }
    public long? HistoryCursorId { get; set; }
    public DateTimeOffset? HistoryLastTime { get; set; }
    public long? HistoryLastId { get; set; }
    // Daily AI-chat counter; the day is a local date in BOT_TIMEZONE.
    public DateOnly? ChatCountDay { get; set; }
    public int ChatCount { get; set; }
    // «Друг+»: paid access until this moment. Notices remember which end date they were sent for.
    public DateTimeOffset? PlusUntil { get; set; }
    public DateTimeOffset? PlusWarnedFor { get; set; }
    public DateTimeOffset? PlusEndNoticeFor { get; set; }
    public string FriendName { get; set; } = "";
    public string FriendGender { get; set; } = "";
    public string FriendTone { get; set; } = "";
    public bool IsPlus(DateTimeOffset now) => PlusUntil > now;
    public void Go(UserState state) { State = state; UiToken = Guid.NewGuid().ToString("N")[..12]; }
}
public abstract class OwnedEntity { public Guid UserId { get; set; } }
public sealed class ChatSession : OwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public bool HasAnswer { get; set; }
    public long Revision { get; set; }
}
public sealed class ChatMessage : OwnedEntity
{
    public long Id { get; set; }
    public Guid SessionId { get; set; }
    public long MemoryVersion { get; set; }
    public string Role { get; set; } = "user";
    public string Text { get; set; } = "";
    public string Status { get; set; } = "queued";
    public long? ReplyToId { get; set; }
    public long? UpdateId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int Attempts { get; set; }
    public bool MoodDerived { get; set; }
    public string SourcesJson { get; set; } = "[]";
    // A Telegram message stays intact in Text; TurnText is the complete user turn.
    public string? TurnText { get; set; }
    public DateTimeOffset? TurnStartedAt { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }
    public long TurnRevision { get; set; }
}
public sealed class ConversationSummary : OwnedEntity
{
    public string Text { get; set; } = "";
    public long CoveredThroughId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
public sealed class Draft : OwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "";
    public int? MoodValue { get; set; }
    public bool ReplaceOnNext { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class DraftPart
{
    public long Id { get; set; }
    public Guid DraftId { get; set; }
    public string Text { get; set; } = "";
}
public sealed class ConfessionSubmission : OwnedEntity
{
    public long Id { get; set; }
    public Guid DraftId { get; set; }
    public Guid OperationId { get; set; } = Guid.NewGuid();
    public string Status { get; set; } = "sending";
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class MoodEntry : OwnedEntity
{
    public long Id { get; set; }
    public int Value { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}
public sealed class ReminderSetting : OwnedEntity
{
    public bool Enabled { get; set; }
    public int IntervalDays { get; set; } = 1;
    public string LocalTime { get; set; } = "20:00";
    public string Timezone { get; set; } = "Europe/Kyiv";
    public DateTimeOffset NextDueAt { get; set; }
    public long Version { get; set; }
    // «Друг пише першим» (Друг+): the reminder text is composed by AI from memory.
    public bool Personal { get; set; }
}
public sealed class ReminderOccurrence : OwnedEntity
{
    public long Id { get; set; }
    public DateTimeOffset ScheduledAt { get; set; }
    public string Status { get; set; } = "queued";
    public long Version { get; set; }
}
public sealed class InboxUpdate
{
    public long Id { get; set; }
    public long? TelegramId { get; set; }
    public string Payload { get; set; } = "";
    public string Status { get; set; } = "queued";
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
}
public sealed class OutboxMessage
{
    public long Id { get; set; }
    public Guid? UserId { get; set; }
    public long? Destination { get; set; }
    public Guid OperationId { get; set; } = Guid.NewGuid();
    public int PartIndex { get; set; }
    public string Text { get; set; } = "";
    public string? Markup { get; set; }
    public string Kind { get; set; } = "ui";
    public Guid? SessionId { get; set; }
    public long? MemoryVersion { get; set; }
    public long? TurnRevision { get; set; }
    public long? ReplyToId { get; set; }
    public long? ReminderVersion { get; set; }
    public bool Burst { get; set; }
    public string Status { get; set; } = "queued";
    public long? TelegramMessageId { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? ErrorCode { get; set; }
}
public sealed class PollCheckpoint { public int Id { get; set; } = 1; public long Offset { get; set; } }
public sealed class KnowledgeSource
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Hash { get; set; } = "";
    public bool Active { get; set; } = true;
    public int ImportVersion { get; set; } = 1;
    public DateTimeOffset ImportedAt { get; set; }
}
public sealed class KnowledgeChunk
{
    public long Id { get; set; }
    public long SourceId { get; set; }
    public int Ordinal { get; set; }
    public int PageStart { get; set; }
    public int PageEnd { get; set; }
    public string? Heading { get; set; }
    public string Text { get; set; } = "";
    public string[] Terms { get; set; } = [];
}

// Telegram Stars payment ledger. Rows survive data deletion (UserId becomes null)
// because the charge id and Telegram ID are needed for refunds.
public sealed class Payment
{
    public long Id { get; set; }
    public Guid? UserId { get; set; }
    public long TelegramId { get; set; }
    public string Kind { get; set; } = "plus";
    public int Amount { get; set; }
    public string Currency { get; set; } = "XTR";
    public string TelegramChargeId { get; set; } = "";
    public string ProviderChargeId { get; set; } = "";
    public DateTimeOffset PaidAt { get; set; }
    public DateTimeOffset? PeriodStart { get; set; }
    public DateTimeOffset? PeriodEnd { get; set; }
    public DateTimeOffset? RefundedAt { get; set; }
}
public sealed class Habit : OwnedEntity
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class HabitMark
{
    public long Id { get; set; }
    public long HabitId { get; set; }
    public DateOnly Day { get; set; }
}
