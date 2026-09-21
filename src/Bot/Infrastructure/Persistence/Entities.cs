namespace Trivozhno.Infrastructure.Persistence;

public enum UserState { MainMenu, ChatActive, ConfessionDraft, ConfessionConfirm, ConfessionSending, MoodSelect, MoodNote, MoodHistory, Settings, ReminderFrequency, ReminderTime, ConfirmClearMemory, ConfirmDeleteData, ConfirmDiscard, ConfirmMoodContext }

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
    public bool MoodContextEnabled { get; set; }
    public bool MoodConsentShown { get; set; }
    public string ChatStyleProfile { get; set; } = "";
    public bool Blocked { get; set; }
    public int PendingFrequency { get; set; } = 1;
    public bool CustomTime { get; set; }
    public DateTimeOffset SummaryNextAt { get; set; }
    public DateTime HistoryEndLocal { get; set; }
    public DateTimeOffset? HistoryCursorTime { get; set; }
    public long? HistoryCursorId { get; set; }
    public DateTimeOffset? HistoryLastTime { get; set; }
    public long? HistoryLastId { get; set; }
    public void Go(UserState state) { State = state; UiToken = Guid.NewGuid().ToString("N")[..12]; }
}
public abstract class OwnedEntity { public Guid UserId { get; set; } }
public sealed class ChatSession : OwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public bool HasAnswer { get; set; }
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
    public bool QueueNoticeShown { get; set; }
    public bool MoodDerived { get; set; }
    public string SourcesJson { get; set; } = "[]";
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
    public long? ReminderVersion { get; set; }
    public string Status { get; set; } = "queued";
    public long? TelegramMessageId { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
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
