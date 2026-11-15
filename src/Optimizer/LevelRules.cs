namespace Mod2Xgm.Optimizer;

/// <summary>
/// How much quieter the optimizer's plan plays than its own levels, on XGM and XGM2: a retry's fix for a mix that clips
/// (plan T9.4s2, docs/adr/0112-retry-list.md). No option sets it: the harness does, with <c>rule:quieter</c> and
/// <c>rule:song-quieter</c> (docs/harness.md#listening-answers). A DMF ignores it.
/// </summary>
/// <param name="SongDb">How many dB quieter everything plays: FM, PSG and every recording, pre-mixes included; 0 or more.</param>
/// <param name="RecordingDb">
/// How many dB quieter each sample (0-based) plays as a recording, on top of <paramref name="SongDb"/>; 0 or more. A
/// pre-mix keeps the levels its notes were mixed at.
/// </param>
internal sealed record LevelRules(double SongDb, IReadOnlyDictionary<int, double> RecordingDb)
{
    /// <summary>The plan's own levels: what the converter plays.</summary>
    public static LevelRules None { get; } = new(0, new Dictionary<int, double>());

    /// <summary>The gain of a recording of <paramref name="sample"/> (0-based), in dB: 0 or less, without the song's.</summary>
    public double RecordingGainDb(int sample) => -RecordingDb.GetValueOrDefault(sample);
}
