using Trivozhno.Features.Memory;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Resources;

namespace Trivozhno.Tests;

public sealed class HumanChatV2Tests
{
    [Theory]
    [InlineData("Мені сьогодні дуже погано і сил нема")]
    [InlineData("Хз")]
    [InlineData("Нічого не хочу")]
    [InlineData("Вона прочитала і не відповіла")]
    [InlineData("Поговори зі мною")]
    [InlineData("Розкажи якусь історію")]
    [InlineData("Звідки ця інформація?")]
    public void CasualSupportStoryAndSourceMessagesDoNotAutomaticallyRetrieveBooks(string text)
        => Assert.False(ConversationMemory.ShouldUseKnowledge(text));

    [Theory]
    [InlineData("Порадь, що мені робити")]
    [InlineData("Що мені робити з цією ситуацією?")]
    [InlineData("Можеш щось підказати?")]
    [InlineData("Поясни, чому так відбувається")]
    [InlineData("Допоможи мені розібратися")]
    [InlineData("Як мені з цим бути?")]
    public void ExplicitAdviceOrKnowledgeRequestsCanRetrieveBooks(string text)
        => Assert.True(ConversationMemory.ShouldUseKnowledge(text));

    [Theory]
    [InlineData("Розкажи якусь історію")]
    [InlineData("Розкажи смішний випадок")]
    [InlineData("Можеш розповісти життєву історію?")]
    [InlineData("Розкажи щось дивне")]
    public void ExplicitStoryRequestsUseStoryBank(string text)
        => Assert.True(ConversationMemory.ShouldUseStoryBank(text));

    [Theory]
    [InlineData("Я хочу розказати тобі історію")]
    [InlineData("У мене сьогодні був дивний випадок")]
    [InlineData("Поговори зі мною")]
    public void UserStoriesAndNormalChatDoNotTriggerStoryBank(string text)
        => Assert.False(ConversationMemory.ShouldUseStoryBank(text));

    [Fact]
    public void StorySelectionUsesRequestedTone()
    {
        StorySeed[] bank =
        [
            new("fun", ["funny", "awkward"], "funny"),
            new("warm", ["wholesome"], "warm")
        ];

        var picked = ConversationMemory.SelectStories(bank, "Розкажи смішну історію", 1, 1);

        Assert.Single(picked);
        Assert.Contains("funny", picked[0].Tags);
    }

    [Fact]
    public void NormalChatUsesSmallerCompletionBudget()
    {
        var payload = GroqClient.Payload("openai/gpt-oss-120b", [new("user", "Привіт")], false);
        Assert.Equal(700, payload["max_completion_tokens"]);
        Assert.Equal(0.7, payload["temperature"]);
        Assert.Equal("low", payload["reasoning_effort"]);
    }
}
