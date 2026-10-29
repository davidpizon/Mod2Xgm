using Mod2Xgm.Mod;
using Mod2Xgm.Model;

namespace Mod2Xgm.Harness.Tests;

/// <summary>Tests for <see cref="OpenMptTiming"/> and <see cref="DurationCheck"/>: the song timeline against the reference.</summary>
public sealed class DurationCheckTests
{
    /// <summary>A real Amiga's tick at <paramref name="bpm"/> (ProTracker's CIA timer).</summary>
    private static double Cia(int bpm) => 1.0 / ModTickPlayer.CiaTickHz(bpm);

    /// <summary>OpenMPT's tick at <paramref name="bpm"/>: whole samples at 48 kHz.</summary>
    private static double OpenMpt(int bpm) => Math.Floor(48_000 * 2.5 / bpm) / 48_000;

    /// <summary>Ticks with the given lengths, as (order, row, tick-in-row, seconds), timed back to back.</summary>
    private static Playback Ticks(PlaybackEnd end, params (int Order, int Row, int Tick, double Seconds)[] ticks)
    {
        List<PlaybackTick> list = [];
        double time = 0;
        foreach ((int order, int row, int tick, double seconds) in ticks)
        {
            list.Add(new PlaybackTick(time, seconds, order, row, tick, false, []));
            time += seconds;
        }

        return new Playback(list, null, end);
    }

    /// <summary>A CIA tick length gives back its BPM; any other length has none.</summary>
    [Fact]
    public void BpmIsFoundFromTheCiaTick()
    {
        Assert.Equal(128, OpenMptTiming.Bpm(Cia(128)));
        Assert.Equal(32, OpenMptTiming.Bpm(Cia(32)));
        Assert.Equal(255, OpenMptTiming.Bpm(Cia(255)));
        Assert.Null(OpenMptTiming.Bpm(0.0213));
    }

    /// <summary>
    /// OpenMPT's ticks are whole samples at 48 kHz: 960 at BPM 125 (exactly 20 ms), 937 at 128 (937.5 cut down), as
    /// libopenmpt's row times on the corpus show.
    /// </summary>
    [Fact]
    public void TicksAreWholeSamplesAt48Khz()
    {
        Assert.Equal([960.0 / 48_000], OpenMptTiming.TickDurations(Ticks(PlaybackEnd.Stops, (0, 0, 0, Cia(125)))));
        Assert.Equal([937.0 / 48_000], OpenMptTiming.TickDurations(Ticks(PlaybackEnd.Stops, (0, 0, 0, Cia(128)))));
    }

    /// <summary>
    /// A tempo change shows one tick late in ProTracker: on the row's second tick, or on the next row at speed 1. OpenMPT
    /// times the tick that read the <c>Fxx</c> with it. (Only a row's first tick reads <c>Fxx</c>; the guard keeps any
    /// other change where it is.)
    /// </summary>
    [Fact]
    public void ATempoChangeMovesToTheTickThatReadIt()
    {
        IReadOnlyList<double> sameRow = OpenMptTiming.TickDurations(Ticks(PlaybackEnd.Stops, (0, 0, 0, Cia(101)), (0, 0, 1, Cia(128)), (0, 0, 2, Cia(128))));
        IReadOnlyList<double> speedOne = OpenMptTiming.TickDurations(Ticks(PlaybackEnd.Stops, (0, 0, 0, Cia(125)), (0, 1, 0, Cia(101)), (0, 2, 0, Cia(101))));
        IReadOnlyList<double> notFirstTick = OpenMptTiming.TickDurations(Ticks(PlaybackEnd.Stops, (0, 0, 1, Cia(101)), (0, 0, 2, Cia(128))));

        Assert.Equal([OpenMpt(128), OpenMpt(128), OpenMpt(128)], sameRow);
        Assert.Equal([OpenMpt(101), OpenMpt(101), OpenMpt(101)], speedOne);
        Assert.Equal([OpenMpt(101), OpenMpt(128)], notFirstTick);
    }

    /// <summary>A tick that isn't a CIA tempo (Ultimate Soundtracker's tempo byte) keeps its length, in whole samples.</summary>
    [Fact]
    public void OtherTicksKeepTheirLength()
    {
        IReadOnlyList<double> durations = OpenMptTiming.TickDurations(Ticks(PlaybackEnd.Stops, (0, 0, 0, 0.0213), (0, 0, 1, 0.0213)));

        Assert.Equal([1022.0 / 48_000, 1022.0 / 48_000], durations);
    }

    /// <summary>Each row is listed once, at its first tick, in playing order, with both of the tick player's times and the reference's.</summary>
    [Fact]
    public void RowsAreComparedAtTheirFirstTick()
    {
        Playback playback = Ticks(PlaybackEnd.Loops, (0, 0, 0, Cia(125)), (0, 0, 1, Cia(125)), (1, 5, 0, Cia(125)), (0, 0, 0, Cia(125)));
        List<(int, int)> asked = [];

        IReadOnlyList<RowTiming> rows = DurationCheck.Rows(playback, r =>
        {
            asked.AddRange(r);
            return [0.0, 0.05];
        });

        Assert.Equal([(0, 0), (1, 5)], asked);
        Assert.Equal(new RowTiming(0, 0, 0, 0, 0), rows[0]);
        Assert.Equal(2 * Cia(125), rows[1].TickPlayerSeconds, 12);
        Assert.Equal(0.04, rows[1].OpenMptRulesSeconds, 12);
        Assert.Equal(-0.01, rows[1].DifferenceSeconds, 9);
    }

    /// <summary>
    /// The song passes when every row and the end are within one tick (the longest) of the reference; the first row
    /// further off fails it, and so does the end, unless the song stops at F00.
    /// </summary>
    [Fact]
    public void EveryRowAndTheEndMustBeWithinOneTick()
    {
        Playback playback = Ticks(PlaybackEnd.Stops, (0, 0, 0, Cia(125)), (0, 1, 0, Cia(100)), (0, 2, 0, Cia(125)));
        double length = OpenMpt(125) + OpenMpt(100) + OpenMpt(125);
        RowTiming[] onTime = [new(0, 0, 0, 0, 0), new(0, 1, 0.02, 0.02, 0.02), new(0, 2, 0.045, 0.045, 0.045)];
        RowTiming[] late = [new(0, 0, 0, 0, 0), new(0, 1, 0.02, 0.02, 0.02), new(0, 2, 0.045, 0.045, 0.1)];

        DurationCheck exact = DurationCheck.Of(playback, onTime, length, stopsAtF00: false);
        DurationCheck endOneTickOff = DurationCheck.Of(playback, onTime, length + OpenMpt(100), stopsAtF00: false);
        DurationCheck endTwoTicksOff = DurationCheck.Of(playback, onTime, length + 0.05, stopsAtF00: false);
        DurationCheck stopsAtF00 = DurationCheck.Of(playback, onTime, length + 2, stopsAtF00: true);
        DurationCheck rowOff = DurationCheck.Of(playback, late, length, stopsAtF00: false);

        Assert.Equal(Cia(100), exact.ToleranceSeconds);
        Assert.Equal(length, exact.OpenMptRulesSeconds, 12);
        Assert.Equal(playback.DurationSeconds, exact.TickPlayerSeconds);
        Assert.Equal(playback.DurationSeconds - length, exact.AmigaDifferenceSeconds, 12);
        Assert.True(exact.Within);
        Assert.True(endOneTickOff.Within);
        Assert.False(endTwoTicksOff.Within);
        Assert.True(stopsAtF00.Within);
        Assert.False(rowOff.Within);
        Assert.Equal(late[2], rowOff.FirstRowOff);
    }

    /// <summary>A song with no ticks has no tolerance and nothing to be off.</summary>
    [Fact]
    public void AnEmptyPlaybackHasNoTolerance()
    {
        DurationCheck check = DurationCheck.Of(new Playback([], null, PlaybackEnd.Stops), [], 0, stopsAtF00: false);

        Assert.Equal(0, check.ToleranceSeconds);
        Assert.True(check.Within);
    }

    /// <summary>A song that stops on a row holding F00 ends at F00; one that loops, or stops elsewhere, doesn't.</summary>
    [Fact]
    public void AnF00EndIsRecognised()
    {
        ModCell[] cells = new ModCell[ModPattern.Rows * 4];
        cells[(3 * 4) + 2] = new ModCell(0, 0, 0xF, 0);
        cells[(5 * 4) + 1] = new ModCell(0, 0, 0xF, 6);
        ModModule module = new()
        {
            Title = "f00",
            Format = ModFormat.ProTracker31,
            Tag = "M.K.",
            ChannelCount = 4,
            Samples = [],
            SongLength = 1,
            RestartPosition = 127,
            OrderTable = [0],
            Patterns = [new ModPattern(4, cells)],
            FileSize = 0,
            Player = ModPlayer.ProTracker2,
        };

        Assert.True(DurationCheck.EndsAtF00(module, Ticks(PlaybackEnd.Stops, (0, 3, 0, Cia(125)))));
        Assert.False(DurationCheck.EndsAtF00(module, Ticks(PlaybackEnd.Loops, (0, 3, 0, Cia(125)))));
        Assert.False(DurationCheck.EndsAtF00(module, Ticks(PlaybackEnd.Stops, (0, 5, 0, Cia(125)))));
        Assert.False(DurationCheck.EndsAtF00(module, new Playback([], null, PlaybackEnd.Stops)));
    }
}
