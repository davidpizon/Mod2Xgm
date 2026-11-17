using Mod2Xgm.Mod;
using Mod2Xgm.Model;
using Mod2Xgm.Model.Window;
using Mod2Xgm.Tests.Mod;
using Serilog.Core;

namespace Mod2Xgm.Tests.Model;

/// <summary>
/// Tests for <see cref="PlaybackTimeline"/> (docs/adr/0116-song-window.md): which ticks start rows, how times and tracker
/// positions resolve to them, and how a looping song's timeline goes on past its first pass.
/// </summary>
public sealed class PlaybackTimelineTests
{
    private const int TicksPerRow = 6;

    /// <summary>The repeats of a row held by EEx don't start rows; the next row does.</summary>
    [Fact]
    public void RowStarts_WithPatternDelay_SkipsRepeats()
    {
        // Arrange
        PlaybackTimeline sut = Timeline(Song().WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xE, parameter: 0xE2));

        // Act
        int[] starts = [.. sut.RowStarts(0).Take(3)];

        // Assert
        Assert.Equal([0, 18, 24], starts);
    }

    /// <summary>The rows a pattern loop (E6x) plays again start rows each time.</summary>
    [Fact]
    public void RowStarts_WithPatternLoop_StartsRepeatedRows()
    {
        // Arrange
        PlaybackTimeline sut = Timeline(Song().WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xE, parameter: 0x61));

        // Act
        PlaybackTick[] rows = [.. sut.RowStarts(0).Take(3).Select(sut.TickAt)];

        // Assert
        Assert.Equal([0, 0, 1], rows.Select(t => t.Row));
    }

    /// <summary>A time halfway between two row starts snaps to the earlier one; a little later, to the later one.</summary>
    [Fact]
    public void Resolve_WithTimeBetweenRows_SnapsToNearestEarlierOnTie()
    {
        // Arrange
        PlaybackTimeline sut = Timeline(Song());
        double half = sut.Playback.Ticks[TicksPerRow].TimeSeconds / 2;

        // Act
        int tie = sut.Resolve(new TimePosition(half), 0, isEnd: false, "--start").Tick;
        int later = sut.Resolve(new TimePosition(half + 0.001), 0, isEnd: false, "--start").Tick;

        // Assert
        Assert.Equal(0, tie);
        Assert.Equal(TicksPerRow, later);
    }

    /// <summary>A tracker position resolves to the first time its row plays at or after the given tick, in a later pass if need be.</summary>
    [Fact]
    public void Resolve_WithTrackerPosition_FindsFirstPlayAtOrAfter()
    {
        // Arrange
        PlaybackTimeline sut = Timeline(Song());

        // Act
        int first = sut.Resolve(new TrackerPosition(0, 2), 0, isEnd: false, "--start").Tick;
        int again = sut.Resolve(new TrackerPosition(0, 2), first + 1, isEnd: true, "--end").Tick;

        // Assert
        Assert.Equal(2 * TicksPerRow, first);
        Assert.Equal(sut.TickCount + (2 * TicksPerRow), again);
    }

    /// <summary>A looping song's time goes on past its pass: a tick in the second pass is a pass plus its time in the loop.</summary>
    [Fact]
    public void TimeOf_PastFirstPass_AddsLoopTime()
    {
        // Arrange
        PlaybackTimeline sut = Timeline(Song());

        // Act
        double time = sut.TimeOf(sut.TickCount + 12);

        // Assert
        Assert.Equal(sut.PassSeconds + sut.Playback.Ticks[12].TimeSeconds, time, 1e-9);
        Assert.Equal(12, sut.RealTick(sut.TickCount + 12));
    }

    /// <summary>An end time past a song that stops is its end, marked as cut short.</summary>
    [Fact]
    public void Resolve_WithEndPastStoppingSong_ClampsToEnd()
    {
        // Arrange
        PlaybackTimeline sut = Timeline(Song().WithCell(0, row: 3, channel: 0, sample: 0, period: 0, effect: 0xF, parameter: 0));

        // Act
        (int tick, bool clamped) = sut.Resolve(new TimePosition(60), 1, isEnd: true, "--end");

        // Assert
        Assert.Equal(sut.TickCount, tick);
        Assert.True(clamped);
    }

    /// <summary>A playback with no ticks can't be cut.</summary>
    [Fact]
    public void Constructor_WithNoTicks_Throws()
    {
        // Arrange
        Playback empty = new([], null, PlaybackEnd.Stops);

        // Act & Assert
        Assert.Throws<WindowException>(() => new PlaybackTimeline(empty));
    }

    private static ModBuilder Song() => new ModBuilder().WithSample(1, "lead", 1024);

    private static PlaybackTimeline Timeline(ModBuilder builder)
    {
        ConversionNotes notes = new(Logger.None);
        ModModule module = ModReader.Read(builder.Build(), notes);
        return new PlaybackTimeline(ModTickPlayer.Play(module, AmigaPitch.PalClockHz, 1.0, notes));
    }
}
