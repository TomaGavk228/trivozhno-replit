using System.Text.Json;

namespace Trivozhno.Infrastructure.Content;

// One validated snapshot per file, shared by requests. Invalid edits never replace it.
public sealed class ReloadingJsonFile<T>(string path, T initial, Func<T, bool> validate, ILogger log)
    where T : class
{
    private readonly object gate = new();
    private T snapshot = initial;
    private (DateTime Time, long Size)? seen;
    private bool missingReported;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public T Read()
    {
        lock (gate)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists)
                {
                    seen = null;
                    if (!missingReported) log.LogWarning("Content file missing: {File}; keeping previous snapshot", path);
                    missingReported = true;
                    return snapshot;
                }
                missingReported = false;
                var stamp = (file.LastWriteTimeUtc, file.Length);
                if (seen == stamp) return snapshot;
                seen = stamp;
                if (file.Length > 8_000_000) throw new InvalidDataException("Content file is too large");
                var next = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
                if (next is null || !validate(next)) throw new InvalidDataException("Content shape is invalid");
                snapshot = next;
                log.LogInformation("Content reloaded: {File}", path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // Do not log file contents; JSON errors may contain user-authored text.
                log.LogWarning("Content edit rejected: {File}, {Category}; keeping previous snapshot", path, e.GetType().Name);
            }
            return snapshot;
        }
    }
}
