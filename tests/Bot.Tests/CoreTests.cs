using System.Globalization;
using System.Text;
using Npgsql;
using Trivozhno.Features.Reminders;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Knowledge;
using Trivozhno.Infrastructure.Telegram;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Trivozhno.Tests;

public sealed class CoreTests
{
    [Theory]
    [InlineData("звичайний текст & <tag> _test_", 4000)]
    [InlineData("👨‍👩‍👧‍👦 🇺🇦 е́ 😀", 15)]
    [InlineData("абвгдеєжзиіїйклмнопрстуфхцчшщьюя", 11)]
    public void SplittingIsLosslessAndUnicodeSafe(string pattern, int maximum)
    {
        var text = string.Concat(Enumerable.Repeat(pattern + "\n\n", 2000));
        var parts = TextSplitter.Split(text, maximum);
        Assert.Equal(text, string.Concat(parts));
        Assert.All(parts, p => { Assert.InRange(p.Length, 1, maximum); Assert.False(char.IsHighSurrogate(p[^1])); Assert.False(char.IsLowSurrogate(p[0])); });
        if (maximum > 20) Assert.All(parts, p => new UTF8Encoding(false, true).GetBytes(p));
    }
    [Fact]
    public void URIParsesEncodedCredentialsAndSslWithoutLeaking()
    {
        var b = new NpgsqlConnectionStringBuilder(ConnectionStrings.Parse("postgres://name%40x:p%40ss%3Aword%2F%25@localhost:5433/test?sslmode=verify-full&connect_timeout=12&channel_binding=require"));
        Assert.Equal("name@x", b.Username); Assert.Equal("p@ss:word/%", b.Password); Assert.Equal(SslMode.VerifyFull, b.SslMode); Assert.Equal(12, b.Timeout);
        Assert.Equal(ChannelBinding.Require, b.ChannelBinding);
        var error = Assert.Throws<InvalidOperationException>(() => ConnectionStrings.Parse("postgres://u:secret@localhost/db?unknown=secret"));
        Assert.DoesNotContain("secret", error.Message);
    }
    [Theory]
    [InlineData("2026-03-28T07:00:00Z", "2026-03-29T06:00:00Z")]
    [InlineData("2026-10-24T06:00:00Z", "2026-10-25T07:00:00Z")]
    public void ReminderUsesCalendarDaysAcrossDst(string before, string expected)
    {
        var previous = DateTimeOffset.Parse(before, CultureInfo.InvariantCulture);
        Assert.Equal(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture), ReminderDates.Next(previous, previous, 1, "09:00", "Europe/Kyiv"));
    }
    [Fact]
    public void ReminderFirstOccurrenceIsNextFutureTimeAndNoBacklog()
    {
        var now = DateTimeOffset.Parse("2026-09-12T19:00:00Z");
        Assert.Equal(DateTimeOffset.Parse("2026-09-13T17:00:00Z"), ReminderDates.First(now, "20:00", "Europe/Kyiv"));
        Assert.True(ReminderDates.Next(now.AddDays(-30), now, 3, "20:00", "Europe/Kyiv") > now);
    }
    [Fact]
    public void ReasoningProfilesAreSeparateAndInternalTextIsRemoved()
    {
        var oss = GroqClient.Payload("openai/gpt-oss-120b", [new("user", "Привіт")], false);
        var qwen = GroqClient.Payload("qwen/qwen3.6-27b", [new("user", "Привіт")], false);
        Assert.Equal(false, oss["include_reasoning"]); Assert.False(oss.ContainsKey("reasoning_format"));
        Assert.Equal("none", qwen["reasoning_effort"]); Assert.False(qwen.ContainsKey("include_reasoning")); Assert.False(qwen.ContainsKey("reasoning_format"));
        Assert.Equal("Привіт!", GroqClient.Clean("<think>internal text</think>Привіт!"));
        Assert.Equal("", GroqClient.Clean("<think>unfinished"));
    }
    [Fact]
    public void LexiconUsesExplicitFormsRatherThanFiveCharacterPrefixes()
    {
        Assert.Contains("тривога", Lexicon.Terms("Я хвилююся"));
        Assert.Equal(Lexicon.Terms("кар’єра"), Lexicon.Terms("кар'єра"));
        Assert.DoesNotContain("довіра", Lexicon.Terms("довіреність"));
    }
    [Fact]
    public void ChunksHavePageMetadataAndBoundedOverlap()
    {
        var pages = new[] { new TextPage(1, string.Concat(Enumerable.Repeat("Перший абзац про довіру. ", 80))), new TextPage(2, string.Concat(Enumerable.Repeat("Інша сторінка про межі. ", 80))) };
        var chunks = BookImporter.Chunk(pages, 1800, 180);
        Assert.True(chunks.Count >= 2); Assert.All(chunks, c => { Assert.InRange(c.Text.Length, 1, 2200); Assert.InRange(c.PageStart, 1, 2); Assert.True(c.PageEnd >= c.PageStart); });
        Assert.All(chunks.Where(c => c.Text.Contains("Перший абзац")), c => Assert.Equal(1, c.PageStart));
        Assert.All(chunks.Where(c => c.Text.Contains("Інша сторінка")), c => Assert.Equal(2, c.PageEnd));
    }
}
