using Microsoft.EntityFrameworkCore;
using Trivozhno.Infrastructure.Telegram;

namespace Trivozhno.Tests;

public sealed class CurrentTurnTests
{
    [PostgresFact]
    public async Task TwoQuickMessagesBecomeOneCurrentTurn()
    {
        await using var r = new TestRig(); await r.Init(); await r.StartChat(); await r.DrainOutbox();
        await r.Inbox.Store(new([new(501, 1, "Почекай,", null), new(502, 1, "я ще допишу думку", null)], 503), default);
        await r.DrainInbox();
        Assert.Equal(2, await r.Read(db => db.Messages.CountAsync(x => x.Role == "user")));
        Assert.False(await r.Processor.Step(default));
        r.Clock.Advance(TimeSpan.FromMilliseconds(1200));
        Assert.True(await r.Processor.Step(default));
        var request = Assert.Single(r.Ai.Requests);
        Assert.Equal("Почекай,\nя ще допишу думку", request.Last(x => x.Role == "user").Content);
        Assert.False(await r.Processor.Step(default));
        await r.DrainOutbox();
        Assert.Single(r.Telegram.Sent, x => x.Text.StartsWith("Відповідь:"));
    }

    [PostgresFact]
    public async Task InputDuringGenerationDiscardsStaleReply()
    {
        await using var r = new TestRig(); await r.Init(); await r.StartChat(); await r.DrainOutbox();
        await r.Text("Перша половина");
        r.Ai.Pause = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var processing = r.Processor.Step(default);
        await r.Ai.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await r.Text("І ось що я хотів сказати");
        r.Ai.Pause.SetResult(new("Стара відповідь", "fake", 30));
        await processing;
        r.Ai.Pause = null;
        Assert.True(await r.Processor.Step(default));
        await r.DrainOutbox();
        Assert.DoesNotContain(r.Telegram.Sent, x => x.Text == "Стара відповідь");
        Assert.Contains(r.Telegram.Sent, x => x.Text.Contains("Перша половина\nІ ось що я хотів сказати"));
    }

    [PostgresFact]
    public async Task NewInputCancelsSecondBubbleAndHistoryKeepsOnlyDeliveredFirst()
    {
        await using var r = new TestRig(); await r.Init(); await r.StartChat(); await r.DrainOutbox();
        await r.Text("Моя думка");
        r.Ai.Pause = new(TaskCreationOptions.RunContinuationsAsynchronously);
        r.Ai.Pause.SetResult(new("Перша завершена думка тут.\n\nДруга завершена думка тут.", "fake", 30));
        Assert.True(await r.Processor.Step(default));
        Assert.True(await r.Outbox.Step(default));
        await r.Text("Стривай, я змінив тему");
        await r.DrainOutbox();
        Assert.Contains(r.Telegram.Sent, x => x.Text == "Перша завершена думка тут.");
        Assert.DoesNotContain(r.Telegram.Sent, x => x.Text == "Друга завершена думка тут.");
        r.Ai.Pause = null;
        Assert.True(await r.Processor.Step(default));
        var context = r.Ai.Requests.Last();
        Assert.Contains(context, x => x.Role == "assistant" && x.Content == "Перша завершена думка тут.");
        Assert.DoesNotContain(context, x => x.Content == "Друга завершена думка тут.");
    }

    [Fact]
    public void ChatSplitOnlyCreatesTwoBubblesForTwoShortParagraphs()
    {
        Assert.Equal(2, TextSplitter.SplitChat("Перша завершена думка.\r\n\r\nДруга завершена думка.").Count);
        Assert.Single(TextSplitter.SplitChat("Привіт!"));
        Assert.Single(TextSplitter.SplitChat("Перший рядок\n\nДругий\n\nТретій"));
    }
}
