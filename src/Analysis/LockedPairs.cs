using Serilog;

namespace Mod2Xgm.Analysis;

/// <summary>One time a locked pair plays: a note of each track, both starting on the same tick.</summary>
/// <param name="A">The note of <see cref="LockedPair"/>'s first track.</param>
/// <param name="B">The note of its second track.</param>
internal sealed record LockedOnset(SongNote A, SongNote B)
{
    /// <summary>How far <see cref="B"/> plays above <see cref="A"/>, in cents (below when negative), by their playback rates.</summary>
    public double GapCents => 1200 * Math.Log2(B.RateHz / A.RateHz);
}

/// <summary>
/// Two song tracks (a sample on a channel) that mostly start together at the same pitch gap (plan T8.5b,
/// docs/adr/0076-loose-locked-pairs.md): at least <see cref="LockedPairs.MinShare"/> of each track's notes start on the
/// same tick as a note of the other, at most <see cref="LockedPairs.MaxSpreadCents"/> apart in gap, at least
/// <see cref="LockedPairs.MinOccurrences"/> times. Such a pair can share one FM channel in CH3 special mode, as two
/// 2-operator voices; a note either track plays alone is keyed on its own half.
/// </summary>
/// <param name="SampleA">The first track's sample (index into <c>Song.Samples</c>).</param>
/// <param name="ChannelA">The first track's channel: the lower of the two.</param>
/// <param name="SampleB">The second track's sample; the same as <paramref name="SampleA"/> for one sample doubled on two channels.</param>
/// <param name="ChannelB">The second track's channel.</param>
/// <param name="Onsets">Every time they start together at the pair's gap, in song order.</param>
/// <param name="NotesA">How many notes the first track plays in all: those not in <paramref name="Onsets"/> play alone.</param>
/// <param name="NotesB">How many the second track plays.</param>
internal sealed record LockedPair(int SampleA, int ChannelA, int SampleB, int ChannelB, IReadOnlyList<LockedOnset> Onsets, int NotesA, int NotesB)
{
    /// <summary>The first track's notes that play without the second (or at another gap).</summary>
    public int LoneA => NotesA - Onsets.Count;

    /// <summary>The second track's notes that play without the first.</summary>
    public int LoneB => NotesB - Onsets.Count;

    /// <summary>True when neither track ever plays alone: the strict lock of docs/adr/0073-ch3-locked-pairs.md.</summary>
    public bool Strict => LoneA == 0 && LoneB == 0;

    /// <summary>The mean gap, in cents: how far the second track plays above the first.</summary>
    public double GapCents => Onsets.Average(o => o.GapCents);

    /// <summary>How far the gaps spread, in cents: the largest minus the smallest.</summary>
    public double SpreadCents => Onsets.Max(o => o.GapCents) - Onsets.Min(o => o.GapCents);
}

/// <summary>Finds the song's locked pairs (pass 1 of docs/multi-pass.md, plan T8.5b; docs/adr/0073-ch3-locked-pairs.md, docs/adr/0076-loose-locked-pairs.md).</summary>
internal static class LockedPairs
{
    /// <summary>
    /// A pair is locked only when it plays together at least this often (David): a few chance meetings of two tracks prove
    /// nothing, and a part this short barely needs its own channel.
    /// </summary>
    public const int MinOccurrences = 8;

    /// <summary>Two onsets have the same gap when they differ by at most this much (David): finetune, not a different note.</summary>
    public const double MaxSpreadCents = 10;

    /// <summary>
    /// A pair starts together on at least this share of each track's notes (David): a doubling that drops out now and then,
    /// or a lead whose every note comes with a bass that also plays alone. Of one track only, a rare sample that happens
    /// to start with a busy one would pair with it.
    /// </summary>
    public const double MinShare = 0.5;

    /// <summary>
    /// The locked pairs of <paramref name="song"/>'s analyzed samples with sound, leaving out <paramref name="keepOut"/>
    /// (samples that can't go on FM: <c>--require-dac</c>, <c>--require-psg</c>), ordered by their channels and samples.
    /// </summary>
    public static IReadOnlyList<LockedPair> Find(SongAnalysis song, IReadOnlySet<int>? keepOut = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        HashSet<int> eligible = [.. song.Samples.Where(a => a.Kind != SampleKind.Silent && keepOut?.Contains(a.Sample) != true).Select(a => a.Sample)];
        return Find(song.Notes, eligible);
    }

    /// <summary>The locked pairs among <paramref name="notes"/> of the samples in <paramref name="eligible"/>.</summary>
    public static IReadOnlyList<LockedPair> Find(IReadOnlyList<SongNote> notes, IReadOnlySet<int> eligible)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(eligible);
        SongNote[][] tracks = [.. notes
            .Where(n => eligible.Contains(n.Sample))
            .GroupBy(n => (n.Channel, n.Sample))
            .OrderBy(g => g.Key.Channel)
            .ThenBy(g => g.Key.Sample)
            .Select(g => g.OrderBy(n => n.FirstTick).ToArray())
            .Where(t => t.Length >= MinOccurrences)];

        List<LockedPair> found = [];
        for (int i = 0; i < tracks.Length; i++)
        {
            for (int j = i + 1; j < tracks.Length; j++)
            {
                if (Locked(tracks[i], tracks[j]) is { } pair)
                {
                    found.Add(pair);
                }
            }
        }

        return found;
    }

    /// <summary>The pair of tracks <paramref name="a"/> and <paramref name="b"/> (each one sample on one channel, in tick order), or <c>null</c> when they aren't locked.</summary>
    private static LockedPair? Locked(SongNote[] a, SongNote[] b)
    {
        // One channel plays one note at a time, so two tracks on it never start together.
        if (a[0].Channel == b[0].Channel)
        {
            return null;
        }

        Dictionary<int, SongNote> bAt = b.ToDictionary(n => n.FirstTick);
        LockedOnset[] together = [.. a.Where(n => bAt.ContainsKey(n.FirstTick)).Select(n => new LockedOnset(n, bAt[n.FirstTick]))];
        LockedOnset[] onsets = AtOneGap(together);
        if (onsets.Length < MinOccurrences || Math.Min((double)onsets.Length / a.Length, (double)onsets.Length / b.Length) < MinShare)
        {
            return null;
        }

        return new LockedPair(a[0].Sample, a[0].Channel, b[0].Sample, b[0].Channel, onsets, a.Length, b.Length);
    }

    /// <summary>
    /// The most onsets whose gaps lie within <see cref="MaxSpreadCents"/> of each other, in song order: the pair's own
    /// interval, without the times the tracks happen to start together at another one. Of equal groups, the lowest gap.
    /// </summary>
    private static LockedOnset[] AtOneGap(LockedOnset[] together)
    {
        double[] gaps = [.. together.Select(o => o.GapCents).Order()];
        int best = 0;
        int bestCount = 0;
        for (int i = 0, j = 0; i < gaps.Length; i++)
        {
            while (j < gaps.Length && gaps[j] - gaps[i] <= MaxSpreadCents)
            {
                j++;
            }

            if (j - i > bestCount)
            {
                (best, bestCount) = (i, j - i);
            }
        }

        if (bestCount == 0)
        {
            return [];
        }

        double low = gaps[best];
        double high = gaps[best + bestCount - 1];
        return [.. together.Where(o => o.GapCents >= low && o.GapCents <= high)];
    }
}

/// <summary>Logs the locked pairs at <c>Debug</c> level (<c>--verbose</c>, docs/adr/0073-ch3-locked-pairs.md, docs/adr/0076-loose-locked-pairs.md).</summary>
internal static class LockedPairReport
{
    /// <summary>One line per pair, then how many there are.</summary>
    public static void Log(ILogger logger, IReadOnlyList<LockedPair> pairs)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(pairs);
        foreach (LockedPair p in pairs)
        {
            logger.Debug(
                "Locked pair: sample {SampleA} on channel {ChannelA} and sample {SampleB} on channel {ChannelB} start together {Count} times, {Gap:0.##} semitones apart ({LoneA} and {LoneB} notes alone)",
                p.SampleA + 1,
                p.ChannelA + 1,
                p.SampleB + 1,
                p.ChannelB + 1,
                p.Onsets.Count,
                p.GapCents / 100,
                p.LoneA,
                p.LoneB);
        }

        logger.Debug("{Count} locked pairs (candidates for CH3 special mode on DMF and XGM2)", pairs.Count);
    }
}
