using System.Text.Json;
using Serilog;

namespace Mod2Xgm.Output;

/// <summary>
/// How fast each phase ran on earlier runs, kept in <see cref="FileName"/> at the root of the candidate cache folder
/// (docs/cli-output.md#time-estimate): per phase, a moving average of seconds per unit of song size (<see cref="SongSize"/>). Outside the per-build
/// folders, so it survives new builds; <c>--no-cache</c> neither reads nor writes it. A file that can't be read or
/// written is ignored with a <c>Debug</c> line, never a failure. Read with <see cref="JsonDocument"/> and written with
/// <see cref="Utf8JsonWriter"/>: no reflection.
/// </summary>
internal sealed class TimingHistory
{
    /// <summary>The file's name in the cache folder.</summary>
    public const string FileName = "timings.json";

    /// <summary>Bumped when the file's layout changes; a file of another version is ignored.</summary>
    private const int FormatVersion = 1;

    /// <summary>Weight of the newest run in a phase's average.</summary>
    private const double Weight = 0.5;

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly SortedDictionary<string, double> _secondsPerUnit = new(StringComparer.Ordinal);
    private bool _changed;

    private TimingHistory(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
    }

    /// <summary>The history in <paramref name="cacheRoot"/>, empty when there is none yet or it can't be read.</summary>
    public static TimingHistory Load(string cacheRoot, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheRoot);
        ArgumentNullException.ThrowIfNull(logger);
        TimingHistory history = new(Path.Combine(Path.GetFullPath(cacheRoot), FileName), logger);
        try
        {
            if (File.Exists(history._path))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(history._path));
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("v", out JsonElement version) && version.TryGetInt32(out int v) && v == FormatVersion
                    && root.TryGetProperty("seconds_per_unit", out JsonElement phases) && phases.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty phase in phases.EnumerateObject())
                    {
                        if (phase.Value.ValueKind == JsonValueKind.Number && phase.Value.TryGetDouble(out double rate) && double.IsFinite(rate) && rate >= 0)
                        {
                            history._secondsPerUnit[phase.Name] = rate;
                        }
                    }
                }
                else
                {
                    logger.Debug("Timing history {Path:l} has another layout; starting a new one", history._path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.Debug("Timing history {Path:l} can't be read ({Reason:l}); starting a new one", history._path, ex.Message);
            history._secondsPerUnit.Clear();
        }

        return history;
    }

    /// <summary>How long phase <paramref name="id"/> is expected to take for a song of <paramref name="size"/> units; <c>null</c> when unknown.</summary>
    public TimeSpan? Expected(string id, long size) =>
        size > 0 && _secondsPerUnit.TryGetValue(id, out double rate) ? TimeSpan.FromSeconds(rate * size) : null;

    /// <summary>Phase <paramref name="id"/> took <paramref name="elapsed"/> on a song of <paramref name="size"/> units.</summary>
    public void Record(string id, TimeSpan elapsed, long size)
    {
        if (size <= 0)
        {
            return;
        }

        double rate = elapsed.TotalSeconds / size;
        _secondsPerUnit[id] = _secondsPerUnit.TryGetValue(id, out double old) ? old + (Weight * (rate - old)) : rate;
        _changed = true;
    }

    /// <summary>Writes the history if a run added to it: to a temporary file first, then renamed over the old one.</summary>
    public void Save()
    {
        if (!_changed)
        {
            return;
        }

        string temporary = _path + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!); // a full path always has a folder
            using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write))
            using (Utf8JsonWriter json = new(stream, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteNumber("v", FormatVersion);
                json.WriteStartObject("seconds_per_unit");
                foreach ((string id, double rate) in _secondsPerUnit)
                {
                    json.WriteNumber(id, rate);
                }

                json.WriteEndObject();
                json.WriteEndObject();
            }

            File.Move(temporary, _path, overwrite: true);
            _changed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Debug("Timing history {Path:l} can't be written ({Reason:l})", _path, ex.Message);
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more to do: the next save writes a new temporary file.
        }
    }
}
