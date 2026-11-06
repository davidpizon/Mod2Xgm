using Mod2Xgm.Analysis;
using Mod2Xgm.Tests.Mod;
using Mod2Xgm.Tests.Optimizer;

namespace Mod2Xgm.Tests.Analysis;

/// <summary>Tests for <see cref="SongNote.Find"/>: where each note starts, how long it sounds and whether it bends.</summary>
public sealed class SongNotesTests
{
    /// <summary>A looped note sounds until the next note on its channel; its pitch and volume are the ones it starts at.</summary>
    [Fact]
    public void Find_WithLoopedNotes_LastsUntilNextNote()
    {
        // Arrange
        byte[] mod = new ModBuilder()
            .WithSample(1, "saw", 64, volume: 32, loopStart: 0, loopLength: 64)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 428)
            .WithCell(0, row: 4, channel: 0, sample: 1, period: 214)
            .Build();

        // Act
        IReadOnlyList<SongNote> notes = PcmSongs.Analyze(mod).Notes;

        // Assert
        Assert.Equal(2, notes.Count);
        Assert.Equal(0, notes[0].Sample);
        Assert.Equal(24, notes[0].Ticks);
        Assert.Equal(24 * PcmSongs.TickSeconds, notes[0].Seconds, 3);
        Assert.Equal(0.5, notes[0].Volume, 6);
        Assert.Equal(2 * notes[0].RateHz, notes[1].RateHz, 1);
        Assert.All(notes, n => Assert.False(n.Bent));
    }

    /// <summary>A one-shot ends when it runs out.</summary>
    [Fact]
    public void Find_WithOneShot_EndsWhenItRunsOut()
    {
        // Act
        SongNote note = EffectNotes()[0];

        // Assert
        Assert.False(note.Bent);
        Assert.InRange(note.Seconds, 1024 / note.RateHz, (1024 / note.RateHz) + PcmSongs.TickSeconds);
    }

    /// <summary>A vibrato, a volume slide or a start offset bends a note.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Find_WithEffect_BendsNote(int channel)
    {
        // Act
        SongNote note = EffectNotes()[channel];

        // Assert
        Assert.Equal(channel, note.Channel);
        Assert.True(note.Bent);
    }

    /// <summary>A start offset moves where the note starts in its sample.</summary>
    [Fact]
    public void Find_WithSampleOffset_KeepsStartFrame()
    {
        // Act
        SongNote note = EffectNotes()[3];

        // Assert
        Assert.Equal(256, note.StartFrame);
    }

    /// <summary>The notes of one row of a one-shot on four channels: plain, with vibrato, with a volume slide and with a sample offset.</summary>
    private static IReadOnlyList<SongNote> EffectNotes()
    {
        byte[] mod = new ModBuilder()
            .WithSample(1, "click", 1024)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 428)
            .WithCell(0, row: 0, channel: 1, sample: 1, period: 428, effect: 0x4, parameter: 0x48)
            .WithCell(0, row: 0, channel: 2, sample: 1, period: 428, effect: 0xA, parameter: 0x04)
            .WithCell(0, row: 0, channel: 3, sample: 1, period: 428, effect: 0x9, parameter: 0x01)
            .Build();
        return PcmSongs.Analyze(mod).Notes;
    }
}
