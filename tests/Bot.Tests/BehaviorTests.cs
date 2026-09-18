using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trivozhno.Features.Memory;
using Trivozhno.Features.Reminders;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Infrastructure.Telegram;

namespace Trivozhno.Tests;

public sealed class BehaviorTests
{
    [PostgresFact]
    public async Task MenuDoesNotInvokeAiAndUnsupportedInputKeepsDraft()
    {
        await using var r = new TestRig(); await r.Init(); await r.Text("/start"); await r.Text("Привіт");
        Assert.Empty(r.Ai.Requests); Assert.Equal(UserState.MainMenu, (await r.User()).State);
        await r.Click("menu:confession"); await r.Text("Текст чернетки");
        await r.Inbox.Store(new([new(100, 1, null, null, true)], 101), default); await r.DrainInbox();
        Assert.Equal(UserState.ConfessionDraft, (await r.User()).State);
        Assert.Equal("Текст чернетки", await r.Read(db => db.DraftParts.Select(x => x.Text).SingleAsync()));
    }
    [PostgresFact]
    public async Task RapidMessagesAreAnsweredInOrderWithPreviousAnswerAndNoCrossUserContext()
    {
        await using var r = new TestRig(); await r.Init(); await r.StartChat(); await r.StartChat(2);
        await r.Text("Перша думка"); await r.Text("Друга думка"); await r.Text("Інша людина", 2);
        Assert.True(await r.Processor.Step(default)); Assert.True(await r.Processor.Step(default)); Assert.True(await r.Processor.Step(default));
        var requests = r.Ai.Requests.ToArray();
        Assert.Equal("Перша думка", requests[0].Last().Content);
        Assert.Equal("Друга думка", requests[1].Last().Content);
        Assert.Contains(requests[1], x => x.Role == "assistant" && x.Content == "Відповідь: Перша думка");
        Assert.DoesNotContain(requests[2], x => x.Content.Contains("Перша думка"));
        await r.DrainOutbox();
        var answers = r.Telegram.Sent.Where(x => x.Destination == 1 && x.Text.StartsWith("Відповідь:")).ToArray();
        Assert.Equal(2, answers.Length); Assert.All(answers, x => { Assert.DoesNotContain("inline_keyboard", x.Markup ?? ""); Assert.Contains("Завершити чат", x.Markup is null ? "" : System.Text.Json.JsonDocument.Parse(x.Markup).RootElement.GetProperty("keyboard")[0][0].GetProperty("text").GetString()); });
    }
    [PostgresFact]
    public async Task ExitKeepsMemoryAndSuppressesLateAnswer()
    {
        await using var r = new TestRig(); await r.Init(); await r.StartChat(); await r.Text("Запам’ятай думку");
        r.Ai.Pause = new(TaskCreationOptions.RunContinuationsAsynchronously); var processing = r.Processor.Step(default);
        await r.Ai.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); await r.Text("🚪 Завершити чат");
        r.Ai.Pause.SetResult(new("Запізніла відповідь", "fake", 1)); await processing;
        Assert.Equal(0, await r.Read(db => db.Messages.CountAsync(x => x.Role == "assistant")));
        Assert.Equal(1, await r.Read(db => db.Messages.CountAsync(x => x.Role == "user")));
        await r.DrainOutbox(); Assert.DoesNotContain(r.Telegram.Sent, x => x.Text == "Запізніла відповідь");
    }
    [PostgresFact]
    public async Task ClearDuringAiDoesNotRestoreDeletedMemory()
    {
        await using var r = new TestRig(); await r.Init(); await r.StartChat(); await r.Text("Старий особистий текст");
        r.Ai.Pause = new(TaskCreationOptions.RunContinuationsAsynchronously); var processing = r.Processor.Step(default);
        await r.Ai.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); await r.Text("/menu");
        await r.Click("menu:settings"); await r.Click("settings:clear"); await r.Click("settings:clear:yes");
        r.Ai.Pause.SetResult(new("Пізній текст", "fake", 1)); await processing;
        Assert.Equal(0, await r.Read(db => db.Messages.CountAsync())); Assert.True((await r.User()).MemoryVersion > 0);
    }
    [PostgresFact]
    public async Task FailureKeepsUnansweredUserTextWithoutFakeAiPair()
    {
        await using var r = new TestRig(); await r.Init(); await r.StartChat(); r.Ai.Fail = true; await r.Text("Потрібно виговоритися");
        await r.Processor.Step(default);
        Assert.Equal("unanswered", await r.Read(db => db.Messages.Select(x => x.Status).SingleAsync())); Assert.False((await r.User()).ReminderPromptShown);
    }
    [PostgresFact]
    public async Task ConfessionEditInvalidatesOldButtonsAndDoubleSendDoesNotDuplicate()
    {
        await using var r = new TestRig(); await r.Init(); await r.Text("/start"); await r.Click("menu:confession");
        await r.Text("Стара версія"); await r.Text("✅ Готово"); var old = (await r.User()).UiToken;
        await r.Click("conf:edit"); await r.Text("Нова версія"); await r.Text("Друга частина");
        await r.Click("conf:send", token: old); Assert.Equal(0, await r.Read(db => db.Submissions.CountAsync()));
        await r.Text("✅ Готово"); var sendToken = (await r.User()).UiToken;
        await r.Click("conf:send"); await r.Click("conf:send", token: sendToken);
        Assert.Equal(1, await r.Read(db => db.Submissions.CountAsync()));
        await r.DrainOutbox(); await r.Tick(); await r.DrainOutbox();
        var sent = Assert.Single(r.Telegram.Sent, x => x.Destination == -100123);
        Assert.Contains("Нова версія\n\nДруга частина", sent.Text); Assert.DoesNotContain("Стара версія", sent.Text); Assert.Null(sent.Markup);
        Assert.Equal(UserState.MainMenu, (await r.User()).State); Assert.True((await r.User()).ReminderPromptShown);
        Assert.Equal(0, await r.Read(db => db.Drafts.CountAsync()));
    }
    [PostgresFact]
    public async Task UnknownDeliveryRequiresManualResolutionAndSurvivesRestart()
    {
        await using var r = new TestRig(); await r.Init(); await r.Text("/start"); await r.Click("menu:confession");
        await r.Text(new string('я', 3900)); await r.Text(new string('ю', 2000)); await r.Text("✅ Готово"); await r.Click("conf:send");
        r.Telegram.Fail = d => d < 0 ? new DeliveryUnknownException() : null;
        await r.DrainOutbox(); await r.Tick();
        Assert.Equal(1, await r.Read(db => db.Outbox.CountAsync(x => x.Status == "delivery_unknown")));
        r.Telegram.Fail = null; await r.Services.GetRequiredService<Maintenance>().Recover(default); await r.DrainOutbox();
        Assert.DoesNotContain(r.Telegram.Sent, x => x.Destination < 0);
        Assert.Equal("unknown", await r.Read(db => db.Submissions.Select(x => x.Status).SingleAsync()));
    }
    [PostgresFact]
    public async Task KnownRefusalCanRetryOnlyFailedPart()
    {
        await using var r = new TestRig(); await r.Init(); await r.Text("/start"); await r.Click("menu:confession");
        await r.Text("Тест доставки"); await r.Text("✅ Готово"); await r.Click("conf:send");
        r.Telegram.Fail = d => d < 0 ? new TelegramFailure(403) : null;
        await r.DrainOutbox(); await r.Tick();
        Assert.False((await r.User()).Blocked);
        r.Telegram.Fail = null; await r.Click("conf:retry"); await r.DrainOutbox(); await r.Tick();
        Assert.Single(r.Telegram.Sent, x => x.Destination < 0);
    }
    [PostgresFact]
    public async Task MultipleMoodsAndLongNoteAreSavedAndHistoryIsLossless()
    {
        await using var r = new TestRig(); await r.Init(); await r.Text("/start"); await r.Click("menu:mood"); await r.Click("mood:4");
        var parts = Enumerable.Range(1, 5).Select(i => $"Частина {i} " + new string('ї', 3000)).ToArray();
        foreach (var part in parts) await r.Text(part);
        await r.Text("✅ Зберегти"); await r.Click("menu:mood"); await r.Click("mood:3"); await r.Text("⏭ Без нотатки");
        Assert.Equal(2, await r.Read(db => db.Moods.CountAsync()));
        Assert.Equal(string.Join("\n\n", parts), await r.Read(db => db.Moods.Where(x => x.Value == 4).Select(x => x.Note).SingleAsync()));
        await r.DrainOutbox(); var before = r.Telegram.Sent.Count;
        await r.Click("menu:mood"); await r.Click("history:latest"); await r.DrainOutbox();
        var history = string.Concat(r.Telegram.Sent.Skip(before).Select(x => x.Text));
        Assert.Contains(string.Join("\n\n", parts), history);
    }
    [PostgresFact]
    public async Task MenuAndSkipAskBeforeDiscardingDraft()
    {
        await using var r = new TestRig(); await r.Init(); await r.Text("/start"); await r.Click("menu:mood"); await r.Click("mood:2"); await r.Text("Залишити нотатку");
        await r.Text("/menu"); Assert.Equal(UserState.ConfirmDiscard, (await r.User()).State);
        await r.Click("discard:no"); Assert.Equal(UserState.MoodNote, (await r.User()).State);
        await r.Text("⏭ Без нотатки"); await r.Click("discard:yes");
        Assert.Null(await r.Read(db => db.Moods.Select(x => x.Note).SingleAsync()));
    }
    [PostgresFact]
    public async Task MoodContextRequiresConsentAndIsExcludedFromNewRequestsAfterDisable()
    {
        await using var r = new TestRig(); await r.Init(); await r.Text("/start");
        var u = await r.User(); await r.WithDb(async db => { db.Moods.Add(new() { UserId = u.Id, Value = 3, Note = "СЕКРЕТНА_НОТАТКА", RecordedAt = r.Clock.UtcNow }); await db.SaveChangesAsync(); });
        await r.Click("menu:talk"); await r.Text("Привіт"); await r.Processor.Step(default);
        Assert.DoesNotContain(r.Ai.Requests.Last(), x => x.Content.Contains("СЕКРЕТНА_НОТАТКА"));
        await r.Text("/menu"); await r.Click("menu:settings"); await r.Click("settings:mood");
        Assert.False((await r.User()).MoodContextEnabled); await r.Click("settings:mood:yes"); await r.Text("/menu");
        await r.Click("menu:talk"); await r.Text("Мій день"); await r.Processor.Step(default);
        Assert.Contains(r.Ai.Requests.Last(), x => x.Content.Contains("СЕКРЕТНА_НОТАТКА"));
        await r.Text("/menu"); await r.Click("menu:settings"); await r.Click("settings:mood"); await r.Text("/menu");
        await r.Click("menu:talk"); await r.Text("Новий запит"); await r.Processor.Step(default);
        Assert.DoesNotContain(r.Ai.Requests.Last(), x => x.Content.Contains("СЕКРЕТНА_НОТАТКА"));
    }
    [PostgresFact]
    public async Task FullDeletionRemovesOwnedDataWithoutCreatingProfileOrTouchingChannel()
    {
        await using var r = new TestRig(); await r.Init(); await r.StartChat(); await r.Text("Старі дані");
        r.Ai.Pause = new(TaskCreationOptions.RunContinuationsAsynchronously); var processing = r.Processor.Step(default); await r.Ai.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await r.Text("/menu"); await r.Click("menu:settings"); await r.Click("settings:delete"); await r.Click("settings:delete:yes");
        r.Ai.Pause.SetResult(new("Не має відновитися", "fake", 1)); await processing; await r.DrainOutbox();
        Assert.Equal(0, await r.Read(db => db.Users.CountAsync())); Assert.Equal(0, await r.Read(db => db.Messages.CountAsync()));
        Assert.Equal(0, await r.Read(db => db.Inbox.CountAsync(x => x.TelegramId != null || x.Payload != "")));
        Assert.Equal(0, await r.Read(db => db.Outbox.CountAsync(x => x.Destination != null || x.Text != "")));
        Assert.DoesNotContain(r.Telegram.Sent, x => x.Destination < 0);
        await r.Text("Знову"); Assert.Equal(0, await r.Read(db => db.Users.CountAsync()));
        await r.Text("/start"); Assert.False((await r.User()).AiNoticeShown);
    }
    [PostgresFact]
    public async Task ReminderOnlyOnceAndNoBacklogOrActiveInputInterruptions()
    {
        await using var r = new TestRig(); await r.Init(); await r.Text("/start"); await r.Click("menu:settings");
        await r.Click("settings:reminders"); await r.Click("rem:change"); await r.Click("rem:freq:2"); await r.Click("rem:custom");
        await r.Text("26:99"); Assert.Equal(UserState.ReminderTime, (await r.User()).State); await r.Text("20:00");
        var u = await r.User(); Assert.True(u.ReminderPromptShown);
        await r.WithDb(async db => { var setting = await db.Reminders.SingleAsync(); setting.NextDueAt = r.Clock.UtcNow.AddMinutes(-10); await db.SaveChangesAsync(); });
        await r.Tick(); await r.Tick(); Assert.Equal(1, await r.Read(db => db.Occurrences.CountAsync()));
        await r.DrainOutbox(); Assert.Single(r.Telegram.Sent, x => x.Text == "Привіт! Як ти сьогодні?");
        await r.Click("menu:talk");
        await r.WithDb(async db => { var setting = await db.Reminders.SingleAsync(); setting.NextDueAt = r.Clock.UtcNow.AddMinutes(-5); await db.SaveChangesAsync(); });
        await r.Tick(); Assert.Equal(1, await r.Read(db => db.Occurrences.CountAsync(x => x.Status == "skipped")));
    }
    [PostgresFact]
    public async Task DisabledMoodModuleDoesNotBreakChatAndConfessions()
    {
        await using var r = new TestRig(); await r.Init(mood: false); await r.Text("/start"); await r.DrainOutbox();
        Assert.DoesNotContain(r.Telegram.Sent, x => (x.Markup ?? "").Contains("menu:mood"));
        await r.Click("menu:mood"); Assert.Equal(UserState.MainMenu, (await r.User()).State);
        await r.Click("menu:talk"); await r.Text("Думка"); await r.Processor.Step(default); Assert.Single(r.Ai.Requests);
        await r.Text("/menu"); await r.Click("menu:confession"); Assert.Equal(UserState.ConfessionDraft, (await r.User()).State);
    }
    [PostgresFact]
    public async Task SummaryFailureKeepsMessagesAndSuccessfulSummaryRetainsRecentTen()
    {
        await using var r = new TestRig(); await r.Init(); await r.StartChat();
        for (var i = 0; i < 12; i++) { await r.Text("Думка " + i); await r.Processor.Step(default); }
        var u = await r.User(); r.Ai.Fail = true;
        using (var s = r.Services.CreateScope()) await Assert.ThrowsAsync<AiUnavailableException>(() => s.ServiceProvider.GetRequiredService<IConversationMemory>().Summarize(u.Id, u.MemoryVersion, default));
        Assert.Equal(24, await r.Read(db => db.Messages.CountAsync()));
        r.Ai.Fail = false;
        using (var s = r.Services.CreateScope()) await s.ServiceProvider.GetRequiredService<IConversationMemory>().Summarize(u.Id, u.MemoryVersion, default);
        Assert.Equal(10, await r.Read(db => db.Messages.CountAsync())); Assert.Equal(1, await r.Read(db => db.Summaries.CountAsync()));
    }
    [PostgresFact]
    public async Task ConversationStateFromPreviousTurnIsPassedIntoNextTurn()
    {
        await using var r = new TestRig(); await r.Init(); await r.StartChat();
        r.Ai.TurnDrafts.Enqueue(new(
            "користувач відкинув пораду; не тиснути питаннями; краще переключити розмову",
            "", "", "ок, тоді без порад"));

        await r.Text("нічого не хочу");
        await r.Processor.Step(default);

        await r.Text("і шо");
        await r.Processor.Step(default);

        var second = r.Ai.Requests.ToArray()[1];
        Assert.Contains(second, x => x.Role == "system" &&
            x.Content.Contains("користувач відкинув пораду") &&
            x.Content.Contains("не тиснути питаннями"));
    }

    [PostgresFact]
    public async Task BookGroundingHappensOnlyAfterModelRequestsKnowledge()
    {
        await using var r = new TestRig(); await r.Init(); await r.StartChat();
        var u = await r.User();
        await r.WithDb(async db =>
        {
            var source = new KnowledgeSource
            {
                Title = "Перевірена психологічна книга",
                Hash = "human-chat-v3-book",
                Active = true,
                ImportedAt = r.Clock.UtcNow
            };
            db.Sources.Add(source);
            await db.SaveChangesAsync();
            db.Chunks.Add(new KnowledgeChunk
            {
                SourceId = source.Id,
                Ordinal = 0,
                PageStart = 10,
                PageEnd = 11,
                Text = "Короткий перевірений фрагмент про тривогу і способи зменшення напруги.",
                Terms = ["тривога", "напруга"]
            });
            await db.SaveChangesAsync();
        });

        r.Ai.TurnDrafts.Enqueue(new(
            "користувач прямо попросив конкретний спосіб заспокоїтися",
            "тривога напруга", "", "можна спробувати одну просту штуку"));
        r.Ai.TurnDrafts.Enqueue(new(
            "користувач попросив допомогу; відповіли коротко без лекції",
            "", "", "я б почав з однієї простої речі, без десяти вправ одразу"));

        await r.Text("що мені робити щоб заспокоїтися?");
        await r.Processor.Step(default);

        var requests = r.Ai.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Contains(requests[1], x => x.Role == "system" &&
            x.Content.Contains("Перевірений довідковий фрагмент") &&
            x.Content.Contains("не змінюй голос співрозмовника на психолога"));

        var assistant = await r.Read(db => db.Messages.SingleAsync(x => x.Role == "assistant"));
        using var metadata = System.Text.Json.JsonDocument.Parse(assistant.SourcesJson);
        Assert.Equal("Перевірена психологічна книга",
            metadata.RootElement.GetProperty("sources")[0].GetProperty("title").GetString());
    }

    [PostgresFact]
    public async Task StoryRequestUsesExternalStorySourceAndRegeneratesNaturalReply()
    {
        await using var r = new TestRig(); await r.Init(); await r.StartChat();
        r.Stories.Result =
        [
            new("allenai/soda (CC-BY-4.0)", "train:123",
                "Two friends miss the same bus and end up discovering a tiny night market while waiting.",
                "A: Well, there goes our bus.\nB: At least that food stall smells incredible.")
        ];

        r.Ai.TurnDrafts.Enqueue(new(
            "користувач хоче відволіктися історією",
            "", "light funny everyday story", "о, є одна"));
        r.Ai.TurnDrafts.Enqueue(new(
            "користувач слухає легку історію",
            "", "", "коротше, двоє друзів запізнилися на автобус і випадково натрапили на нічний базар..."));

        await r.Text("розкажи якусь історію");
        await r.Processor.Step(default);

        Assert.Single(r.Stories.Requests);
        Assert.Equal("light funny everyday story", r.Stories.Requests.Single().Query);

        var requests = r.Ai.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Contains(requests[1], x => x.Content.Contains("AllenAI SODA") && x.Content.Contains("train:123"));

        var assistant = await r.Read(db => db.Messages.SingleAsync(x => x.Role == "assistant"));
        Assert.Contains("train:123", assistant.SourcesJson);
        Assert.Contains("двоє друзів", assistant.Text);
    }

}
