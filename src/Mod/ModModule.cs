namespace Mod2Xgm.Mod;

/// <summary>Which MOD layout the file uses (docs/formats/mod.md#variant-detection).</summary>
internal enum ModFormat
{
    /// <summary>31 samples, 4-byte tag at offset 1080 (M.K., M!K!, FLT4, 4CHN).</summary>
    ProTracker31,

    /// <summary>Original Soundtracker: 15 samples, no tag.</summary>
    Soundtracker15,
}

/// <summary>
/// A faithful, uninterpreted image of a MOD file: what the bytes say, nothing more. Musical interpretation (pitch,
/// effects, timing) happens in the song model (plan M3).
/// </summary>
internal sealed record ModModule
{
    public required string Title { get; init; }
    public required ModFormat Format { get; init; }

    /// <summary>The 4-character tag, or empty for 15-sample files.</summary>
    public required string Tag { get; init; }

    public required int ChannelCount { get; init; }

    /// <summary>Every sample slot (31 or 15), including empty ones, numbered from 1.</summary>
    public required IReadOnlyList<ModSample> Samples { get; init; }

    /// <summary>Number of orders actually played (1–128).</summary>
    public required int SongLength { get; init; }

    /// <summary>Restart position byte as stored (often 127 = no restart).</summary>
    public required int RestartPosition { get; init; }

    /// <summary>All 128 order-table entries as stored; only the first <see cref="SongLength"/> are played.</summary>
    public required IReadOnlyList<int> OrderTable { get; init; }

    public required IReadOnlyList<ModPattern> Patterns { get; init; }

    /// <summary>File size in bytes.</summary>
    public required long FileSize { get; init; }

    /// <summary>Whose rules the file was read by (<c>--player</c>, or the default for its format); later stages follow the same rules.</summary>
    public required ModPlayer Player { get; init; }

    /// <summary>The played orders: the first <see cref="SongLength"/> entries of <see cref="OrderTable"/>.</summary>
    public IEnumerable<int> Orders => OrderTable.Take(SongLength);
}

/// <summary>One sample slot. Lengths and loop points are in bytes (the file stores words).</summary>
internal sealed record ModSample
{
    /// <summary>Slot number, 1-based, as used in pattern cells.</summary>
    public required int Number { get; init; }

    /// <summary>Name as stored (Latin-1), trailing NULs and spaces removed; leading spaces kept.</summary>
    public required string Name { get; init; }

    /// <summary>Signed finetune, −8 to +7 (eighths of a semitone).</summary>
    public required int FineTune { get; init; }

    /// <summary>Default volume as stored (0–64 in valid files).</summary>
    public required int Volume { get; init; }

    public required int LoopStart { get; init; }
    public required int LoopLength { get; init; }

    /// <summary>8-bit signed PCM. Its length is the sample length (possibly shortened if the file was truncated).</summary>
    public required sbyte[] Data { get; init; }

    public int Length => Data.Length;

    /// <summary>True when the sample loops (loop length longer than 2 bytes).</summary>
    public bool Loops => LoopLength > 2;

    public bool IsEmpty => Data.Length == 0;
}

/// <summary>64 rows × <see cref="ChannelCount"/> cells.</summary>
internal sealed class ModPattern(int channelCount, ModCell[] cells)
{
    public const int Rows = 64;

    public int ChannelCount { get; } = channelCount;

    public ModCell this[int row, int channel] => cells[(row * ChannelCount) + channel];
}

/// <summary>One pattern cell. 0 means "none" for sample and period.</summary>
internal readonly record struct ModCell(int Sample, int Period, int Effect, int EffectParameter);
