using System.Text.Json;

namespace Trivozhno.Infrastructure.Stories;

public sealed record StoryMaterial(string Source, string SourceId, string Narrative, string Dialogue);

public interface IStorySource
{
    Task<IReadOnlyList<StoryMaterial>> Find(string query, long seed, CancellationToken ct);
}

public sealed class SodaStorySource(HttpClient http, ILogger<SodaStorySource> log) : IStorySource
{
    private const int SearchSize = 50;

    // AllenAI SODA is CC-BY-4.0. Hugging Face Dataset Viewer /search uses
    // full-text BM25 search, so Qwen supplies a short English search phrase.
    public async Task<IReadOnlyList<StoryMaterial>> Find(string query, long seed, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var url =
            "https://datasets-server.huggingface.co/search" +
            "?dataset=allenai%2Fsoda&config=default&split=train" +
            $"&query={Uri.EscapeDataString(query)}&offset=0&length={SearchSize}";

        try
        {
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("SODA story source returned HTTP {Status}", (int)response.StatusCode);
                return [];
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!json.RootElement.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
                return [];

            var candidates = new List<StoryMaterial>();
            foreach (var item in rows.EnumerateArray())
            {
                if (!item.TryGetProperty("row", out var row)) continue;
                var rowId = item.TryGetProperty("row_idx", out var id) ? id.GetInt64().ToString() : "?";
                var narrative = row.TryGetProperty("narrative", out var n) ? n.GetString()?.Trim() ?? "" : "";
                if (narrative.Length is < 80 or > 1400 || Unsafe(narrative)) continue;

                var dialogue = "";
                if (row.TryGetProperty("dialogue", out var d) && d.ValueKind == JsonValueKind.Array)
                {
                    var lines = d.EnumerateArray()
                        .Select(x => x.GetString()?.Trim())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Take(8)
                        .Select(x => x!)
                        .ToArray();
                    dialogue = string.Join("\n", lines);
                }

                if (Unsafe(dialogue)) continue;
                candidates.Add(new(
                    "allenai/soda (CC-BY-4.0)",
                    "train:" + rowId,
                    narrative,
                    dialogue));
            }

            if (candidates.Count == 0) return [];

            // Search ranking stays relevant; seed only rotates within the best safe results
            // so repeated story requests do not always return the identical first item.
            var pool = candidates.Take(15).ToArray();
            var start = (int)((ulong)Math.Abs(seed) % (ulong)pool.Length);
            return Enumerable.Range(0, Math.Min(5, pool.Length))
                .Select(i => pool[(start + i) % pool.Length])
                .ToArray();
        }
        catch (Exception e) when (!ct.IsCancellationRequested &&
                                  (e is HttpRequestException || e is JsonException || e is TaskCanceledException))
        {
            log.LogWarning("SODA story source unavailable: {Category}", e.GetType().Name);
            return [];
        }
    }

    private static bool Unsafe(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.ToLowerInvariant();
        string[] blocked =
        [
            "suicide", "self-harm", "self harm", "killed himself", "killed herself",
            "porn", "sexual", "nude", "naked", "rape", "raped",
            "cocaine", "heroin", "meth", "overdose",
            "gun", "rifle", "pistol", "knife", "stabbing", "murder", "blood"
        ];
        return blocked.Any(value.Contains);
    }
}
