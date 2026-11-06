using Mod2Xgm.Render.Fm;
using Mod2Xgm.Render.Pcm;

namespace Mod2Xgm.Tests.Render.Pcm;

/// <summary>
/// The DMF DAC model against how Furnace plays a DMF's FM6 samples (docs/adr/0058-pcm-driver-models.md): the expected
/// values come from Furnace's <c>genesis.cpp</c>, not from a capture.
/// </summary>
public sealed class DmfDacTests
{
    /// <summary>The chip's rate in whole hertz on NTSC, which the points are counted against.</summary>
    private const int NtscRate = 53_267;

    private static DmfDac InDacMode()
    {
        DmfDac dac = new(Ym3438.NtscClockHz);
        dac.SetDacMode(true);
        return dac;
    }

    /// <summary>A fresh FM6 is in FM mode: the DAC is off at 0, and a note plays no sample.</summary>
    [Fact]
    public void Play_InFmMode_PlaysNoSample()
    {
        // Arrange
        DmfDac sut = new(Ym3438.NtscClockHz);

        // Act
        bool played = sut.Play([0x4000], 8000);

        // Assert
        DacCapture capture = DacCapture.Of(sut, 100);
        Assert.False(played);
        Assert.All(capture.Data, b => Assert.Equal(0x80, b));
        Assert.All(capture.Enable, b => Assert.Equal(0, b));
        Assert.False(sut.IsPlaying);
    }

    /// <summary><c>17 01</c> turns the DAC on.</summary>
    [Fact]
    public void SetDacMode_WithOn_EnablesDac()
    {
        // Arrange
        DmfDac sut = new(Ym3438.NtscClockHz);

        // Act
        sut.SetDacMode(true);

        // Assert
        Assert.Equal(0x80, DacCapture.Of(sut, 1).Enable[0]);
    }

    /// <summary><c>17 00</c> turns the DAC off.</summary>
    [Fact]
    public void SetDacMode_WithOff_DisablesDac()
    {
        // Arrange
        DmfDac sut = InDacMode();

        // Act
        sut.SetDacMode(false);

        // Assert
        Assert.Equal(0, DacCapture.Of(sut, 1).Enable[0]);
    }

    /// <summary>
    /// At 8 kHz the counter passes 53 267 first on the 7th sample (7 × 8000), then every 6 or 7 samples: 8000 points a
    /// second, held in between, so a 2000-point sample plays in a quarter of a second.
    /// </summary>
    [Fact]
    public void Play_WithSample_StepsAtItsRate()
    {
        // Arrange
        DmfDac sut = InDacMode();
        short[] sample = [.. Enumerable.Range(1, 2000).Select(i => (short)(((i % 120) + 1) << 8))];

        // Act
        bool played = sut.Play(sample, 8000);

        // Assert
        DacCapture capture = DacCapture.Of(sut, NtscRate);
        Assert.True(played);
        Assert.Equal(0x80, capture.Data[5]);
        Assert.Equal(0x80 + 2, capture.Data[6]);
        List<int> changes = capture.Changes();
        Assert.Equal(6, changes[0]);
        Assert.All(changes.Zip(changes.Skip(1), (a, b) => b - a), gap => Assert.InRange(gap, 6, 7));
        Assert.Equal(2000, changes.Count);
    }

    /// <summary>Each 16-bit point goes to the DAC as its top 8 bits, rounded down: 255 is 0, −1 is −1.</summary>
    [Fact]
    public void Play_WithSixteenBitPoints_CutsTo8BitsRoundingDown()
    {
        // Arrange
        DmfDac sut = InDacMode();

        // Act
        sut.Play([short.MaxValue, short.MinValue, 255, -1, 0x1234, 0], NtscRate);

        // Assert
        DacCapture capture = DacCapture.Of(sut, 5);
        Assert.Equal([0xff, 0x00, 0x80, 0x7f, 0x80 + 0x12], capture.Data);
    }

    /// <summary>After the last point the DAC turns off (in the same sample) and keeps that last byte.</summary>
    [Fact]
    public void Generate_AtSamplesEnd_TurnsDacOff()
    {
        // Arrange
        DmfDac sut = InDacMode();
        sut.Play([0x1000, 0x2000, 0x3000], NtscRate);

        // Act
        DacCapture capture = DacCapture.Of(sut, 6);

        // Assert
        Assert.Equal([0x90, 0xa0, 0xb0, 0xb0, 0xb0, 0xb0], capture.Data);
        Assert.Equal([0x80, 0x80, 0, 0, 0, 0], capture.Enable);
        Assert.False(sut.IsPlaying);
    }

    /// <summary>A note-off stops the sample and turns the DAC off; the DAC keeps its byte.</summary>
    [Fact]
    public void Stop_WithSamplePlaying_StopsIt()
    {
        // Arrange
        DmfDac sut = InDacMode();
        sut.Play([0x1000, 0x2000, 0x3000], NtscRate);
        DacCapture.Of(sut, 1);

        // Act
        sut.Stop();

        // Assert
        DacCapture capture = DacCapture.Of(sut, 3);
        Assert.All(capture.Data, b => Assert.Equal(0x90, b));
        Assert.All(capture.Enable, b => Assert.Equal(0, b));
    }

    /// <summary>The next note after a note-off turns the DAC on again.</summary>
    [Fact]
    public void Play_AfterStop_TurnsDacOn()
    {
        // Arrange
        DmfDac sut = InDacMode();
        sut.Play([0x1000, 0x2000, 0x3000], NtscRate);
        DacCapture.Of(sut, 1);
        sut.Stop();
        DacCapture.Of(sut, 3);

        // Act
        sut.Play([0x4000, 0x4000], NtscRate);

        // Assert
        Assert.Equal(0x80, DacCapture.Of(sut, 1).Enable[0]);
    }

    /// <summary>A new note restarts from the first point.</summary>
    [Fact]
    public void Play_WithSamplePlaying_Restarts()
    {
        // Arrange
        DmfDac sut = InDacMode();
        short[] sample = [0x1000, 0x2000, 0x3000];
        sut.Play(sample, NtscRate);
        DacCapture.Of(sut, 2);

        // Act
        sut.Play(sample, NtscRate);

        // Assert
        Assert.Equal(0x90, DacCapture.Of(sut, 1).Data[0]);
    }

    /// <summary>Turning DAC mode off pauses the sample where it is.</summary>
    [Fact]
    public void SetDacMode_WithOff_PausesSample()
    {
        // Arrange
        DmfDac sut = InDacMode();
        sut.Play([0x1000, 0x2000, 0x3000], NtscRate);
        DacCapture.Of(sut, 1);

        // Act
        sut.SetDacMode(false);

        // Assert
        Assert.All(DacCapture.Of(sut, 4).Data, b => Assert.Equal(0x90, b));
        Assert.True(sut.IsPlaying);
    }

    /// <summary>Turning DAC mode back on carries a paused sample on from where it was.</summary>
    [Fact]
    public void SetDacMode_WithOnAfterOff_ResumesSample()
    {
        // Arrange
        DmfDac sut = InDacMode();
        sut.Play([0x1000, 0x2000, 0x3000], NtscRate);
        DacCapture.Of(sut, 1);
        sut.SetDacMode(false);
        DacCapture.Of(sut, 4);

        // Act
        sut.SetDacMode(true);

        // Assert
        Assert.Equal([0xa0, 0xb0], DacCapture.Of(sut, 2).Data);
    }

    /// <summary>An empty sample stops at once and, as in Furnace, leaves the DAC on.</summary>
    [Fact]
    public void Play_WithEmptySample_StopsAtOnce()
    {
        // Arrange
        DmfDac sut = InDacMode();

        // Act
        bool played = sut.Play([], 8000);

        // Assert
        DacCapture capture = DacCapture.Of(sut, 20);
        Assert.True(played);
        Assert.False(sut.IsPlaying);
        Assert.All(capture.Data, b => Assert.Equal(0x80, b));
        Assert.All(capture.Enable, b => Assert.Equal(0x80, b));
    }

    /// <summary>A clock too low to step, or not a number, is refused.</summary>
    [Theory]
    [InlineData(100)]
    [InlineData(double.NaN)]
    public void Constructor_WithBadClock_Throws(double clockHz)
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new DmfDac(clockHz));
    }

    /// <summary>A rate of 0 is refused.</summary>
    [Fact]
    public void Play_WithZeroRate_Throws()
    {
        // Arrange
        DmfDac sut = InDacMode();

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => sut.Play([1], 0));
    }

    /// <summary>A null sample is refused.</summary>
    [Fact]
    public void Play_WithNullSample_Throws()
    {
        // Arrange
        DmfDac sut = InDacMode();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => sut.Play(null!, 8000)); // null on purpose, past the annotation
    }

    /// <summary>The two buffers need the same length.</summary>
    [Fact]
    public void Generate_WithDifferentLengths_Throws()
    {
        // Arrange
        DmfDac sut = InDacMode();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => sut.Generate(new byte[1], new byte[2]));
    }
}
