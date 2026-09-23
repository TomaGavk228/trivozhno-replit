using Microsoft.Extensions.Logging.Abstractions;
using Trivozhno.Features.Conversation;
using Trivozhno.Infrastructure.Groq;

namespace Trivozhno.Tests;

public sealed class DialogueSelectionTests
{
    private readonly DialogueExamples examples = new(NullLogger<DialogueExamples>.Instance);

    [Theory]
    [InlineData("Привіт", "Привіт)" )]
    [InlineData("Хочу поговорити, але не знаю про що", "Тоді почну з дрібниці")]
    [InlineData("Поможи відволіктися", "заходиш у кімнату")]
    [InlineData("Не помогло", "із вправами зав’язуємо")]
    [InlineData("Не хочу", "ця тема не зайшла")]
    [InlineData("Парк", "У парку можна")]
    public void RealTurnSelectsARelevantDemonstration(string userText, string expected)
    {
        var messages = examples.BuildMessages(650, [new AiMessage("user", userText)]);
        Assert.Contains(messages, m => m.Role == "assistant" && m.Content.Contains(expected));
        Assert.True(TokenEstimate.Count(messages) <= 650);
    }

    [Fact]
    public void CurrentRefusalHasPriorityOverEarlierUnrelatedTopic()
    {
        var messages = examples.BuildMessages(650,
            [new AiMessage("user", "Привіт"), new AiMessage("assistant", "Привіт)"),
             new AiMessage("user", "Не хочу")]);
        Assert.Equal("assistant", messages[^1].Role);
        Assert.Contains(messages, m => m.Content.Contains("ця тема не зайшла"));
    }

    [Fact]
    public void UnrelatedTurnDoesNotInheritDefaultFourExamples()
    {
        var messages = examples.BuildMessages(650, [new AiMessage("user", "Чому небо блакитне?")]);
        Assert.Empty(messages);
    }
}
