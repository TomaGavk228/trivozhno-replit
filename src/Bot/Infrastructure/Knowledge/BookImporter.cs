using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Trivozhno.Infrastructure.Knowledge;

public sealed record ImportReport(string File, string Hash, int Pages, int Chunks, int[] EmptyPages, string Status);
public sealed record TextPage(int Number, string Text);

public sealed class BookImporter(BotDb db, IClock clock, BotOptions options)
{
    public async Task<ImportReport> Import(string file, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        if (await db.Sources.AnyAsync(x => x.Hash == hash, ct)) return new(Path.GetFileName(file), hash, 0, 0, [], "duplicate-skipped");
        using var pdf = PdfDocument.Open(file);
        var pages = pdf.GetPages().Select(p => new TextPage(p.Number, ContentOrderTextExtractor.GetText(p))).ToArray();
        var empty = pages.Where(p => string.IsNullOrWhiteSpace(p.Text)).Select(p => p.Number).ToArray();
        if (empty.Length == pages.Length) throw new InvalidOperationException("PDF has no extractable text; OCR is not included.");
        var edges = pages.SelectMany(p => p.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(2)
            .Concat(p.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(2)).Select(s => s.Trim()).Distinct())
            .Where(s => s.Length is > 0 and < 150).GroupBy(s => s).Where(g => g.Count() >= Math.Max(3, pages.Length / 2)).Select(g => g.Key).ToHashSet();
        var cleaned = pages.Select(p => new TextPage(p.Number, Clean(p.Text, edges))).ToArray();
        var fragments = Chunk(cleaned, options.ChunkSize, options.ChunkOverlap);
        if (fragments.Count == 0) throw new InvalidOperationException("No usable text fragments.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var title = Path.GetFileNameWithoutExtension(file).Replace('_', ' ');
        var source = new KnowledgeSource { Title = title, Hash = hash, ImportedAt = clock.UtcNow };
        db.Sources.Add(source); await db.SaveChangesAsync(ct);
        foreach (var chunk in fragments) { chunk.SourceId = source.Id; db.Chunks.Add(chunk); }
        await db.SaveChangesAsync(ct);
        await db.Sources.Where(s => s.Title == title && s.Id != source.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Active, false), ct);
        await tx.CommitAsync(ct);
        return new(Path.GetFileName(file), hash, pages.Length, fragments.Count, empty, empty.Length == 0 ? "complete" : "partial-no-ocr");
    }
    public static string Clean(string text, HashSet<string> edges)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        text = string.Join('\n', lines.Where((line, i) => !(i < 2 || i >= lines.Length - 2) || !edges.Contains(line.Trim())));
        text = Regex.Replace(text, @"(?<=\p{L})-\s*\n\s*(?=\p{L})", "");
        text = Regex.Replace(text, @"(?<!\n)\n(?!\n)", " ");
        text = Regex.Replace(text, @"[ \t]+", " ");
        return text.Trim();
    }
    public static List<KnowledgeChunk> Chunk(IEnumerable<TextPage> pages, int target, int overlap)
    {
        if (target < 20 || overlap < 0 || overlap >= target / 2) throw new ArgumentOutOfRangeException(nameof(overlap));
        var result = new List<KnowledgeChunk>(); var buffer = "";
        var spans = new List<(int Start, int End, int Page)>();
        foreach (var page in pages)
        {
            if (string.IsNullOrWhiteSpace(page.Text)) continue;
            foreach (var paragraph in page.Text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                if (buffer.Length > 0) buffer += "\n\n";
                spans.Add((buffer.Length, buffer.Length + paragraph.Length, page.Number));
                buffer += paragraph;
                while (buffer.Length > target)
                {
                    var cut = buffer.LastIndexOf("\n\n", target, StringComparison.Ordinal);
                    if (cut < target * 2 / 3) cut = buffer.LastIndexOf(' ', target);
                    if (cut < target / 2) cut = target;
                    if (char.IsHighSurrogate(buffer[cut - 1])) cut--;
                    Add(cut);
                    var next = Math.Max(0, cut - overlap);
                    if (next > 0 && char.IsLowSurrogate(buffer[next])) next--;
                    buffer = buffer[next..];
                    spans = spans.Where(s => s.End > next).Select(s => (Math.Max(0, s.Start - next), s.End - next, s.Page)).ToList();
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(buffer)) Add(buffer.Length);
        return result;
        void Add(int length)
        {
            var included = spans.Where(s => s.Start < length && s.End > 0).ToArray();
            var text = buffer[..length];
            result.Add(new() { Ordinal = result.Count, PageStart = included[0].Page, PageEnd = included[^1].Page, Text = text, Terms = Lexicon.Terms(text) });
        }
    }
}
