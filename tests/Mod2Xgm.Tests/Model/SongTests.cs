using Mod2Xgm.Model;

namespace Mod2Xgm.Tests.Model;

/// <summary>Tests for the song model's own rules: every reference points inside the song (plan T3.1).</summary>
public sealed class SongTests
{
    private static readonly Channel[] AmigaChannels = [new(-1), new(1), new(1), new(-1)];

    /// <summary>A song whose orders, rows, instruments and samples all line up is accepted as given.</summary>
    [Fact]
    public void AConsistentSongIsAccepted()
    {
        Song song = Build();

        int[] expectedOrders = [0, 1, 0];
        Assert.Equal(expectedOrders, song.Orders);
        Assert.Equal(1, song.LoopOrder);
        Assert.Equal(2, song.Patterns.Count);
        Assert.Equal("kick", song.Instruments[0].Name);
        Assert.Equal(-1, song.Channels[0].Pan);
    }

    /// <summary>An order naming a pattern the song doesn't have is a bug in the reader.</summary>
    [Fact]
    public void OrdersMustNameExistingPatterns()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => Build(orders: [0, 2]));
        Assert.Contains("pattern 2", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The loop point must be one of the orders.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void TheLoopOrderMustBeAnOrder(int loopOrder) =>
        Assert.Throws<ArgumentException>(() => Build(loopOrder: loopOrder));

    /// <summary>A song that simply ends has no loop order.</summary>
    [Fact]
    public void ASongMayEndWithoutLooping() => Assert.Null(Build(loopOrder: null).LoopOrder);

    /// <summary>Every row has exactly one cell per channel.</summary>
    [Fact]
    public void RowsNeedOneCellPerChannel()
    {
        Pattern shortRow = new([new Row([Cell.Empty, Cell.Empty])]);
        Assert.Throws<ArgumentException>(() => Build(patterns: [OnePattern(), shortRow]));
    }

    /// <summary>A cell may only select an instrument the song has.</summary>
    [Fact]
    public void CellsMustNameExistingInstruments()
    {
        Pattern pattern = new([new Row([Cell.Empty with { Instrument = 2 }, Cell.Empty, Cell.Empty, Cell.Empty])]);
        Assert.Throws<ArgumentException>(() => Build(patterns: [OnePattern(), pattern]));
    }

    /// <summary>An instrument must play a sample the song has.</summary>
    [Fact]
    public void InstrumentsMustNameExistingSamples() =>
        Assert.Throws<ArgumentException>(() => Build(instruments: [new Instrument("kick", 5, 1, 0)]));

    /// <summary>A loop must start and end inside the sample and hold at least one frame.</summary>
    [Theory]
    [InlineData(-1, 4)]
    [InlineData(0, 0)]
    [InlineData(6, 3)]
    public void ASampleLoopMustLieInsideTheData(int start, int length) =>
        Assert.Throws<ArgumentException>(() => new Sample("pad", new float[8], new SampleLoop(start, length)));

    /// <summary>A loop that ends on the last frame is fine; a slot with no frames is empty.</summary>
    [Fact]
    public void SamplesKnowTheirLengthLoopAndEmptiness()
    {
        Sample looped = new("pad", new float[8], new SampleLoop(4, 4));
        Sample empty = new("  by Jester", [], null);

        Assert.Equal(8, looped.Length);
        Assert.Equal(new SampleLoop(4, 4), looped.Loop);
        Assert.False(looped.IsEmpty);
        Assert.True(empty.IsEmpty);
        Assert.Null(empty.Loop);
    }

    /// <summary>The tick rate is BPM × 2 / 5: 50 Hz at the default 125 BPM.</summary>
    [Theory]
    [InlineData(125, 50.0)]
    [InlineData(150, 60.0)]
    public void TickRateFollowsTheTempo(int bpm, double hz) => Assert.Equal(hz, new Timing(6, bpm).TickRateHz);

    /// <summary>A note keeps both its musical pitch and its exact playback rate.</summary>
    [Fact]
    public void NotesKeepPitchAndRate()
    {
        NotePitch c2 = new(24, -0.5, 8287.14);

        Assert.Equal(24, c2.Semitone);
        Assert.Equal(-0.5, c2.Cents);
        Assert.Equal(8287.14, c2.PlaybackRateHz);
    }

    private static Pattern OnePattern() =>
        new([new Row([Cell.Empty with { Note = new NotePitch(24, 0, 8287.14), Instrument = 0 }, Cell.Empty, Cell.Empty, Cell.Empty])]);

    private static Song Build(
        int[]? orders = null,
        int? loopOrder = 1,
        Pattern[]? patterns = null,
        Instrument[]? instruments = null) =>
        new(
            "test song",
            AmigaChannels,
            orders ?? [0, 1, 0],
            loopOrder,
            patterns ?? [OnePattern(), OnePattern()],
            instruments ?? [new Instrument("kick", 0, 1, 0), new Instrument(string.Empty, 1, 0, 0)],
            [new Sample("kick", [0.5f, -0.5f], null), new Sample(string.Empty, [], null)],
            new Timing(6, 125));
}
