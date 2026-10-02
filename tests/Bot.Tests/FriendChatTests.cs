using Trivozhno.Features.Conversation;
using Trivozhno.Infrastructure.Groq;

namespace Trivozhno.Tests;

public sealed class FriendChatTests
{
    private static AiMessage[] Chat(params string[] user)
    {
        var list = new List<AiMessage>();
        foreach (var u in user) { list.Add(new("user", u)); list.Add(new("assistant", "ок")); }
        list.RemoveAt(list.Count - 1);
        return list.ToArray();
    }

    [Theory]
    [InlineData("Привіт", DialogueAct.Greeting)]
    [InlineData("та таке собі", DialogueAct.ShortReply)]
    [InlineData("Не знаю", DialogueAct.ShortReply)]
    [InlineData("Ні, не хочу", DialogueAct.Refusal)]
    [InlineData("Допоможи мені позбутися тривожності", DialogueAct.Advice)]
    public void ClassifiesWhatTheMessageDoes(string text, DialogueAct expected) =>
        Assert.Equal(expected, DialogueClassifier.Classify(text));

    [Fact]
    public void RepeatedDontKnowSwitchesToNoInterrogationHint()
    {
        var signals = FriendTurn.Analyze(Chat("Втомився від думок", "Не знаю", "Не знаю"));
        Assert.Equal(2, signals.ShortRepliesInRow);
        Assert.Contains("Не допитуй", FriendTurn.Hint(signals));
    }

    [Fact]
    public void CrisisPhraseTriggersSafetyHintAndNoBook()
    {
        var chat = Chat("не хочу більше жити");
        Assert.True(FriendTurn.Analyze(chat).Crisis);
        Assert.Contains("7333", FriendTurn.Hint(FriendTurn.Analyze(chat)));
    }

    [Fact]
    public void FirstHelpRequestDoesNotOpenTheBook()
    {
        Assert.Null(BookAdviceIntent.Plan(Chat("Втомився від тривожності", "Допоможи мені позбутися тривожності")));
    }

    [Fact]
    public void ExplicitTechniqueRequestOpensTheBook()
    {
        Assert.NotNull(BookAdviceIntent.Plan(Chat("дай вправу від тривоги")));
    }

    [Fact]
    public void RefusalBlocksBookUntilExplicitSourceAsk()
    {
        Assert.Null(BookAdviceIntent.Plan(Chat("Як позбутися тривоги", "Ні, не хочу", "як позбутися тривоги")));
    }

    [Theory]
    [InlineData("Розумію, це вичерпує.")]
    [InlineData("Спробуй:\n- подихати\n- пройтись")]
    [InlineData("що сталося? а коли? а чому?")]
    public void GateRejectsNonChatShapes(string reply) =>
        Assert.False(ReplyQualityGate.Check(DialogueAct.Sharing, "x", reply).Accept);

    [Fact]
    public void GateAcceptsShortHumanReply() =>
        Assert.True(ReplyQualityGate.Check(DialogueAct.Refusal, "ні", "ок, без вправ. я тут").Accept);

    [Fact]
    public void PolishRemovesMarkdownAndLongDash() =>
        Assert.Equal("Привіт - як ти?", ReplyQualityGate.Polish("**Привіт** — як ти?"));
}
