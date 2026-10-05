using Trivozhno.Infrastructure.Groq;

namespace Trivozhno.Features.Conversation;

// One text generation. No planner, canned greeting, quality rewrite or extract replacement.
public sealed class ChatResponder(IAiClient ai)
{
    public Task<AiResult> Reply(ChatContext context, CancellationToken ct) =>
        ai.Complete(context.Messages, context.Configuration.Generation, ct);
}
