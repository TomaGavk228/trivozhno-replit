using Trivozhno.Features.Conversation;
using System.Text.Json;
using Trivozhno.Features.Memory;
using Trivozhno.Infrastructure.Groq;

namespace Trivozhno.Tests;

public sealed class HumanChatV4Tests
{
    [Fact]
    public void StructuredTurnIsSmallNonReasoningAndHasBoundedProfileDelta()
    {
        var payload = GroqClient.TurnPayload(
            "qwen/qwen3.8-27b",
            [new("user", "Привіт")]);

        Assert.Equal(0.7, payload["temperature"]);
        Assert.Equal(360, payload["max_completion_tokens"]);
        Assert.Equal("none", payload["reasoning_effort"]);
        Assert.False(payload.ContainsKey("reasoning_format"));

        var json = JsonSerializer.Serialize(payload["response_format"]);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("json_schema", root.GetProperty("type").GetString());
        Assert.True(root.GetProperty("json_schema").GetProperty("strict").GetBoolean());

        var schema = root.GetProperty("json_schema").GetProperty("schema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var required = schema.GetProperty("required")
            .EnumerateArray()
            .Select(x => x.GetString())
            .ToArray();

        Assert.Contains("conversation_state", required);
        Assert.Contains("knowledge_query", required);
        Assert.Contains("profile_delta", required);
        Assert.Contains("reply", required);
        Assert.DoesNotContain("story_query", required);

        var delta = schema.GetProperty("properties").GetProperty("profile_delta");
        var allowed = delta.GetProperty("items").GetProperty("enum")
            .EnumerateArray()
            .Select(x => x.GetString())
            .ToArray();
        Assert.Contains("likes_humor", allowed);
        Assert.Contains("fewer_questions", allowed);
    }

    [Fact]
    public void StructuredTurnIsAlsoSupportedByGptOssFallback()
    {
        var payload = GroqClient.TurnPayload(
            "openai/gpt-oss-120b",
            [new("user", "Привіт")]);

        Assert.Equal("low", payload["reasoning_effort"]);
        Assert.Equal(false, payload["include_reasoning"]);
        Assert.True(payload.ContainsKey("response_format"));
    }

    [Fact]
    public void ConversationStateRoundTripsThroughExistingMessageMetadata()
    {
        var metadata = ConversationMemory.BuildMetadata(
            "людина відкинула поради й зараз хоче просто нормальної розмови",
            [new("book", "Книга", 12, 13, 42)]);

        Assert.Equal(
            "людина відкинула поради й зараз хоче просто нормальної розмови",
            ConversationMemory.ExtractConversationState(metadata));

        using var json = JsonDocument.Parse(metadata);
        Assert.Equal(
            "book",
            json.RootElement.GetProperty("sources")[0].GetProperty("type").GetString());
    }

    [Fact]
    public void StyleProfileOnlyAcceptsKnownBoundedSignals()
    {
        var profile = ChatStyleProfile.Apply(
            "",
            ["likes_humor", "DROP TABLE Users", "light_punctuation"]);

        var parsed = ChatStyleProfile.Parse(profile);
        Assert.Contains("likes_humor", parsed);
        Assert.Contains("light_punctuation", parsed);
        Assert.DoesNotContain("DROP TABLE Users", parsed);
    }

    [Fact]
    public void LegacySourceArrayDoesNotBreakConversationState()
    {
        Assert.Equal("", ConversationMemory.ExtractConversationState("[]"));
        Assert.Equal("", ConversationMemory.ExtractConversationState(null));
    }

    [Fact]
    public void PlainChatCompletionBudgetIsReduced()
    {
        var payload = GroqClient.Payload(
            "qwen/qwen3.8-27b",
            [new("user", "Привіт")],
            false);

        Assert.Equal(240, payload["max_completion_tokens"]);
        Assert.Equal(0.72, payload["temperature"]);
        Assert.Equal("none", payload["reasoning_effort"]);
    }

    [Fact]
    public void GreetingTurnGuidanceStaysPlain()
    {
        var guidance = ChatResponder.TurnGuidance("Привіт");
        Assert.Contains("ПРИВІТАТИСЯ", guidance);
        Assert.Contains("коротке природне привітання", guidance);
    }

    [Fact]
    public void AdviceTurnGuidanceAllowsOnlyOneConcreteIdea()
    {
        var guidance = ChatResponder.TurnGuidance("Що мені робити?");
        Assert.Contains("ДАТИ ОДНУ ПОРАДУ", guidance);
        Assert.Contains("одну конкретну ідею", guidance);
    }

    [Fact]
    public void RefusalTurnGuidanceDoesNotPushAnotherTask()
    {
        var guidance = ChatResponder.TurnGuidance("Не хочу нічого робити");
        Assert.Contains("ПРИЙНЯТИ МЕЖУ", guidance);
        Assert.Contains("продовжити розмову самому", guidance, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public void AdviceAfterRefusalRespectsTheBoundary()
    {
        var guidance = ChatResponder.TurnGuidance(
            "Що мені робити?",
            ["Та нема сил", "Не хочу нічого робити", "Що мені робити?"]);

        Assert.Contains("ПОРАДУ ПІСЛЯ ВІДМОВИ", guidance);
        Assert.Contains("відкласти рішення", guidance);
    }


    [Theory]
    [InlineData("Як мені позбутися тривоги")]
    [InlineData("Як позбутися тривоги")]
    [InlineData("Що мені робити?")]
    public void AdvicePhrasesAreClassifiedAsAdvice(string text)
    {
        Assert.Equal(DialogueAct.Advice, ChatResponder.ClassifyDialogueAct(text));
    }

    [Theory]
    [InlineData("Не хочу")]
    [InlineData("Не хочу нічого робити")]
    public void RefusalGuidanceKeepsConversationAlive(string text)
    {
        var guidance = ChatResponder.TurnGuidance(text);
        Assert.Contains("ПРОДОВЖИТИ РОЗМОВУ САМОМУ", guidance);
        Assert.Contains("не від розмови", guidance);
    }

    [Theory]
    [InlineData("Угу")]
    [InlineData("Погано")]
    [InlineData("Не знаю")]
    public void ShortReplyGuidanceMakesBotCarrySomeConversation(string text)
    {
        var guidance = ChatResponder.TurnGuidance(text);
        Assert.Contains("ПІДХОПИТИ РОЗМОВУ САМОМУ", guidance);
        Assert.Contains("новий", guidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GateRejectsDeadEndAfterRefusal()
    {
        var quality = ReplyQualityGate.Check(DialogueAct.Refusal, "Не хочу", "Добре.");
        Assert.False(quality.Accept);
    }

    [Fact]
    public void GateNoLongerPolicesWordChoiceInOtherwiseNormalReplies()
    {
        // The gate used to reject replies for containing specific words/roots
        // (physiology claims, "canned advice" stems, imperative verbs, cliche
        // phrases) regardless of whether the reply actually read naturally.
        // That policing is gone: style is the prompt's job, not a regex's.
        var quality = ReplyQualityGate.Check(
            DialogueAct.Advice,
            "Як мені позбутися тривоги",
            "Спробуй просто подихати трохи повільніше, це реально заспокоює.");
        Assert.True(quality.Accept);
    }

    [Fact]
    public void EmergencyFallbackNeverInventsConversationFacts()
    {
        var reply = ReplyQualityGate.EmergencyFallback(DialogueAct.Sharing);

        Assert.DoesNotContain("TikTok", reply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("тривог", reply, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("невдало сформулював", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GateNoLongerRequiresStemmedContextAnchor()
    {
        // The old 5-letter stem-matching "context anchor" check rejected replies
        // that didn't literally re-share a word root with recent messages -- too
        // fragile for real conversation. The gate now only blocks bare dead-end
        // acknowledgments after a refusal/short reply, not topic drift.
        var quality = ReplyQualityGate.Check(
            DialogueAct.ShortReply,
            "Угу",
            "До речі, завтра буде цікава погода.",
            ["У мене нема сил", "Угу"]);

        Assert.True(quality.Accept);
    }

}
