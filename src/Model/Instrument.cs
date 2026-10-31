namespace Mod2Xgm.Model;

/// <summary>An instrument: which sample it plays and its defaults (MOD: one instrument per sample slot).</summary>
/// <param name="Name">Name as stored (may be empty, may carry credits).</param>
/// <param name="Sample">Index (0-based) into <see cref="Song.Samples"/>.</param>
/// <param name="DefaultVolume">Volume a new note starts at, 0–1 (MOD: volume / 64).</param>
/// <param name="FineTuneCents">Tuning offset of every note, in cents (MOD: finetune × 12.5). Cells keep their notes as
/// written; the tick player tunes them, and the mapper takes its pitch (docs/adr/0061-dmf-pitch-from-tick-player.md).</param>
internal sealed record Instrument(string Name, int Sample, double DefaultVolume, double FineTuneCents);

/// <summary>
/// One mono sample. The sound is stored as decimals from −1 to +1 (David, 2026-10-27): 8-bit input converts exactly
/// (value / 128), and resampling and scoring work on it directly.
/// </summary>
internal sealed class Sample
{
    /// <summary>Creates a sample and checks that the loop lies inside the data.</summary>
    /// <exception cref="ArgumentException">The loop starts or ends outside <paramref name="data"/>.</exception>
    public Sample(string name, float[] data, SampleLoop? loop)
    {
        if (loop is { } l && (l.Start < 0 || l.Length <= 0 || l.Start + l.Length > data.Length))
        {
            throw new ArgumentException($"Sample \"{name}\": loop {l.Start}+{l.Length} is outside its {data.Length} frames.", nameof(loop));
        }

        Name = name;
        Data = data;
        Loop = loop;
    }

    /// <summary>Name as stored (may be empty, may carry credits).</summary>
    public string Name { get; }

    /// <summary>Sample frames, −1 to +1.</summary>
    public float[] Data { get; }

    /// <summary>The forward loop, or <c>null</c> when the sample plays once.</summary>
    public SampleLoop? Loop { get; }

    /// <summary>Number of frames.</summary>
    public int Length => Data.Length;

    /// <summary>True when the slot holds no sound.</summary>
    public bool IsEmpty => Data.Length == 0;
}

/// <summary>A forward loop, in frames.</summary>
/// <param name="Start">First frame of the loop.</param>
/// <param name="Length">Frames in the loop (at least 1).</param>
internal readonly record struct SampleLoop(int Start, int Length);
