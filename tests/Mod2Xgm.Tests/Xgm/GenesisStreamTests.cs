using Mod2Xgm.Audio;
using Mod2Xgm.Cli;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;
using Mod2Xgm.Xgm;
using Serilog.Core;
using Serilog.Events;

namespace Mod2Xgm.Tests.Xgm;

/// <summary>Tests for <see cref="GenesisStream.PcmAhead"/>: PCM commands sent ahead of their frame (docs/adr/0119-xgm2-pcm-lead.md).</summary>
public sealed class GenesisStreamTests
{
    private static readonly sbyte[] Sound = [1, 2, 3];

    /// <summary>A play and a stop move 2 frames ahead; the FM writes of their frame stay where they are.</summary>
    [Fact]
    public void PcmAhead_WithPlayAndStop_MovesOnlyThem()
    {
        // Arrange
        IReadOnlyList<ChipEvent>[] frames = [[], [], [], [ChipEvent.Fm(0, 0x28, 0), Play()], [], [ChipEvent.Stop(0)]];
        (ConversionNotes notes, CollectingSink sink) = Notes();

        // Act
        IReadOnlyList<IReadOnlyList<ChipEvent>> moved = GenesisStream.PcmAhead(frames, 2, null, 50, notes);

        // Assert
        Assert.Equal(6, moved.Count);
        Assert.Equal(ChipEventKind.PcmPlay, Assert.Single(moved[1]).Kind);
        Assert.Equal([ChipEventKind.FmWrite, ChipEventKind.PcmStop], moved[3].Select(e => e.Kind));
        Assert.Empty(moved[5]);
        Assert.Empty(sink.Messages(LogEventLevel.Warning));
    }

    /// <summary>A play in the first frames goes to frame 0, in order, with a note that it is heard late.</summary>
    [Fact]
    public void PcmAhead_WithPlayInFirstFrames_SendsItOnFrameZeroAndSaysSo()
    {
        // Arrange
        IReadOnlyList<ChipEvent>[] frames = [[ChipEvent.Stop(1)], [Play()], [], []];
        (ConversionNotes notes, CollectingSink sink) = Notes();

        // Act
        IReadOnlyList<IReadOnlyList<ChipEvent>> moved = GenesisStream.PcmAhead(frames, 2, null, 50, notes);

        // Assert
        Assert.Equal([ChipEventKind.PcmStop, ChipEventKind.PcmPlay], moved[0].Select(e => e.Kind));
        Assert.Contains("1 recordings start in the first 2 frames of the song", Assert.Single(sink.Messages(LogEventLevel.Warning)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A play in a looping song's first frames after the loop point is sent before the loop (the first time through) and
    /// again at the end of the song, 2 frames before the loop comes back round to it.
    /// </summary>
    [Fact]
    public void PcmAhead_WithPlayAfterLoopPoint_SendsItAlsoAtTheEnd()
    {
        // Arrange
        IReadOnlyList<ChipEvent>[] frames = [[], [], [], [Play()], [], [], [], []];

        // Act
        IReadOnlyList<IReadOnlyList<ChipEvent>> moved = GenesisStream.PcmAhead(frames, 2, 2, 50, Notes().Notes);

        // Assert
        Assert.Single(moved[1]);
        Assert.Single(moved[7]);
        Assert.Equal(2, moved.Sum(f => f.Count));
    }

    /// <summary>A PCM play of <see cref="Sound"/> on channel 0.</summary>
    private static ChipEvent Play() => ChipEvent.Play(0, Sound, false, 0, "1 \"x\"");

    /// <summary>A plan with an LFO rate turns the chip's LFO on at that rate in the first frame (docs/adr/0137-simple-mapper-fitted-patches.md).</summary>
    [Theory]
    [InlineData(5, 0x0D)]
    [InlineData(null, 0x00)]
    public void RecordXgm2_WithLfoRate_SetsLfoInFirstFrame(int? rate, int expected)
    {
        // Arrange
        Timing timing = new(6, 125);
        Song song = new("t", [new Channel(0)], [0], null, [new Pattern([new Row([Cell.Empty])])], [], [], timing);
        PlanRow row = new([.. Enumerable.Repeat(PlanCell.Empty, GenesisPlan.ChannelCount)], []);
        GenesisPlan plan = new GenesisPlan("t", [[row]], null, timing, [], []) { LfoRate = rate };
        Playback playback = new([new PlaybackTick(0, 0.02, 0, 0, 0, false, [new ChannelTick(false, new NotePitch(48, 0, 8287), 0, 0, null, new SampleBlock(0, 0, 0))])], null, PlaybackEnd.Stops);

        // Act
        GenesisStream sut = GenesisStream.RecordXgm2(plan, song, playback, Region.Ntsc, ResamplingMode.Smooth, new ConversionNotes(Logger.None));

        // Assert
        Assert.Contains(ChipEvent.Fm(0, 0x22, (byte)expected), sut.Frames[0]);
    }

    /// <summary>Each recorded frame carries the order and row playing then, for the busy-frame note (docs/adr/0139-xgm2-frame-budget.md).</summary>
    [Fact]
    public void RecordXgm2_WithTicks_RecordsEachFramesPosition()
    {
        // Arrange
        Timing timing = new(6, 125);
        Song song = new("t", [new Channel(0)], [0], null, [new Pattern([new Row([Cell.Empty]), new Row([Cell.Empty])])], [], [], timing);
        PlanRow row = new([.. Enumerable.Repeat(PlanCell.Empty, GenesisPlan.ChannelCount)], []);
        GenesisPlan plan = new("t", [[row, row]], null, timing, [], []);
        ChannelTick[] quiet = [new ChannelTick(false, new NotePitch(48, 0, 8287), 0, 0, null, new SampleBlock(0, 0, 0))];
        Playback playback = new([new PlaybackTick(0, 0.05, 0, 0, 0, false, quiet), new PlaybackTick(0.05, 0.05, 0, 1, 0, false, quiet)], null, PlaybackEnd.Stops);

        // Act
        GenesisStream sut = GenesisStream.RecordXgm2(plan, song, playback, Region.Ntsc, ResamplingMode.Smooth, new ConversionNotes(Logger.None));

        // Assert
        Assert.NotNull(sut.Positions);
        Assert.Equal(sut.Frames.Count, sut.Positions.Count);
        Assert.Equal((0, 0), sut.Positions[0]);
        Assert.Equal((0, 1), sut.Positions[^1]);
    }

    /// <summary>A notes collector and the sink that sees the conversion notes it logs.</summary>
    private static (ConversionNotes Notes, CollectingSink Sink) Notes()
    {
        CollectingSink sink = new();
        return (new ConversionNotes(sink.CreateLogger()), sink);
    }
}
