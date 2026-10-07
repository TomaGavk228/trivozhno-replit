using Trivozhno.Infrastructure.Ai;

namespace Trivozhno.Features.Conversation;

// One generation: visible reply plus continuity metadata. No rewrite pass.
public sealed class ChatResponder(IAiClient ai)
{
    public async Task<AiResult> Reply(ChatContext context, CancellationToken ct)
    {
        var result = await ai.Complete(context.Messages, context.Configuration.Generation, ct);
        return result with { Sources = context.References.Where(x => result.UsedSources.Contains(x.Id)).ToArray() };
    }
}
