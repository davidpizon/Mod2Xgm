using Mod2Xgm.Model;

namespace Mod2Xgm.Tests.Model;

/// <summary>Tests for the song model's own rules: every reference points inside the song (plan T3.1).</summary>
public sealed class SongTests
{
    private static readonly Channel[] AmigaChannels = [new(-1), new(1), new(1), new(-1)];

    /// <summary>A song whose orders, rows, instruments and samples all line up is accepted as given.</summary>
    [Fact]
    public void Constructor_WithConsistentSong_KeepsEverything()
    {
        // Arrange
        int[] expectedOrders = [0, 1, 0];
        float[] expectedData = [0.5f, -0.5f];

        // Act
        Song sut = Build();

        // Assert
        Assert.Equal(expectedOrders, sut.Orders);
        Assert.Equal(1, sut.LoopOrder);
        Assert.Equal(2, sut.Patterns.Count);
        Assert.Equal("test song", sut.Title);
        Assert.Equal(new Timing(6, 125), sut.Timing);
        Assert.Equal(-1, sut.Channels[0].Pan);
        Assert.Equal(new Instrument("kick", 0, 1.0, -12.5), sut.Instruments[0]);
        Assert.Equal("kick", sut.Samples[0].Name);
        Assert.Equal(expectedData, sut.Samples[0].Data);
        Assert.Equal(0.75, sut.Patterns[0].Rows[0].Cells[0].Volume);
    }

    /// <summary>An order naming a pattern the song doesn't have is a bug in the reader.</summary>
    [Fact]
    public void Constructor_WithOrderNamingNoPattern_Throws()
    {
        // Act
        ArgumentException error = Assert.Throws<ArgumentException>(() => Build(orders: [0, 2]));

        // Assert
        Assert.Contains("pattern 2", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The loop point must be one of the orders.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void Constructor_WithLoopOrderOutsideOrders_Throws(int loopOrder)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => Build(loopOrder: loopOrder));
    }

    /// <summary>A song that simply ends has no loop order.</summary>
    [Fact]
    public void Constructor_WithoutLoopOrder_HasNone()
    {
        // Act
        Song sut = Build(loopOrder: null);

        // Assert
        Assert.Null(sut.LoopOrder);
    }

    /// <summary>Every row has exactly one cell per channel.</summary>
    [Fact]
    public void Constructor_WithRowMissingCells_Throws()
    {
        // Arrange
        Pattern shortRow = new([new Row([Cell.Empty, Cell.Empty])]);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => Build(patterns: [OnePattern(), shortRow]));
    }

    /// <summary>A cell may only select an instrument the song has.</summary>
    [Fact]
    public void Constructor_WithCellNamingNoInstrument_Throws()
    {
        // Arrange
        Pattern pattern = new([new Row([Cell.Empty with { Instrument = 2 }, Cell.Empty, Cell.Empty, Cell.Empty])]);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => Build(patterns: [OnePattern(), pattern]));
    }

    /// <summary>An instrument must play a sample the song has.</summary>
    [Fact]
    public void Constructor_WithInstrumentNamingNoSample_Throws()
    {
        // Arrange
        Instrument[] instruments = [new("kick", 5, 1, 0)];

        // Act & Assert
        Assert.Throws<ArgumentException>(() => Build(instruments: instruments));
    }

    /// <summary>A loop must start and end inside the sample and hold at least one frame.</summary>
    [Theory]
    [InlineData(-1, 4)]
    [InlineData(0, 0)]
    [InlineData(6, 3)]
    public void SampleConstructor_WithLoopOutsideData_Throws(int start, int length)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new Sample("pad", new float[8], new SampleLoop(start, length)));
    }

    /// <summary>A loop that ends on the last frame is fine.</summary>
    [Fact]
    public void SampleConstructor_WithLoopEndingOnLastFrame_KeepsLoop()
    {
        // Act
        Sample sut = new("pad", new float[8], new SampleLoop(4, 4));

        // Assert
        Assert.Equal(8, sut.Length);
        Assert.Equal(new SampleLoop(4, 4), sut.Loop);
        Assert.False(sut.IsEmpty);
    }

    /// <summary>A slot with no frames is empty.</summary>
    [Fact]
    public void IsEmpty_WithoutFrames_ReturnsTrue()
    {
        // Act
        Sample sut = new("  by Jester", [], null);

        // Assert
        Assert.True(sut.IsEmpty);
        Assert.Null(sut.Loop);
    }

    /// <summary>The tick rate is BPM × 2 / 5: 50 Hz at the default 125 BPM.</summary>
    [Theory]
    [InlineData(125, 50.0)]
    [InlineData(150, 60.0)]
    public void TickRateHz_WithBpm_FollowsTempo(int bpm, double hz)
    {
        // Arrange
        Timing sut = new(6, bpm);

        // Act
        double rate = sut.TickRateHz;

        // Assert
        Assert.Equal(hz, rate);
    }

    /// <summary>A note keeps both its musical pitch and its exact playback rate.</summary>
    [Fact]
    public void NotePitchConstructor_WithPitchAndRate_KeepsBoth()
    {
        // Act
        NotePitch sut = new(24, -0.5, 8287.14);

        // Assert
        Assert.Equal(24, sut.Semitone);
        Assert.Equal(-0.5, sut.Cents);
        Assert.Equal(8287.14, sut.PlaybackRateHz);
    }

    /// <summary>A pattern of one row with a note at volume 0.75 on the first channel.</summary>
    private static Pattern OnePattern() =>
        new([new Row([Cell.Empty with { Note = new NotePitch(24, 0, 8287.14), Instrument = 0, Volume = 0.75 }, Cell.Empty, Cell.Empty, Cell.Empty])]);

    /// <summary>A consistent song, with the given parts replaced.</summary>
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
            instruments ?? [new Instrument("kick", 0, 1, -12.5), new Instrument(string.Empty, 1, 0, 0)],
            [new Sample("kick", [0.5f, -0.5f], null), new Sample(string.Empty, [], null)],
            new Timing(6, 125));
}
