using System.Text.Json;
using Trivozhno.Features.Memory;
using Trivozhno.Infrastructure.Groq;

namespace Trivozhno.Tests;

public sealed class HumanChatV3Tests
{
    [Fact]
    public void StructuredTurnUsesStrictJsonSchemaOnQwen()
    {
        var payload = GroqClient.TurnPayload("qwen/qwen3.8-27b", [new("user", "Привіт")]);

        Assert.Equal(0.65, payload["temperature"]);
        Assert.Equal("low", payload["reasoning_effort"]);

        var json = JsonSerializer.Serialize(payload["response_format"]);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("json_schema", root.GetProperty("type").GetString());
        Assert.True(root.GetProperty("json_schema").GetProperty("strict").GetBoolean());

        var schema = root.GetProperty("json_schema").GetProperty("schema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var required = schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains("conversation_state", required);
        Assert.Contains("knowledge_query", required);
        Assert.Contains("story_query", required);
        Assert.Contains("reply", required);
    }

    [Fact]
    public void StructuredTurnIsAlsoSupportedByGptOssFallback()
    {
        var payload = GroqClient.TurnPayload("openai/gpt-oss-120b", [new("user", "Привіт")]);

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
        Assert.Equal("book", json.RootElement.GetProperty("sources")[0].GetProperty("type").GetString());
    }

    [Fact]
    public void LegacySourceArrayDoesNotBreakConversationState()
    {
        Assert.Equal("", ConversationMemory.ExtractConversationState("[]"));
        Assert.Equal("", ConversationMemory.ExtractConversationState(null));
    }

    [Fact]
    public void NormalSummaryPayloadKeepsExistingBehavior()
    {
        var payload = GroqClient.Payload("openai/gpt-oss-120b", [new("user", "Привіт")], false);
        Assert.Equal(700, payload["max_completion_tokens"]);
        Assert.Equal(0.7, payload["temperature"]);
        Assert.Equal("low", payload["reasoning_effort"]);
    }
}
