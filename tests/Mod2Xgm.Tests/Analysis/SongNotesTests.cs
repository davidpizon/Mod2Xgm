using Mod2Xgm.Analysis;
using Mod2Xgm.Tests.Mod;
using Mod2Xgm.Tests.Optimizer;

namespace Mod2Xgm.Tests.Analysis;

/// <summary>Tests for <see cref="SongNote.Find"/>: where each note starts, how long it sounds and whether it bends.</summary>
public sealed class SongNotesTests
{
    /// <summary>A looped note sounds until the next note on its channel; its pitch and volume are the ones it starts at.</summary>
    [Fact]
    public void ANoteLastsUntilTheNextOne()
    {
        byte[] mod = new ModBuilder()
            .WithSample(1, "saw", 64, volume: 32, loopStart: 0, loopLength: 64)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 428)
            .WithCell(0, row: 4, channel: 0, sample: 1, period: 214)
            .Build();

        IReadOnlyList<SongNote> notes = PcmSongs.Analyze(mod).Notes;

        Assert.Equal(2, notes.Count);
        Assert.Equal(0, notes[0].Sample);
        Assert.Equal(24, notes[0].Ticks);
        Assert.Equal(24 * PcmSongs.TickSeconds, notes[0].Seconds, 3);
        Assert.Equal(0.5, notes[0].Volume, 6);
        Assert.Equal(2 * notes[0].RateHz, notes[1].RateHz, 1);
        Assert.All(notes, n => Assert.False(n.Bent));
    }

    /// <summary>A one-shot ends when it runs out, and a vibrato, a volume slide or a start offset bends a note.</summary>
    [Fact]
    public void EffectsBendNotesAndOneShotsRunOut()
    {
        byte[] mod = new ModBuilder()
            .WithSample(1, "click", 1024)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 428)
            .WithCell(0, row: 0, channel: 1, sample: 1, period: 428, effect: 0x4, parameter: 0x48)
            .WithCell(0, row: 0, channel: 2, sample: 1, period: 428, effect: 0xA, parameter: 0x04)
            .WithCell(0, row: 0, channel: 3, sample: 1, period: 428, effect: 0x9, parameter: 0x01)
            .Build();

        IReadOnlyList<SongNote> notes = PcmSongs.Analyze(mod).Notes;

        Assert.Equal([0, 1, 2, 3], notes.Select(n => n.Channel));
        Assert.False(notes[0].Bent);
        Assert.True(notes[1].Bent);
        Assert.True(notes[2].Bent);
        Assert.True(notes[3].Bent);
        Assert.Equal(256, notes[3].StartFrame);
        Assert.InRange(notes[0].Seconds, 1024 / notes[0].RateHz, (1024 / notes[0].RateHz) + PcmSongs.TickSeconds);
    }
}
