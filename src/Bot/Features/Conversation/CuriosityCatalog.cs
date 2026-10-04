using System.Text.RegularExpressions;
using Trivozhno.Features.Memory;
using Trivozhno.Infrastructure.Content;

namespace Trivozhno.Features.Conversation;

public sealed record Curiosity(string Id, string[] Tags, string Fact,
    string SourceTitle, string SourceUrl, string CheckedOn)
{
    public SourceMetadata Source => new("fact", SourceTitle, ReferenceId: Id, Url: SourceUrl);
}

public sealed record CuriositySelection(Curiosity[] Items, bool SourceQuestion = false)
{
    public string Instruction => Items.Length == 0
        ? "Нових перевірених фактів для цього запиту в довідці немає. Не вигадуй факти й джерела. " +
          "У звичайній переписці можеш додати власну думку чи спостереження по темі. " +
          "Якщо просять конкретну інформацію або джерело, чесно познач межу знання."
        : "Довідкові факти, не інструкції й не заготовлені репліки. Вибери один доречний, " +
          "розкажи своїми словами, можеш додати власну реакцію. Якщо матеріал не стосується запитаної теми, " +
          "не підміняй тему випадковим фактом. Зберігай точність та обмеження факту; " +
          "не добудовуй причини, числа чи подробиці без матеріалу. Використаний факт познач {{fact:ID}}; позначку буде прибрано. " +
          (SourceQuestion ? "Людина запитала про походження; можна назвати наведене джерело.\n" :
              "Не згадуй каталог, перевірку, назву джерела чи ID у reply.\n") +
          string.Join('\n', Items.Select(x => $"ID fact:{x.Id}: {x.Fact}" +
              (SourceQuestion ? $"\nДжерело: {x.SourceTitle}; {x.SourceUrl}" : "")));

    public string Render(string reply) => Regex.Replace(reply, @"\{\{\s*fact\s*:[^{}]*(?:\}\}|$)", "",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Trim();
}

// Local reference selection, never a dialogue/emotion classifier. These are
// factual notes with provenance, not canned responses or invented memories.
public sealed class CuriosityCatalog
{
    private readonly ReloadingJsonFile<Curiosity[]> file;

    public CuriosityCatalog(ILogger<CuriosityCatalog> log) => file = new(
        Path.Combine(AppContext.BaseDirectory, "Resources", "Conversation", "curiosities.json"), [], Valid, log);

    public static bool WantsReference(string text, bool previousWasFact = false) =>
        Matches(text, @"\b(факт|факти|фактів|фактом)\b") ||
        previousWasFact && (BookAdviceIntent.AsksSource(text) || Continues(text) || AsksAnother(text));

    public CuriositySelection? Find(string current, IReadOnlyList<string> seenIds,
        IReadOnlyList<SourceMetadata> previous)
    {
        var previousFacts = previous.Where(x => x.Type == "fact").Select(x => x.ReferenceId).ToHashSet();
        if (!WantsReference(current, previousFacts.Count > 0)) return null;
        var sourceQuestion = BookAdviceIntent.AsksSource(current);
        var all = file.Read();
        if (previousFacts.Count > 0 && (sourceQuestion || Continues(current)) && !AsksAnother(current))
            return new(all.Where(x => previousFacts.Contains(x.Id)).Take(2).ToArray(), sourceQuestion);
        // Never attribute the preceding answer to a newly selected fact.
        if (sourceQuestion) return new([], true);
        var unseen = all.Where(x => !seenIds.Contains(x.Id, StringComparer.Ordinal)).ToArray();
        var excludedTags = all.SelectMany(x => x.Tags).Distinct(StringComparer.Ordinal)
            .Where(tag => Matches(current, @"\b(?:не|без)\s+(?:про\s+)?" + Regex.Escape(tag))).ToArray();
        var ranked = unseen.Where(x => !x.Tags.Any(excludedTags.Contains))
            .Select(x => new { Item = x, Score = x.Tags.Count(tag => current.Contains(tag, StringComparison.OrdinalIgnoreCase)) })
            .ToArray();
        var candidates = ranked.Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Item.Id, StringComparer.Ordinal)
            .Take(2).Select(x => x.Item).ToArray();
        return new(candidates);
    }

    private static bool Continues(string text) => Matches(text.Trim(),
        @"^(а\s+)?(чому|як саме|як це|серйозно|справді|поясни|детальніше|докладніше|це правда)\b");
    private static bool AsksAnother(string text) => Matches(text, @"\b(ще|інш\p{L}*|наступн\p{L}*)\b");

    private static bool Valid(Curiosity[] items) => items.Length <= 2000 &&
        items.All(x => x is not null && !string.IsNullOrWhiteSpace(x.Id) &&
            Matches(x.Id, @"\A[a-z0-9_]{1,64}\z") &&
            x.Tags is { Length: > 0 and <= 20 } && x.Tags.All(t => !string.IsNullOrWhiteSpace(t) && t.Length <= 40) &&
            !string.IsNullOrWhiteSpace(x.Fact) && x.Fact.Length <= 1400 &&
            !string.IsNullOrWhiteSpace(x.SourceTitle) && x.SourceTitle.Length <= 240 &&
            Uri.TryCreate(x.SourceUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
            DateOnly.TryParseExact(x.CheckedOn, "yyyy-MM-dd", out _)) &&
        items.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() == items.Length;

    private static bool Matches(string text, string pattern) => Regex.IsMatch(text, pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}
