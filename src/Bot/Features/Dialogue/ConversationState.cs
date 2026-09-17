using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;

namespace Trivozhno.Features.Dialogue;

// Ephemeral dialogue observations, never a replacement for long-term memory.
public sealed record ConversationState
{
    public string Topic { get; init; } = "";
    public string Need { get; init; } = "chat";
    public string Tone { get; init; } = "neutral";
    public string Questions { get; init; } = "normal";
    public string Advice { get; init; } = "ask_first";
    public string LastAction { get; init; } = "";
    public string Feedback { get; init; } = "";
    public string OpenThread { get; init; } = "";
    public string Length { get; init; } = "brief";
}

public sealed class ConversationStateRow : OwnedEntity
{
    public Guid SessionId { get; set; }
    public long MemoryVersion { get; set; }
    public long AppliedThroughId { get; set; }
    public string Json { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
    public bool MoodDerived { get; set; }
}

public static class DialogueJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}

public interface IConversationStateStore
{
    Task<ConversationState> Prepare(BotUser user, ChatMessage current, CancellationToken ct);
    Task Save(BotUser user, ChatMessage current, ConversationState state, bool hasMood, CancellationToken ct);
}

public sealed class ConversationStateStore(BotDb db, IClock clock, BotOptions options) : IConversationStateStore
{
    public async Task<ConversationState> Prepare(BotUser user, ChatMessage current, CancellationToken ct)
    {
        var row = await db.Set<ConversationStateRow>().SingleOrDefaultAsync(x => x.UserId == user.Id, ct);
        var state = new ConversationState();
        var retainsMood = false;
        if (row is not null && row.SessionId == current.SessionId && row.MemoryVersion == user.MemoryVersion &&
            clock.UtcNow - row.UpdatedAt < TimeSpan.FromHours(options.StateIdleHours) &&
            (!row.MoodDerived || user.MoodContextEnabled && options.Mood))
        {
            try { state = JsonSerializer.Deserialize<ConversationState>(row.Json, DialogueJson.Options) ?? state; retainsMood = row.MoodDerived; }
            catch (JsonException) { /* Recover from corrupt ephemeral state; history remains intact. */ }
        }
        state = FeedbackPolicy.Apply(state, current.Text);
        // Caller owns SaveChanges + transaction under the per-user lock. Persist explicit feedback even if API fails.
        await Save(user, current, state, retainsMood, ct);
        return state;
    }
    public async Task Save(BotUser user, ChatMessage current, ConversationState state, bool hasMood, CancellationToken ct)
    {
        var row = await db.Set<ConversationStateRow>().SingleOrDefaultAsync(x => x.UserId == user.Id, ct);
        if (row is null) { row = new() { UserId = user.Id }; db.Add(row); }
        if (row.SessionId == current.SessionId && row.MemoryVersion == user.MemoryVersion && row.AppliedThroughId > current.Id) return;
        row.SessionId = current.SessionId; row.MemoryVersion = user.MemoryVersion; row.AppliedThroughId = current.Id;
        row.Json = DialogueJson.Write(state); row.MoodDerived = hasMood; row.UpdatedAt = clock.UtcNow;
    }
}

public static class FeedbackPolicy
{
    private static bool Match(string text, string pattern) => Regex.IsMatch(text, pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static bool WantsDetail(string text) => Match(text, @"\b(докладно|детально|розгорнуто|покроково|подробиц\w*|по кроках)\b") &&
        !Match(text, @"\b(не треба|не потрібно|без)\s+(детал\w*|подробиц\w*|доклад\w*)");
    public static bool RequestsAdvice(string text) => Match(text, @"\b(порадь|підкажи|дай пораду|що (мені )?робити|як (мені )?(краще |можна )?(зробити|почати|впоратися|сказати))\b");
    public static ConversationState Apply(ConversationState state, string text)
    {
        // Conservative first pass only. The model handles semantic feedback in the same response.
        // Quoted statements and narrated speech are not direct preferences.
        var direct = Regex.Replace(text, "[«\"“].*?[»\"”]", "", RegexOptions.Singleline, TimeSpan.FromMilliseconds(100));
        direct = Regex.Replace(direct, @"(?m)^\s*>.*$", "", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        var next = state with { Need = state.Need == "repair" ? "chat" : state.Need, Advice = state.Advice == "requested" ? "ask_first" : state.Advice, Length = state.Length == "detailed" ? "brief" : state.Length };
        if (Match(direct, @"\b(він|вона|друг|подруга|мама|тато)\s+(сказав|сказала|каже|написав|написала)\b")) return next;
        var feedback = false;
        if (Match(direct, @"\b(не (питай|розпитуй)|без (питань|запитань)|забагато (питань|запитань)|досить (питань|запитань))\b"))
        { next = next with { Questions = "avoid" }; feedback = true; }
        else if (Match(direct, @"\b(можеш (питати|запитувати)|став (питання|запитання)|запитай мене)\b"))
            next = next with { Questions = "normal" };
        if (Match(direct, @"\b(не (хочу|треба|потрібно) (твоїх )?порад|без порад|не (радь|пропонуй (вправи|рішення))|не просив порад)\b"))
        { next = next with { Advice = "avoid", Need = "listen" }; feedback = true; }
        else if (RequestsAdvice(direct)) next = next with { Advice = "requested", Need = "advice" };
        if (Match(direct, @"\b(коротше|коротко|стисло|задовго|забагато тексту)\b"))
        { next = next with { Length = "short" }; feedback = true; }
        else if (WantsDetail(direct)) next = next with { Length = "detailed" };
        if (Match(direct, @"\b(твоя відповідь|ти (мене )?(не розумієш|дратуєш)|говори нормально|відповідаєш (погано|як робот)|не те питаю)\b"))
        { next = next with { Need = "repair" }; feedback = true; }
        if (Match(direct, @"\b(змінимо тему|про інше|інша тема|досить про це)\b")) next = next with { Topic = "", OpenThread = "", Need = "chat" };
        return feedback ? next with { Feedback = Clip(direct, 180) } : next;
    }
    public static ConversationState Merge(ConversationState before, ConversationState model) => model with
    {
        Questions = before.Questions == "avoid" ? "avoid" : model.Questions,
        Advice = before.Advice == "avoid" ? "avoid" : model.Advice,
        Feedback = string.IsNullOrWhiteSpace(model.Feedback) ? before.Feedback : model.Feedback,
        Length = before.Length == "short" ? "short" : model.Length
    };
    public static string Clip(string value, int length) => value.Length <= length ? value : value[..(char.IsHighSurrogate(value[length - 1]) ? length - 1 : length)];
}
