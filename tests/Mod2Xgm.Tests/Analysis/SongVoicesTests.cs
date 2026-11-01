using Mod2Xgm.Analysis;
using Mod2Xgm.Model;

namespace Mod2Xgm.Tests.Analysis;

/// <summary>Tests for <see cref="SongVoices"/>: how many notes a song sounds at once, in all and per sample.</summary>
public sealed class SongVoicesTests
{
    /// <summary>A rate that plays 100 frames per 20 ms tick.</summary>
    private const double Rate = 5000;

    /// <summary>
    /// Channels count while their note sounds: a one-shot stops counting once it has played out, a looped note keeps
    /// counting, a channel at volume 0 doesn't count, and a sample on two channels counts twice.
    /// </summary>
    [Fact]
    public void NotesCountWhileTheySound()
    {
        Song song = new(
            "song",
            [new Channel(-1), new Channel(1), new Channel(1)],
            [0],
            null,
            [new Pattern([new Row([new Cell(null, null, null, []), new Cell(null, null, null, []), new Cell(null, null, null, [])])])],
            [new Instrument("a", 0, 1, 0), new Instrument("b", 1, 1, 0)],
            [new Sample("shot", new float[150], null), new Sample("loop", new float[100], new SampleLoop(0, 100))],
            new Timing(6, 125));
        ChannelTick Start(int sample, int frames, SampleBlock next, double volume = 1) =>
            new(true, new NotePitch(36, 0, Rate), volume, 0, new SampleBlock(sample, 0, frames), next);
        ChannelTick Hold(SampleBlock next, double volume = 1) => new(true, new NotePitch(36, 0, Rate), volume, 0, null, next);
        SampleBlock end = new(0, 0, 2);
        SampleBlock loop = new(1, 0, 100);
        Playback playback = new(
            [
                new PlaybackTick(0, 0.02, 0, 0, 0, false, [Start(0, 150, end), Start(0, 150, end), Start(1, 100, loop)]),
                new PlaybackTick(0.02, 0.02, 0, 0, 1, false, [Hold(end), Hold(end, 0), Hold(loop)]),
                new PlaybackTick(0.04, 0.02, 0, 0, 2, false, [Hold(end), Hold(end), Hold(loop)]),
                new PlaybackTick(0.06, 0.02, 0, 0, 3, false, [Hold(end), Start(1, 100, loop), Hold(loop)]),
            ],
            null,
            PlaybackEnd.Stops);

        SongVoices voices = SongVoices.Count(song, playback);

        Assert.Equal(3, voices.MostAtOnce);
        Assert.Equal(2, voices.MostAtOnceOf(0));
        Assert.Equal(2, voices.MostAtOnceOf(1));
        Assert.Equal(0, voices.MostAtOnceOf(5));
        Assert.Equal(0, voices.MostAtOnceOf(-1));
    }

    /// <summary>A song with no ticks sounds nothing.</summary>
    [Fact]
    public void NoTicksNoVoices()
    {
        Song song = new("song", [new Channel(0)], [], null, [], [], [new Sample("a", new float[10], null)], new Timing(6, 125));

        SongVoices voices = SongVoices.Count(song, new Playback([], null, PlaybackEnd.Stops));

        Assert.Equal(0, voices.MostAtOnce);
        Assert.Equal([0], voices.SampleMostAtOnce);
    }
}
