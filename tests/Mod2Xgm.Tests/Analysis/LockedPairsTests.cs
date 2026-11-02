using Mod2Xgm.Analysis;
using Mod2Xgm.Tests.Mod;
using Mod2Xgm.Tests.Optimizer;
using Serilog.Events;

namespace Mod2Xgm.Tests.Analysis;

/// <summary>Tests for <see cref="LockedPairs"/>: which tracks always start together at one pitch gap (plan T8.5b).</summary>
public sealed class LockedPairsTests
{
    private static readonly HashSet<int> All = [0, 1, 2];

    /// <summary>A note of <paramref name="sample"/> on <paramref name="channel"/> at <paramref name="tick"/>, played at <paramref name="rateHz"/>.</summary>
    private static SongNote Note(int channel, int sample, int tick, double rateHz = 8287) =>
        new(channel, sample, tick, 4, rateHz, 0, 1, 0.08, false);

    /// <summary>A bass and an octave-up lead that always start together are one pair, the lower channel first.</summary>
    [Fact]
    public void TracksThatAlwaysStartTogetherAreLocked()
    {
        SongNote[] notes = [Note(1, 1, 0, 16574), Note(0, 0, 0), Note(0, 0, 8, 9300), Note(1, 1, 8, 18600)];

        LockedPair pair = Assert.Single(LockedPairs.Find(notes, All));

        Assert.Equal((0, 0, 1, 1), (pair.SampleA, pair.ChannelA, pair.SampleB, pair.ChannelB));
        Assert.Equal(2, pair.Onsets.Count);
        Assert.Equal(1200, pair.GapCents, 1);
        Assert.Equal(0, pair.SpreadCents, 1);
        Assert.All(pair.Onsets, o => Assert.Equal(o.A.FirstTick, o.B.FirstTick));
    }

    /// <summary>One note of either track alone breaks the lock, on either side.</summary>
    [Fact]
    public void ALoneNoteBreaksTheLock()
    {
        SongNote[] together = [Note(0, 0, 0), Note(1, 1, 0), Note(0, 0, 8), Note(1, 1, 8)];

        Assert.Empty(LockedPairs.Find([.. together, Note(1, 1, 16)], All));
        Assert.Empty(LockedPairs.Find([.. together, Note(0, 0, 16)], All));
        Assert.Empty(LockedPairs.Find([Note(0, 0, 0), Note(1, 1, 0), Note(0, 0, 8), Note(1, 1, 9)], All));
    }

    /// <summary>The gap may wander by finetune (10 cents at most), not by more.</summary>
    [Fact]
    public void TheGapMayOnlyWanderByFinetune()
    {
        double cents9 = 8287 * Math.Pow(2, 9 / 1200.0);
        double cents11 = 8287 * Math.Pow(2, 11 / 1200.0);

        LockedPair pair = Assert.Single(LockedPairs.Find([Note(0, 0, 0), Note(1, 1, 0), Note(0, 0, 8), Note(1, 1, 8, cents9)], All));
        Assert.Equal(9, pair.SpreadCents, 3);
        Assert.Equal(4.5, pair.GapCents, 3);
        Assert.Empty(LockedPairs.Find([Note(0, 0, 0), Note(1, 1, 0), Note(0, 0, 8), Note(1, 1, 8, cents11)], All));
    }

    /// <summary>A pair played once proves nothing; one sample doubled on two channels is a pair; samples left out never pair.</summary>
    [Fact]
    public void PairsNeedTwoOnsetsAndEligibleSamples()
    {
        Assert.Empty(LockedPairs.Find([Note(0, 0, 0), Note(1, 1, 0)], All));

        LockedPair doubled = Assert.Single(LockedPairs.Find([Note(0, 2, 0), Note(2, 2, 0, 16574), Note(0, 2, 8), Note(2, 2, 8, 16574)], All));
        Assert.Equal((2, 0, 2, 2), (doubled.SampleA, doubled.ChannelA, doubled.SampleB, doubled.ChannelB));

        Assert.Empty(LockedPairs.Find([Note(0, 0, 0), Note(1, 1, 0), Note(0, 0, 8), Note(1, 1, 8)], new HashSet<int> { 0 }));
    }

    /// <summary>Three tracks all locked give every pair of them, ordered by channel.</summary>
    [Fact]
    public void EveryPairOfLockedTracksIsFound()
    {
        SongNote[] notes = [.. new[] { 0, 8, 16 }.SelectMany(t => new[] { Note(0, 0, t), Note(1, 1, t, 12430), Note(2, 2, t, 16574) })];

        IReadOnlyList<LockedPair> pairs = LockedPairs.Find(notes, All);

        Assert.Equal([(0, 1), (0, 2), (1, 2)], pairs.Select(p => (p.ChannelA, p.ChannelB)));
        Assert.All(pairs, p => Assert.Equal(3, p.Onsets.Count));
    }

    /// <summary>In a real song the pair is found from the tick player's notes, unless one sample must stay off FM; the report logs it.</summary>
    [Fact]
    public void ASongsPairsAreFoundAndLogged()
    {
        ModBuilder mod = new ModBuilder()
            .WithSample(1, "saw", 64, loopStart: 0, loopLength: 64)
            .WithSample(2, "square", 64, data: i => (sbyte)(i < 32 ? 80 : -80), loopStart: 0, loopLength: 64);
        foreach (int row in (int[])[0, 8, 16])
        {
            mod.WithCell(0, row: row, channel: 0, sample: 1, period: 428).WithCell(0, row: row, channel: 1, sample: 2, period: 214);
        }

        SongAnalysis song = PcmSongs.Analyze(mod.Build());
        LockedPair pair = Assert.Single(LockedPairs.Find(song));
        CollectingSink sink = new();
        LockedPairReport.Log(sink.CreateLogger(), [pair]);

        Assert.Equal(1200, pair.GapCents, 0);
        Assert.Empty(LockedPairs.Find(song, new HashSet<int> { 1 }));
        Assert.Equal(
            ["Locked pair: sample 1 on channel 1 and sample 2 on channel 2 start together 3 times, 12 semitones apart", "1 locked pairs (candidates for CH3 special mode on DMF and XGM2)"],
            sink.Messages(LogEventLevel.Debug));
    }
}
