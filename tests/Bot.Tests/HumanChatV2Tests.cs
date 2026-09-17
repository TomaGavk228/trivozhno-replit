using Trivozhno.Features.Memory;
using Trivozhno.Infrastructure.Groq;

namespace Trivozhno.Tests;

public sealed class HumanChatV2Tests
{
    [Theory]
    [InlineData("Мені сьогодні дуже погано і сил нема")]
    [InlineData("Хз")]
    [InlineData("Нічого не хочу")]
    [InlineData("Вона прочитала і не відповіла")]
    public void CasualOrSupportMessagesDoNotAutomaticallyRetrieveBooks(string text)
        => Assert.False(ConversationMemory.ShouldUseKnowledge(text));

    [Theory]
    [InlineData("Порадь, що мені робити")]
    [InlineData("Що мені робити з цією ситуацією?")]
    [InlineData("Можеш щось підказати?")]
    [InlineData("Поясни, чому так відбувається")]
    [InlineData("Звідки ця інформація?")]
    public void ExplicitAdviceOrKnowledgeRequestsCanRetrieveBooks(string text)
        => Assert.True(ConversationMemory.ShouldUseKnowledge(text));

    [Fact]
    public void NormalChatUsesSmallerCompletionBudget()
    {
        var payload = GroqClient.Payload("openai/gpt-oss-120b", [new("user", "Привіт")], false);
        Assert.Equal(700, payload["max_completion_tokens"]);
        Assert.Equal(0.7, payload["temperature"]);
        Assert.Equal("low", payload["reasoning_effort"]);
    }
}
