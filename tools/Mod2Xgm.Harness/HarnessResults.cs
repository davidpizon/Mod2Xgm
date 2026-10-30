using Mod2Xgm.Mod;
using Mod2Xgm.Model;
using Mod2Xgm.Scoring;

namespace Mod2Xgm.Harness;

/// <summary>
/// The tick player's song timeline against the reference player's (plan T3.4: ±1 tick; ADR 0047). The tick player
/// follows ProTracker 2 on a real Amiga, which OpenMPT simplifies in two ways (<see cref="OpenMptTiming"/>), so its
/// ticks are first re-timed OpenMPT's way. Then every row must start within one tick of the time libopenmpt gives for
/// it, and the song must end within one tick of libopenmpt's length, except when it stops at <c>F00</c>, which OpenMPT
/// plays past. One tick is the song's longest, so one tick gained or lost anywhere passes.
/// </summary>
/// <param name="TickPlayerSeconds">One pass through the song by <c>ModTickPlayer</c>: a real Amiga's timing.</param>
/// <param name="OpenMptRulesSeconds">The same ticks timed OpenMPT's way.</param>
/// <param name="ReferenceSeconds">One pass by the reference player's own count.</param>
/// <param name="ToleranceSeconds">One tick.</param>
/// <param name="FirstRowOff">The first row the two players start more than a tick apart, or <c>null</c> when none does.</param>
/// <param name="StopsAtF00">True when the song ends at an <c>F00</c> (ProTracker stops; OpenMPT plays on), so the end isn't compared.</param>
internal sealed record DurationCheck(
    double TickPlayerSeconds,
    double OpenMptRulesSeconds,
    double ReferenceSeconds,
    double ToleranceSeconds,
    RowTiming? FirstRowOff,
    bool StopsAtF00)
{
    /// <summary>OpenMPT-rules length minus the reference's: what the check judges.</summary>
    public double DifferenceSeconds => OpenMptRulesSeconds - ReferenceSeconds;

    /// <summary>A real Amiga's length minus the OpenMPT-rules length: what OpenMPT's simplifications change.</summary>
    public double AmigaDifferenceSeconds => TickPlayerSeconds - OpenMptRulesSeconds;

    /// <summary>True when every row and the end are within one tick.</summary>
    public bool Within => FirstRowOff is null && (StopsAtF00 || Math.Abs(DifferenceSeconds) <= ToleranceSeconds + Epsilon);

    private const double Epsilon = 1e-9;

    /// <summary>The check for <paramref name="rows"/> (from <see cref="Rows"/>) and the reference's length.</summary>
    /// <param name="playback">The tick player's pass.</param>
    /// <param name="rows">When each row starts by both players.</param>
    /// <param name="referenceSeconds">The reference's song length.</param>
    /// <param name="stopsAtF00">The song ends at an <c>F00</c>.</param>
    public static DurationCheck Of(Playback playback, IReadOnlyList<RowTiming> rows, double referenceSeconds, bool stopsAtF00)
    {
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(rows);
        IReadOnlyList<double> openMpt = OpenMptTiming.TickDurations(playback);
        double tick = playback.Ticks.Count == 0 ? 0 : Math.Max(playback.Ticks.Max(t => t.DurationSeconds), openMpt.Max());
        RowTiming? first = rows.FirstOrDefault(r => Math.Abs(r.DifferenceSeconds) > tick + Epsilon);
        return new DurationCheck(playback.DurationSeconds, openMpt.Sum(), referenceSeconds, tick, first, stopsAtF00);
    }

    /// <summary>
    /// When each row first starts, by the tick player (a real Amiga's timing and OpenMPT's) and by the reference
    /// (<paramref name="referenceTimes"/> asks it), in playing order.
    /// </summary>
    public static IReadOnlyList<RowTiming> Rows(Playback playback, Func<IReadOnlyList<(int Order, int Row)>, IReadOnlyList<double>> referenceTimes)
    {
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(referenceTimes);
        IReadOnlyList<double> durations = OpenMptTiming.TickDurations(playback);
        List<(PlaybackTick Tick, double OpenMptSeconds)> firsts = [];
        HashSet<(int, int)> seen = [];
        double time = 0;
        for (int i = 0; i < playback.Ticks.Count; i++)
        {
            PlaybackTick tick = playback.Ticks[i];
            if (seen.Add((tick.Order, tick.Row)))
            {
                firsts.Add((tick, time));
            }

            time += durations[i];
        }

        IReadOnlyList<double> reference = referenceTimes([.. firsts.Select(f => (f.Tick.Order, f.Tick.Row))]);
        return [.. firsts.Select((f, i) => new RowTiming(f.Tick.Order, f.Tick.Row, f.Tick.TimeSeconds, f.OpenMptSeconds, reference[i]))];
    }

    /// <summary>True when <paramref name="playback"/> stopped on a row of <paramref name="module"/> that holds an <c>F00</c>.</summary>
    public static bool EndsAtF00(ModModule module, Playback playback)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(playback);
        if (playback.End != PlaybackEnd.Stops || playback.Ticks.Count == 0)
        {
            return false;
        }

        PlaybackTick last = playback.Ticks[^1];
        ModPattern pattern = module.Patterns[module.OrderTable[last.Order]];
        return Enumerable.Range(0, pattern.ChannelCount).Any(c => pattern[last.Row, c] is { Effect: 0xF, EffectParameter: 0 });
    }
}

/// <summary>When one row first starts, by the tick player and by the reference.</summary>
/// <param name="Order">Position in the order list.</param>
/// <param name="Row">Row in the pattern.</param>
/// <param name="TickPlayerSeconds">The tick player's time (a real Amiga's).</param>
/// <param name="OpenMptRulesSeconds">The tick player's time with its ticks timed OpenMPT's way.</param>
/// <param name="ReferenceSeconds">The reference player's time.</param>
internal sealed record RowTiming(int Order, int Row, double TickPlayerSeconds, double OpenMptRulesSeconds, double ReferenceSeconds)
{
    /// <summary>OpenMPT-rules time minus the reference's.</summary>
    public double DifferenceSeconds => OpenMptRulesSeconds - ReferenceSeconds;
}

/// <summary>
/// How long OpenMPT makes each of the tick player's ticks. Two differences from ProTracker 2 on an Amiga (ADR 0047):
/// <list type="number">
/// <item>A tick is a whole number of samples at libopenmpt's 48 kHz timing rate: ⌊48000 × 2.5 / BPM⌋, not the Amiga
/// CIA timer's 709 379 / (⌊1 773 447 / BPM⌋ + 1) Hz. At BPM 125 both are 20 ms; at most others OpenMPT is a little fast.</item>
/// <item>A tempo change (<c>Fxx</c>, 32 and up) times the tick that reads it. ProTracker's CIA timer only takes the new
/// value at its next interrupt, so the tick player changes one tick later (pt2-clone, ADR 0033).</item>
/// </list>
/// Ticks whose length isn't a CIA tempo (Ultimate Soundtracker's tempo byte) keep their length, rounded the same way.
/// </summary>
internal static class OpenMptTiming
{
    /// <summary>The sample rate libopenmpt counts song time in (seen in its row times: 937 samples per tick at BPM 128).</summary>
    private const int TimingRate = 48_000;

    private const int MinBpm = 32;
    private const int MaxBpm = 255;

    /// <summary>The length OpenMPT gives each tick of <paramref name="playback"/>, in order.</summary>
    public static IReadOnlyList<double> TickDurations(Playback playback)
    {
        ArgumentNullException.ThrowIfNull(playback);
        IReadOnlyList<PlaybackTick> ticks = playback.Ticks;
        int?[] bpms = [.. ticks.Select(t => Bpm(t.DurationSeconds))];
        double[] ideal = [.. ticks.Select((t, i) => bpms[i] is { } bpm ? 2.5 / bpm : t.DurationSeconds)];

        // A tempo change shows on the tick after the row's first tick; OpenMPT times that first tick with it already.
        for (int i = 0; i + 1 < ticks.Count; i++)
        {
            if (ticks[i].Tick == 0 && bpms[i + 1] is { } next && bpms[i] != next)
            {
                ideal[i] = ideal[i + 1];
            }
        }

        return [.. ideal.Select(seconds => Math.Floor((seconds * TimingRate) + 1e-6) / TimingRate)];
    }

    /// <summary>The BPM whose CIA tick lasts <paramref name="seconds"/>, or <c>null</c> when none does.</summary>
    internal static int? Bpm(double seconds)
    {
        for (int bpm = MinBpm; bpm <= MaxBpm; bpm++)
        {
            if (Math.Abs((1.0 / ModTickPlayer.CiaTickHz(bpm)) - seconds) < 1e-12)
            {
                return bpm;
            }
        }

        return null;
    }
}

/// <summary>How one rendering of the conversion scored against the reference.</summary>
/// <param name="Renderer">Which renderer made it.</param>
/// <param name="Score">The scores, or <c>null</c> when it couldn't be rendered or scored.</param>
/// <param name="Remark">Anything the reader should know (a stale export, the error).</param>
/// <param name="Clip">The scores of the song's first seconds alone (<c>--clip</c>), or <c>null</c> when not asked for.</param>
/// <param name="Balance">The clip's instrument balance (<c>--balance</c>), or <c>null</c> when not asked for or this renderer can't make stems.</param>
internal sealed record CandidateScore(string Renderer, ScoreReport? Score, string? Remark, ScoreReport? Clip = null, BalanceResult? Balance = null);

/// <summary>Which MOD sample a stem is, and what the conversion plays it with.</summary>
/// <param name="Sample">The MOD sample number (1-based, as in the MOD's cells).</param>
/// <param name="Name">The sample's name.</param>
/// <param name="Voices">What the conversion plays its notes with in the clip (<c>FM</c>, <c>DAC</c>, <c>noise</c>, joined by <c>+</c>); empty when none of its notes made it.</param>
internal sealed record StemInfo(int Sample, string Name, string Voices);

/// <summary>A clip's instrument balance against the reference (docs/adr/0051-instrument-balance-metric.md).</summary>
/// <param name="Report">The balance distance and each instrument's offset, in the order of <paramref name="Stems"/>.</param>
/// <param name="Stems">The stems compared.</param>
/// <param name="ReferenceSumErrorDb">How far the reference's stems' loudness, added up, is from its mix's, in dB (near 0 is right).</param>
/// <param name="CandidateSumErrorDb">The same for the rendering's stems.</param>
internal sealed record BalanceResult(BalanceReport Report, IReadOnlyList<StemInfo> Stems, double ReferenceSumErrorDb, double CandidateSumErrorDb);

/// <summary>Everything the harness found for one song.</summary>
/// <param name="Song">File name without extension.</param>
/// <param name="Duration">Length check, or <c>null</c> when the song failed before it.</param>
/// <param name="ConversionNotes">How many conversion notes the conversion logged.</param>
/// <param name="Scores">One entry per renderer that had something for this song.</param>
/// <param name="Error">Why the song couldn't be measured, or <c>null</c>.</param>
internal sealed record SongResult(string Song, DurationCheck? Duration, int ConversionNotes, IReadOnlyList<CandidateScore> Scores, string? Error);
