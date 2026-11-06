using Mod2Xgm.Analysis;
using Mod2Xgm.Tests.Mod;
using Mod2Xgm.Tests.Optimizer;
using Serilog.Events;

namespace Mod2Xgm.Tests.Analysis;

/// <summary>Tests for <see cref="LockedPairs"/>: which tracks mostly start together at one pitch gap (plan T8.5b, ADR 0076).</summary>
public sealed class LockedPairsTests
{
    private static readonly HashSet<int> All = [0, 1, 2];

    /// <summary>A bass and an octave-up lead that always start together are one pair, the lower channel first.</summary>
    [Fact]
    public void Find_WithTracksAlwaysStartingTogether_ReturnsStrictPair()
    {
        // Arrange
        SongNote[] notes = [.. Track(1, 1, 8, rateHz: 16574), .. Track(0, 0, 8)];

        // Act
        LockedPair pair = Assert.Single(LockedPairs.Find(notes, All));

        // Assert
        Assert.Equal((0, 0, 1, 1), (pair.SampleA, pair.ChannelA, pair.SampleB, pair.ChannelB));
        Assert.Equal(8, pair.Onsets.Count);
        Assert.Equal(1200, pair.GapCents, 1);
        Assert.Equal(0, pair.SpreadCents, 1);
        Assert.All(pair.Onsets, o => Assert.Equal(o.A.FirstTick, o.B.FirstTick));
        Assert.True(pair.Strict);
    }

    /// <summary>
    /// A pair may play alone, as long as half of each track's notes start with the other: like k0w-rsnd, a lead whose every
    /// note comes with a bass that also plays six notes alone, or like hit, a doubling that drops out now and then.
    /// </summary>
    [Fact]
    public void Find_WithTracksMostlyStartingTogether_ReturnsLoosePair()
    {
        // Arrange
        SongNote[] notes = [.. Track(0, 0, 8), .. Track(1, 1, 8), .. Track(1, 1, 6, first: 4)];

        // Act
        LockedPair pair = Assert.Single(LockedPairs.Find(notes, All));

        // Assert
        Assert.Equal((8, 0, 6), (pair.Onsets.Count, pair.LoneA, pair.LoneB));
        Assert.False(pair.Strict);
    }

    /// <summary>Exactly half of each track counts.</summary>
    [Fact]
    public void Find_WithExactlyHalfOfTrackTogether_ReturnsPair()
    {
        // Arrange
        SongNote[] notes = [.. Track(0, 0, 8), .. Track(1, 1, 8), .. Track(1, 1, 8, first: 4)];

        // Act
        IReadOnlyList<LockedPair> pairs = LockedPairs.Find(notes, All);

        // Assert
        Assert.Single(pairs);
    }

    /// <summary>A rare sample's every note starts with a busy track that mostly plays alone: no pair.</summary>
    [Fact]
    public void Find_WithLessThanHalfOfTrackTogether_ReturnsNothing()
    {
        // Arrange
        SongNote[] notes = [.. Track(0, 0, 8), .. Track(1, 1, 8), .. Track(1, 1, 9, first: 4)];

        // Act
        IReadOnlyList<LockedPair> pairs = LockedPairs.Find(notes, All);

        // Assert
        Assert.Empty(pairs);
    }

    /// <summary>Tracks that never start on the same tick don't pair.</summary>
    [Fact]
    public void Find_WithTracksNeverTogether_ReturnsNothing()
    {
        // Arrange
        SongNote[] notes = [.. Track(0, 0, 8), .. Track(1, 1, 8, first: 1)];

        // Act
        IReadOnlyList<LockedPair> pairs = LockedPairs.Find(notes, All);

        // Assert
        Assert.Empty(pairs);
    }

    /// <summary>Only the onsets at the pair's own gap count (here ten times an octave apart, twice a fifth): starting together at another interval is a different note.</summary>
    [Fact]
    public void Find_WithOnsetsAtOtherGap_CountsOnlyPairsOwnGap()
    {
        // Arrange
        SongNote[] notes = [.. Track(0, 0, 12), .. Track(1, 1, 10, rateHz: 16574), .. Track(1, 1, 2, first: 80, rateHz: 12415)];

        // Act
        LockedPair pair = Assert.Single(LockedPairs.Find(notes, All));

        // Assert
        Assert.Equal(10, pair.Onsets.Count);
        Assert.Equal(1200, pair.GapCents, 1);
        Assert.Equal((2, 2), (pair.LoneA, pair.LoneB));
    }

    /// <summary>The gap may wander by finetune, up to 10 cents.</summary>
    [Fact]
    public void Find_WithGapWanderingByFinetune_ReturnsPair()
    {
        // Arrange
        double rate = 8287 * Math.Pow(2, 9 / 1200.0);
        SongNote[] notes = [.. Track(0, 0, 8), .. Track(1, 1, 4), .. Track(1, 1, 4, first: 32, rateHz: rate)];

        // Act
        LockedPair pair = Assert.Single(LockedPairs.Find(notes, All));

        // Assert
        Assert.Equal(9, pair.SpreadCents, 3);
        Assert.Equal(4.5, pair.GapCents, 3);
    }

    /// <summary>11 cents apart makes two gaps of four onsets each, never eight at one.</summary>
    [Fact]
    public void Find_WithGapWanderingMoreThanFinetune_ReturnsNothing()
    {
        // Arrange
        double rate = 8287 * Math.Pow(2, 11 / 1200.0);
        SongNote[] notes = [.. Track(0, 0, 8), .. Track(1, 1, 4), .. Track(1, 1, 4, first: 32, rateHz: rate)];

        // Act
        IReadOnlyList<LockedPair> pairs = LockedPairs.Find(notes, All);

        // Assert
        Assert.Empty(pairs);
    }

    /// <summary>A pair met seven times proves nothing.</summary>
    [Fact]
    public void Find_WithSevenOnsets_ReturnsNothing()
    {
        // Arrange
        SongNote[] notes = [.. Track(0, 0, 7), .. Track(1, 1, 7)];

        // Act
        IReadOnlyList<LockedPair> pairs = LockedPairs.Find(notes, All);

        // Assert
        Assert.Empty(pairs);
    }

    /// <summary>One sample doubled on two channels is a pair.</summary>
    [Fact]
    public void Find_WithOneSampleOnTwoChannels_ReturnsPair()
    {
        // Arrange
        SongNote[] notes = [.. Track(0, 2, 8), .. Track(2, 2, 8, rateHz: 16574)];

        // Act
        LockedPair pair = Assert.Single(LockedPairs.Find(notes, All));

        // Assert
        Assert.Equal((2, 0, 2, 2), (pair.SampleA, pair.ChannelA, pair.SampleB, pair.ChannelB));
    }

    /// <summary>Samples left out never pair.</summary>
    [Fact]
    public void Find_WithIneligibleSample_ReturnsNothing()
    {
        // Arrange
        SongNote[] notes = [.. Track(0, 0, 8), .. Track(1, 1, 8)];

        // Act
        IReadOnlyList<LockedPair> pairs = LockedPairs.Find(notes, new HashSet<int> { 0 });

        // Assert
        Assert.Empty(pairs);
    }

    /// <summary>Three tracks all locked give every pair of them, ordered by channel.</summary>
    [Fact]
    public void Find_WithThreeLockedTracks_ReturnsEveryPair()
    {
        // Arrange
        SongNote[] notes = [.. Track(0, 0, 8), .. Track(1, 1, 8, rateHz: 12430), .. Track(2, 2, 8, rateHz: 16574)];

        // Act
        IReadOnlyList<LockedPair> pairs = LockedPairs.Find(notes, All);

        // Assert
        Assert.Equal([(0, 1), (0, 2), (1, 2)], pairs.Select(p => (p.ChannelA, p.ChannelB)));
        Assert.All(pairs, p => Assert.Equal(8, p.Onsets.Count));
    }

    /// <summary>In a real song the pair is found from the tick player's notes.</summary>
    [Fact]
    public void Find_WithSong_FindsPairFromPlayedNotes()
    {
        // Arrange
        SongAnalysis song = OctaveSong();

        // Act
        LockedPair pair = Assert.Single(LockedPairs.Find(song));

        // Assert
        Assert.Equal(1200, pair.GapCents, 0);
    }

    /// <summary>A sample that must stay off FM leaves no pair.</summary>
    [Fact]
    public void Find_WithSongSampleOffFm_ReturnsNothing()
    {
        // Arrange
        SongAnalysis song = OctaveSong();

        // Act
        IReadOnlyList<LockedPair> pairs = LockedPairs.Find(song, new HashSet<int> { 1 });

        // Assert
        Assert.Empty(pairs);
    }

    /// <summary>The report logs each pair and how many there are.</summary>
    [Fact]
    public void Log_WithPair_LogsItAndTheCount()
    {
        // Arrange
        LockedPair pair = Assert.Single(LockedPairs.Find(OctaveSong()));
        CollectingSink sink = new();

        // Act
        LockedPairReport.Log(sink.CreateLogger(), [pair]);

        // Assert
        Assert.Equal(
            ["Locked pair: sample 1 on channel 1 and sample 2 on channel 2 start together 8 times, 12 semitones apart (0 and 0 notes alone)", "1 locked pairs (candidates for CH3 special mode on DMF and XGM2)"],
            sink.Messages(LogEventLevel.Debug));
    }

    /// <summary>A note of <paramref name="sample"/> on <paramref name="channel"/> at <paramref name="tick"/>, played at <paramref name="rateHz"/>.</summary>
    private static SongNote Note(int channel, int sample, int tick, double rateHz = 8287) =>
        new(channel, sample, tick, 4, rateHz, 0, 1, 0.08, false);

    /// <summary>A track of <paramref name="count"/> notes, one every 8 ticks from <paramref name="first"/>.</summary>
    private static IEnumerable<SongNote> Track(int channel, int sample, int count, int first = 0, double rateHz = 8287) =>
        Enumerable.Range(0, count).Select(k => Note(channel, sample, first + (8 * k), rateHz));

    /// <summary>A song whose saw and square start together on eight rows, an octave apart.</summary>
    private static SongAnalysis OctaveSong()
    {
        ModBuilder mod = new ModBuilder()
            .WithSample(1, "saw", 64, loopStart: 0, loopLength: 64)
            .WithSample(2, "square", 64, data: i => (sbyte)(i < 32 ? 80 : -80), loopStart: 0, loopLength: 64);
        for (int row = 0; row < 64; row += 8)
        {
            mod.WithCell(0, row: row, channel: 0, sample: 1, period: 428).WithCell(0, row: row, channel: 1, sample: 2, period: 214);
        }

        return PcmSongs.Analyze(mod.Build());
    }
}
