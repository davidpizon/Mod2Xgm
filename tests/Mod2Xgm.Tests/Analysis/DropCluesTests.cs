using Mod2Xgm.Analysis;
using Mod2Xgm.Tests.Mod;
using Mod2Xgm.Tests.Optimizer;
using Serilog.Events;

namespace Mod2Xgm.Tests.Analysis;

/// <summary>Tests for <see cref="DropClues"/>: which tracks are cheap to drop or merge (plan T8.7, ADR 0084).</summary>
public sealed class DropCluesTests
{
    private const int Period = 428;

    /// <summary>Amiga channels 1 and 2 are on opposite sides: the same notes there mirror each other, each way round.</summary>
    [Fact]
    public void Find_WithSameNotesOnOppositeSides_ReturnsMirrorEachWay()
    {
        // Arrange
        ModBuilder mod = Play(Play(Samples(), 0, 1, Rows(8, 8)), 1, 1, Rows(8, 8));

        // Act
        DropClues clues = DropClues.Find(PcmSongs.Analyze(mod.Build()));

        // Assert
        Assert.Equal(
            [(TwinKind.Mirror, new SongTrack(0, 0), new SongTrack(0, 1)), (TwinKind.Mirror, new SongTrack(0, 1), new SongTrack(0, 0))],
            clues.Twins.Select(c => (c.Kind, c.Track, c.Twin)));
        Assert.All(clues.Twins, c => Assert.Equal(8, c.Matches.Count));
        Assert.All(clues.Twins, c => Assert.Equal(1, c.Share));
        Assert.All(clues.Twins, c => Assert.Equal(0, c.DelayTicks));
    }

    /// <summary>Amiga channels 2 and 3 are on the same side: the same notes there are a double.</summary>
    [Fact]
    public void Find_WithSameNotesOnSameSide_ReturnsDouble()
    {
        // Arrange
        ModBuilder mod = Play(Play(Samples(), 1, 1, Rows(8, 8)), 2, 1, Rows(8, 8));

        // Act
        DropClues clues = DropClues.Find(PcmSongs.Analyze(mod.Build()));

        // Assert
        Assert.All(clues.Twins, c => Assert.Equal(TwinKind.Double, c.Kind));
        Assert.Equal(2, clues.Twins.Count);
    }

    /// <summary>Exactly three of a track's four notes covered is enough.</summary>
    [Fact]
    public void Find_WithThreeOfFourNotesCovered_MarksThatTrack()
    {
        // Arrange: channel 1 plays 12 notes, channel 4 only 9 of them plus 3 of its own.
        ModBuilder mod = Play(Play(Samples(), 0, 1, Rows(0, 12, every: 4)), 3, 1, [.. Rows(0, 9, every: 4), 37, 41, 45]);

        // Act
        DropClues clues = DropClues.Find(PcmSongs.Analyze(mod.Build()));

        // Assert
        TwinClue clue = Assert.Single(clues.Twins, c => c.Track == new SongTrack(0, 3));
        Assert.Equal(0.75, clue.Share);
    }

    /// <summary>A track that plays too many notes of its own isn't marked, even when its twin is: dropping it would lose them.</summary>
    [Fact]
    public void Find_WithLessThanThreeOfFourCovered_MarksOnlyTheCoveredTrack()
    {
        // Arrange: channel 2 plays channel 1's 8 notes and 8 more; channel 1 has none of its own.
        ModBuilder mod = Play(Play(Samples(), 0, 1, Rows(0, 8, every: 8)), 1, 1, Rows(0, 16, every: 4));

        // Act
        DropClues clues = DropClues.Find(PcmSongs.Analyze(mod.Build()));

        // Assert
        TwinClue clue = Assert.Single(clues.Twins);
        Assert.Equal(new SongTrack(0, 0), clue.Track);
    }

    /// <summary>Fewer shared notes than locked pairs need prove nothing.</summary>
    [Fact]
    public void Find_WithTooFewSharedNotes_ReturnsNoCopies()
    {
        // Arrange
        ModBuilder mod = Play(Play(Samples(), 0, 1, Rows(0, 7, every: 8)), 1, 1, Rows(0, 7, every: 8));

        // Act
        DropClues clues = DropClues.Find(PcmSongs.Analyze(mod.Build()));

        // Assert
        Assert.Empty(clues.Twins);
    }

    /// <summary>Another sample at the same pitch is an instrument of its own, not a copy (David).</summary>
    [Fact]
    public void Find_WithDifferentSampleSamePitch_ReturnsNoCopies()
    {
        // Arrange
        ModBuilder mod = Play(Play(Samples(), 0, 1, Rows(8, 8)), 1, 2, Rows(8, 8));

        // Act
        DropClues clues = DropClues.Find(PcmSongs.Analyze(mod.Build()));

        // Assert
        Assert.Empty(clues.Twins);
    }

    /// <summary>The same sample an octave up is a part of its own, not a copy (David).</summary>
    [Fact]
    public void Find_WithSameSampleAnOctaveUp_ReturnsNoCopies()
    {
        // Arrange
        ModBuilder mod = Play(Play(Samples(), 0, 1, Rows(8, 8)), 1, 1, Rows(8, 8), Period / 2);

        // Act
        DropClues clues = DropClues.Find(PcmSongs.Analyze(mod.Build()));

        // Assert
        Assert.Empty(clues.Twins);
    }

    /// <summary>The same notes a steady delay later and quieter are an echo, marked on the later track only.</summary>
    [Fact]
    public void Find_WithQuieterCopyLater_ReturnsEcho()
    {
        // Arrange: two rows later is 12 ticks of 0.02 s, at half the volume.
        ModBuilder mod = Play(Play(Samples(), 0, 1, Rows(0, 8, every: 8)), 1, 1, Rows(2, 8, every: 8), volume: 0x20);

        // Act
        DropClues clues = DropClues.Find(PcmSongs.Analyze(mod.Build()));

        // Assert
        TwinClue clue = Assert.Single(clues.Twins);
        Assert.Equal(TwinKind.Echo, clue.Kind);
        Assert.Equal(new SongTrack(0, 1), clue.Track);
        Assert.Equal(new SongTrack(0, 0), clue.Twin);
        Assert.Equal(12, clue.DelayTicks);
        Assert.Equal(0.24, clue.DelaySeconds, 3);
        Assert.Equal(6.02, clue.QuieterDb, 2);
    }

    /// <summary>A copy as loud as the first is a real part playing a round, not an echo (David).</summary>
    [Fact]
    public void Find_WithCopyAsLoud_ReturnsNoEcho()
    {
        // Arrange
        ModBuilder mod = Play(Play(Samples(), 0, 1, Rows(0, 8, every: 8)), 1, 1, Rows(2, 8, every: 8));

        // Act
        DropClues clues = DropClues.Find(PcmSongs.Analyze(mod.Build()));

        // Assert
        Assert.Empty(clues.Twins);
    }

    /// <summary>A quieter copy more than a second later is a part of its own, not an echo (David).</summary>
    [Fact]
    public void Find_WithCopyOverOneSecondLater_ReturnsNoEcho()
    {
        // Arrange: at speed 12 a row is 0.24 s, so six rows are 1.44 s.
        ModBuilder mod = Play(Play(Samples(), 0, 1, Rows(0, 8, every: 8)), 1, 1, Rows(6, 8, every: 8), volume: 0x20).WithCell(0, 0, 3, 0, 0, 0xF, 12);

        // Act
        DropClues clues = DropClues.Find(PcmSongs.Analyze(mod.Build()));

        // Assert
        Assert.Empty(clues.Twins);
    }

    /// <summary>A track far below the loudest under it is quiet and buried, and both clues keep their numbers; the loudest is neither.</summary>
    [Fact]
    public void Find_WithFaintTrackUnderLoudOne_MarksItQuietAndBuried()
    {
        // Arrange: volume 1 of 64 is 36 dB down.
        ModBuilder mod = Play(Play(Samples(), 0, 1, Rows(8, 8)), 1, 2, Rows(8, 8), volume: 1);

        // Act
        DropClues clues = DropClues.Find(PcmSongs.Analyze(mod.Build()));

        // Assert
        TrackLevel loud = Assert.Single(clues.Tracks, t => t.Track == new SongTrack(0, 0));
        TrackLevel faint = Assert.Single(clues.Tracks, t => t.Track == new SongTrack(1, 1));
        Assert.False(loud.Quiet);
        Assert.False(loud.Buried);
        Assert.Equal(0, loud.BelowLoudestDb);
        Assert.True(faint.Quiet);
        Assert.True(faint.Buried);
        Assert.Equal(1, faint.BuriedShare);
        Assert.Equal(8, faint.BuriedNotes.Count);
    }

    /// <summary>A faint track playing while nothing else sounds is quiet, but nothing buries it.</summary>
    [Fact]
    public void Find_WithFaintTrackAlone_MarksItQuietOnly()
    {
        // Arrange: a one-shot of 64 frames has run out within its first tick, long before the faint track starts.
        ModBuilder mod = Play(new ModBuilder().WithSample(1, "hit", 64).WithSample(2, "faint", 64, loopStart: 0, loopLength: 64), 0, 1, [0]);
        mod = Play(mod, 1, 2, Rows(8, 7, every: 8), volume: 1);

        // Act
        DropClues clues = DropClues.Find(PcmSongs.Analyze(mod.Build()));

        // Assert
        TrackLevel faint = Assert.Single(clues.Tracks, t => t.Track == new SongTrack(1, 1));
        Assert.True(faint.Quiet);
        Assert.False(faint.Buried);
        Assert.Empty(faint.BuriedNotes);
    }

    /// <summary>A sample without sound isn't a track: there is nothing to drop.</summary>
    [Fact]
    public void Find_WithSilentSample_LeavesItOut()
    {
        // Arrange
        ModBuilder mod = Play(new ModBuilder().WithSample(1, "silent", 64, data: _ => 0), 0, 1, Rows(0, 8, every: 8));

        // Act
        DropClues clues = DropClues.Find(PcmSongs.Analyze(mod.Build()));

        // Assert
        Assert.Empty(clues.Tracks);
        Assert.Empty(clues.Twins);
    }

    /// <summary>The report logs each clue with its numbers, then how many there are.</summary>
    [Fact]
    public void Log_WithClues_LogsEachAndTheCount()
    {
        // Arrange: a mirrored pair, with a faint track under it.
        ModBuilder mod = Play(Play(Play(Samples(), 0, 1, Rows(8, 8)), 1, 1, Rows(8, 8)), 2, 2, Rows(8, 8), volume: 1);
        DropClues clues = DropClues.Find(PcmSongs.Analyze(mod.Build()));
        CollectingSink sink = new();

        // Act
        DropClueReport.Log(sink.CreateLogger(), clues);

        // Assert
        string[] debug = [.. sink.Messages(LogEventLevel.Debug)];
        Assert.Equal("Drop clue: sample 1 on channel 1 mirrors channel 2 on 8 of its 8 notes, 0 ticks later, 0 dB quieter", debug[0]);
        Assert.Equal("Drop clue: sample 1 on channel 2 mirrors channel 1 on 8 of its 8 notes, 0 ticks later, 0 dB quieter", debug[1]);
        Assert.StartsWith("Drop clue: sample 2 on channel 3 is quiet and buried: 36", debug[2], StringComparison.Ordinal);
        Assert.Equal("4 drop clues (nothing is dropped: the optimizer weighs them)", debug[3]);
    }

    /// <summary>Each kind of copy has its word for logs and the harness's CSV.</summary>
    [Theory]
    [InlineData("Double", "doubles")]
    [InlineData("Mirror", "mirrors")]
    [InlineData("Echo", "echoes")]
    public void KindName_WithKind_ReturnsItsWord(string kind, string expected)
    {
        // Act
        string name = DropClueReport.KindName(Enum.Parse<TwinKind>(kind));

        // Assert
        Assert.Equal(expected, name);
    }

    /// <summary>Two looped samples with the same wave, so a track's level follows only its volume.</summary>
    private static ModBuilder Samples() => new ModBuilder()
        .WithSample(1, "saw", 64, loopStart: 0, loopLength: 64)
        .WithSample(2, "saw too", 64, loopStart: 0, loopLength: 64);

    /// <summary><paramref name="count"/> rows from row 0, <paramref name="every"/> apart.</summary>
    private static int[] Rows(int every, int count) => Rows(0, count, every);

    /// <summary><paramref name="count"/> rows, <paramref name="every"/> apart, from <paramref name="first"/>.</summary>
    private static int[] Rows(int first, int count, int every) => [.. Enumerable.Range(0, count).Select(k => first + (k * every))];

    /// <summary>Adds a note of <paramref name="sample"/> on <paramref name="channel"/> at each of <paramref name="rows"/>, at <paramref name="volume"/> when given.</summary>
    private static ModBuilder Play(ModBuilder mod, int channel, int sample, int[] rows, int period = Period, int? volume = null)
    {
        foreach (int row in rows)
        {
            mod.WithCell(0, row, channel, sample, period, volume is null ? 0 : 0xC, volume ?? 0);
        }

        return mod;
    }
}
