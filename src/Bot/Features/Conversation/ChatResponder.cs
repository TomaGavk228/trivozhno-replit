using Trivozhno.Infrastructure.Groq;

namespace Trivozhno.Features.Conversation;

// One generation: visible reply plus continuity metadata. No rewrite pass.
public sealed class ChatResponder(IAiClient ai)
{
    public Task<AiResult> Reply(ChatContext context, CancellationToken ct) =>
        ai.Complete(context.Messages, context.Configuration.Generation, ct);
}
