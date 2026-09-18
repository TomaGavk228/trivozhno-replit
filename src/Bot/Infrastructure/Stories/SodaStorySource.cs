using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Trivozhno.Infrastructure.Stories;

public sealed record StoryMaterial(string Source, string SourceId, string Narrative, string Dialogue);

public interface IStorySource
{
    Task<IReadOnlyList<StoryMaterial>> Find(string query, long seed, CancellationToken ct);
}

public sealed class SodaStorySource(HttpClient http, ILogger<SodaStorySource> log) : IStorySource
{
    private const int TrainRows = 1_191_582;
    private const int PageSize = 20;

    // Story material is fetched from allenai/soda (CC-BY-4.0) through the public
    // Hugging Face Dataset Viewer API. We keep source row ids for attribution.
    public async Task<IReadOnlyList<StoryMaterial>> Find(string query, long seed, CancellationToken ct)
    {
        var offset = Offset(query, seed);
        var url =
            "https://datasets-server.huggingface.co/rows" +
            "?dataset=allenai%2Fsoda&config=default&split=train" +
            $"&offset={offset}&length={PageSize}";

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
                        .ToArray();
                    dialogue = string.Join("\n", lines!);
                }

                if (Unsafe(dialogue)) continue;
                candidates.Add(new(
                    "allenai/soda (CC-BY-4.0)",
                    "train:" + rowId,
                    narrative,
                    dialogue));
                if (candidates.Count == 5) break;
            }

            return candidates;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("SODA story source unavailable: {Category}", e.GetType().Name);
            return [];
        }
    }

    private static int Offset(string query, long seed)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(query + "|" + seed));
        var value = BitConverter.ToUInt32(bytes, 0);
        return (int)(value % (TrainRows - PageSize));
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
