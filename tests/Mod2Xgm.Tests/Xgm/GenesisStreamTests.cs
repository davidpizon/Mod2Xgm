using Mod2Xgm.Xgm;
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
        (ConversionNotes notes, MessageList messages) = Notes();

        // Act
        IReadOnlyList<IReadOnlyList<ChipEvent>> moved = GenesisStream.PcmAhead(frames, 2, null, 50, notes);

        // Assert
        Assert.Equal(6, moved.Count);
        Assert.Equal(ChipEventKind.PcmPlay, Assert.Single(moved[1]).Kind);
        Assert.Equal([ChipEventKind.FmWrite, ChipEventKind.PcmStop], moved[3].Select(e => e.Kind));
        Assert.Empty(moved[5]);
        Assert.Empty(messages);
    }

    /// <summary>A play in the first frames goes to frame 0, in order, with a note that it is heard late.</summary>
    [Fact]
    public void PcmAhead_WithPlayInFirstFrames_SendsItOnFrameZeroAndSaysSo()
    {
        // Arrange
        IReadOnlyList<ChipEvent>[] frames = [[ChipEvent.Stop(1)], [Play()], [], []];
        (ConversionNotes notes, MessageList messages) = Notes();

        // Act
        IReadOnlyList<IReadOnlyList<ChipEvent>> moved = GenesisStream.PcmAhead(frames, 2, null, 50, notes);

        // Assert
        Assert.Equal([ChipEventKind.PcmStop, ChipEventKind.PcmPlay], moved[0].Select(e => e.Kind));
        Assert.Contains("1 recordings start in the first 2 frames of the song", Assert.Single(messages), StringComparison.Ordinal);
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

    /// <summary>A notes collector and the conversion notes it logs.</summary>
    private static (ConversionNotes Notes, MessageList Messages) Notes()
    {
        CollectingSink sink = new();
        ConversionNotes notes = new(sink.CreateLogger());
        return (notes, new MessageList(sink));
    }

    /// <summary>The conversion notes (Warning messages) a <see cref="CollectingSink"/> has seen so far.</summary>
    private sealed class MessageList(CollectingSink sink) : IEnumerable<string>
    {
        /// <inheritdoc/>
        public IEnumerator<string> GetEnumerator() => sink.Messages(LogEventLevel.Warning).GetEnumerator();

        /// <inheritdoc/>
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
