using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Trivozhno.Infrastructure.Persistence;

namespace Trivozhno.Infrastructure.Knowledge;

public sealed record KnowledgeHit(long ChunkId, string Title, int PageStart, int PageEnd, string Text, double Score);
public interface IKnowledgeRetriever
{
    Task<IReadOnlyList<KnowledgeHit>> Search(string query, CancellationToken ct);
    Task<IReadOnlyList<KnowledgeHit>> ReadPassages(IReadOnlyList<long> chunkIds, CancellationToken ct);
}

public static class Lexicon
{
    private static readonly HashSet<string> SupportTopics =
    [
        "тривога", "страх", "самотність", "стосунки", "межі", "самооцінка",
        "провина", "злість", "конфлікт", "втома", "довіра", "ревнощі",
        "розставання", "почуття", "підтримка", "прокрастинація", "перфекціонізм"
    ];
    private static readonly HashSet<string> SearchNoise =
        "робити зробити роблю зроблю допомогти допоможи допоможіть допомога порада поради порадь підкажи побороти подолати позбутися позбавитися впоратися впоратись заспокоїтися заспокоїтись перестати спосіб способи метод методи будь ласка краще знати зрозуміти".Split(' ').ToHashSet();

    public static bool IsSupportTopic(string term) => SupportTopics.Contains(term);
    public static string[] SearchTerms(string text) => Terms(text).Where(x => !SearchNoise.Contains(x))
        .Distinct().Take(24).ToArray();
    private static readonly HashSet<string> Stop = "і й а та але або чи що щоб як так це цей ця ці воно він вона вони ми ви ти я мені мене мої мій моя моє свої свій себе собі тебе тобі твій ваш наш його її їх їм нам вам нас вас у в на до за з із зі для від про при по не ні є був була були бути буде дуже вже ще теж також навіть просто зараз сьогодні вчора коли тоді тому бо лише тільки все всіх всі щось хтось нічого ніхто там тут десь кожен іноді часто завжди привіт добрий день доброго дякую спасибі ок добре гаразд знову хочеться хочу хочуся можу можна може треба потрібно хотів після перед під над між без через один два три розкажи поясни напиши підкажи".Split(' ').ToHashSet();
    private static readonly Dictionary<string, string> Forms = BuildForms();
    private static Dictionary<string, string> BuildForms()
    {
        var groups = new[]
        {
            "тривога тривоги тривогу тривогою тривожність тривожності тривожний тривожно хвилювання хвилююся хвилююсь неспокій непокоюся непокоюсь переживаю",
            "страх страху страхом страхи боюся боюсь страшно лячно наляканий",
            "самотність самотності самотній самотня самотньо самотнім одиноко",
            "стосунки стосунків стосунках стосунками взаємини взаємин пара парі партнер партнером партнера партнерка партнеркою партнери",
            "межі меж межами кордони кордонів кордонами відмовити відмовляти відмовляю незручно догоджати",
            "самооцінка самооцінки самооцінку самоцінність невпевненість невпевнений невпевнена нікчемний нікчемна недооцінюю",
            "провина провини провину винний винна винуватий сором сорому соромно соромлюся соромлюсь",
            "злість злості злістю злюся злюсь гнів гніву роздратування роздратований роздратована дратує",
            "конфлікт конфлікти конфліктів сварка сварки сваримося сваримось сваритися сваритись посварилися посварились",
            "втома втоми втомлений втомлена виснаження виснажений виснажена вигорання вигорів вигоріла",
            "відпочинок відпочинку відпочити відпочивати перепочинок перепочити",
            "довіра довіри довіру довіряти довіряю недовіра недовіри недовіряю",
            "ревнощі ревнощів ревную ревнувати ревнивий ревнива",
            "розставання розставань розійшлися розійшлись розрив розриву розлучення розлучилися розлучились",
            "спілкування спілкуватися спілкуватись розмова розмови розмову поговорити говорити висловити слухати вислухати почути",
            "почуття почуттів почуттями емоції емоцій емоціями переживання переживань",
            "помилка помилки помилок помилятися помиляюсь помиляюся невдача невдачі невдач провал провалу",
            "контроль контролю контролювати контролює керувати керує",
            "підтримка підтримки підтримку підтримати підтримує допомога допомоги допомогу",
            "рішення рішень вирішити вибір вибору вибрати обрати",
            "робота роботи роботу роботі працювати працюю кар'єра кар'єри",
            "прокрастинація прокрастинації відкладаю відкладати зволікання зволікаю",
            "перфекціонізм перфекціонізму ідеально ідеальний ідеальна досконалість досконалості"
        };
        var forms = new Dictionary<string, string>();
        foreach (var group in groups) { var words = group.Split(' '); foreach (var word in words) forms[word] = words[0]; }
        return forms;
    }
    public static string[] Terms(string input)
    {
        var normal = input.ToLowerInvariant().Replace('’', '\'').Replace('ʼ', '\'').Replace('`', '\'');
        return Regex.Matches(normal, @"[\p{L}]+(?:'[\p{L}]+)?", RegexOptions.None, TimeSpan.FromSeconds(1))
            .Select(m => m.Value).Where(w => w.Length >= 3 && !Stop.Contains(w)).Select(w => Forms.GetValueOrDefault(w, w)).ToArray();
    }
}

public sealed class KnowledgeRetriever(BotDb db) : IKnowledgeRetriever
{
    public async Task<IReadOnlyList<KnowledgeHit>> Search(string query, CancellationToken ct)
    {
        var terms = Lexicon.SearchTerms(query);
        var topics = terms.Where(Lexicon.IsSupportTopic).ToArray();
        if (terms.Length == 0) return [];
        var candidates = await db.Chunks.AsNoTracking().Where(x => x.Text != "" && x.PageStart > 0 && x.PageEnd >= x.PageStart &&
                x.Terms.Any(t => terms.Contains(t)) && db.Sources.Any(s => s.Id == x.SourceId && s.Active))
            .OrderByDescending(x => x.Terms.Count(t => terms.Contains(t))).ThenBy(x => x.Id).Take(256)
            .Join(db.Sources, x => x.SourceId, s => s.Id, (x, s) => new { Chunk = x, s.Title }).ToListAsync(ct);
        if (candidates.Count == 0) return [];
        var average = candidates.Average(x => x.Chunk.Terms.Length);
        var frequencies = terms.ToDictionary(t => t, t => candidates.Count(x => x.Chunk.Terms.Contains(t)));
        // A relevant passage need not contain the user's request verb. BM25
        // scores depend on this candidate set: a frequent topic can have a very
        // small IDF, so an absolute score threshold incorrectly drops all hits.
        var ranked = candidates.Select(x =>
        {
            double score = 0; var matches = 0;
            foreach (var term in terms)
            {
                var tf = x.Chunk.Terms.Count(t => t == term); if (tf == 0) continue; matches++;
                var idf = Math.Log(1 + (candidates.Count - frequencies[term] + 0.5) / (frequencies[term] + 0.5));
                score += idf * tf * 2.2 / (tf + 1.2 * (0.25 + 0.75 * x.Chunk.Terms.Length / average));
            }
            return new { Hit = new KnowledgeHit(x.Chunk.Id, x.Title, x.Chunk.PageStart, x.Chunk.PageEnd, x.Chunk.Text, score), matches, x.Chunk.Terms };
        }).Where(x => topics.Length > 0 ? x.Terms.Any(topics.Contains) : x.matches >= Math.Min(2, terms.Length))
            .OrderByDescending(x => x.Hit.Score);
        var chosen = new List<KnowledgeHit>(); var fingerprints = new List<HashSet<string>>();
        foreach (var item in ranked)
        {
            var set = item.Terms.ToHashSet();
            if (fingerprints.Any(s => (double)s.Intersect(set).Count() / Math.Max(1, s.Union(set).Count()) > 0.72)) continue;
            chosen.Add(item.Hit); fingerprints.Add(set); if (chosen.Count == 8) break;
        }
        return chosen;
    }

    public async Task<IReadOnlyList<KnowledgeHit>> ReadPassages(IReadOnlyList<long> chunkIds, CancellationToken ct)
    {
        var ids = chunkIds.Where(x => x > 0).Distinct().Take(2).ToArray();
        if (ids.Length == 0) return [];
        var anchors = await db.Chunks.AsNoTracking().Where(x => ids.Contains(x.Id) &&
                x.Text != "" && x.PageStart > 0 && x.PageEnd >= x.PageStart)
            .Join(db.Sources.Where(s => s.Active), x => x.SourceId, s => s.Id,
                (x, s) => new { Chunk = x, s.Title }).ToListAsync(ct);
        var passages = new List<KnowledgeHit>();
        var covered = new HashSet<long>();
        foreach (var id in ids)
        {
            var anchor = anchors.FirstOrDefault(x => x.Chunk.Id == id);
            if (anchor is null || covered.Contains(id)) continue;
            var ordinal = anchor.Chunk.Ordinal;
            var neighbors = await db.Chunks.AsNoTracking().Where(x => x.SourceId == anchor.Chunk.SourceId &&
                    x.Ordinal >= ordinal - 1 && x.Ordinal <= ordinal + 1 &&
                    x.Text != "" && x.PageStart > 0 && x.PageEnd >= x.PageStart)
                .OrderBy(x => x.Ordinal).ToListAsync(ct);
            if (neighbors.Count == 0) continue;
            var text = neighbors[0].Text;
            foreach (var neighbor in neighbors.Skip(1)) text = JoinOverlapping(text, neighbor.Text);
            passages.Add(new(id, anchor.Title, neighbors.Min(x => x.PageStart), neighbors.Max(x => x.PageEnd), text, 0));
            covered.UnionWith(neighbors.Select(x => x.Id));
        }
        return passages;
    }

    private static string JoinOverlapping(string left, string right)
    {
        for (var size = Math.Min(600, Math.Min(left.Length, right.Length)); size >= 20; size--)
            if (left.AsSpan(left.Length - size).SequenceEqual(right.AsSpan(0, size)))
                return left + right[size..];
        return left + "\n\n" + right;
    }
}
