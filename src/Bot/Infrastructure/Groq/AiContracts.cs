using System.Text;

namespace Trivozhno.Infrastructure.Groq;

public sealed record AiMessage(string Role, string Content);
public sealed record AiResult(
    string Text,
    string Model,
    int Tokens,
    int PromptTokens = 0,
    int CompletionTokens = 0,
    int ReasoningTokens = 0,
    int CachedTokens = 0);
public sealed record AiTurnDraft(
    string ConversationState,
    string KnowledgeQuery,
    IReadOnlyList<string> ProfileDelta,
    string Reply);
public sealed record AiTurnResult(AiTurnDraft Turn, string Model, int Tokens);
public interface IAiClient
{
    Task<AiResult> Complete(IReadOnlyList<AiMessage> messages, bool summary, CancellationToken ct);
    Task<AiTurnResult> CompleteTurn(IReadOnlyList<AiMessage> messages, CancellationToken ct);
}
public sealed class AiUnavailableException(string reason = "unavailable") : Exception(reason)
{
    public string Reason { get; } = reason;
}
public sealed class ContextTooLargeException : Exception { }

public static class TokenEstimate
{
    // Fast conservative estimate used only for local admission control.
    public static int Count(string text) => (Encoding.UTF8.GetByteCount(text) + 2) / 3 + 8;
    public static int Count(IEnumerable<AiMessage> messages) => messages.Sum(m => Count(m.Content) + 8);
}

public sealed class ApiUsage
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string Model { get; set; } = "";
    public int Tokens { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int ReasoningTokens { get; set; }
    public int CachedTokens { get; set; }
    public bool Summary { get; set; }
}

