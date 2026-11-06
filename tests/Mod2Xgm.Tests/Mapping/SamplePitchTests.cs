using Mod2Xgm.Mapping;
using Mod2Xgm.Model;

namespace Mod2Xgm.Tests.Mapping;

/// <summary>Tests for <see cref="SamplePitch"/>: the cycle length of looped and one-shot tones, and no pitch for noise or silence.</summary>
public sealed class SamplePitchTests
{
    /// <summary>A one-cycle loop is a whole tone: its cycle is the loop's length, however short.</summary>
    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(100)]
    [InlineData(512)]
    public void Measure_WithOneCycleLoop_ReturnsLoopLength(int length)
    {
        // Arrange
        Sample sample = new("wave", Sine(length, length), new SampleLoop(0, length));

        // Act
        double? period = SamplePitch.Measure(sample).PeriodFrames;

        // Assert
        Assert.Equal(length, period ?? double.NaN, 0.05);
    }

    /// <summary>A loop holding several cycles is measured by the cycle, not the loop.</summary>
    [Fact]
    public void Measure_WithSeveralCyclesInLoop_ReturnsCycle()
    {
        // Arrange
        Sample sample = new("wave", Sine(256, 64), new SampleLoop(0, 256));

        // Act
        double? period = SamplePitch.Measure(sample).PeriodFrames;

        // Assert
        Assert.Equal(64, period ?? double.NaN, 0.05);
    }

    /// <summary>A one-shot tone is measured after its attack, with sub-frame precision.</summary>
    [Fact]
    public void Measure_WithOneShotTone_ReturnsCycle()
    {
        // Arrange
        Sample sample = new("bass", Sine(8000, 75.5), null);

        // Act
        double? period = SamplePitch.Measure(sample).PeriodFrames;

        // Assert
        Assert.Equal(75.5, period ?? double.NaN, 0.1);
    }

    /// <summary>A rich wave (a saw) is measured at its fundamental, not a harmonic.</summary>
    [Fact]
    public void Measure_WithSaw_ReturnsFundamental()
    {
        // Arrange
        float[] saw = [.. Enumerable.Range(0, 48).Select(i => (float)((i / 24.0) - 1))];
        Sample sample = new("saw", saw, new SampleLoop(0, 48));

        // Act
        double? period = SamplePitch.Measure(sample).PeriodFrames;

        // Assert
        Assert.Equal(48, period ?? double.NaN, 0.1);
    }

    /// <summary>
    /// A one-shot shorter than the full window (a chip blip) is still measured at its fundamental: here a few hundred frames
    /// with k0w-rsnd sample 1's harmonics, whose fifth is 11 dB above the fundamental.
    /// </summary>
    [Theory]
    [InlineData(482, 21)]
    [InlineData(964, 42)]
    [InlineData(500, 20)]
    public void Measure_WithShortOneShot_ReturnsFundamental(int frames, int cycle)
    {
        // Arrange: harmonics 1–9 in dB against the fifth, measured from the sample.
        double[] db = [-11, -60, -4, -11, 0, -5, -2, -8, -8];
        float[] blip = [.. Enumerable.Range(0, frames).Select(i => (float)db.Select((d, k) => Math.Pow(10, d / 20) * Math.Sin(2 * Math.PI * (k + 1) * i / cycle)).Sum())];

        // Act
        double? period = SamplePitch.Measure(new Sample("blip", blip, null)).PeriodFrames;

        // Assert
        Assert.Equal(cycle, period ?? double.NaN, 0.1);
    }

    /// <summary>Noise has no pitch, long or short.</summary>
    [Theory]
    [InlineData(8000)]
    [InlineData(500)]
    public void Measure_WithNoise_HasNoPitch(int frames)
    {
        // Arrange
        float[] noise = [.. new Random(3).GetItems([-1f, -0.5f, 0.5f, 1f], frames)];

        // Act
        double? period = SamplePitch.Measure(new Sample("noise", noise, null)).PeriodFrames;

        // Assert
        Assert.Null(period);
    }

    /// <summary>Silence has no pitch.</summary>
    [Fact]
    public void Measure_WithSilence_HasNoPitch()
    {
        // Arrange
        Sample sample = new("silence", new float[8000], null);

        // Act
        double? period = SamplePitch.Measure(sample).PeriodFrames;

        // Assert
        Assert.Null(period);
    }

    /// <summary>A silent loop has no pitch.</summary>
    [Fact]
    public void Measure_WithSilentLoop_HasNoPitch()
    {
        // Arrange
        Sample sample = new("quiet loop", new float[64], new SampleLoop(0, 64));

        // Act
        double? period = SamplePitch.Measure(sample).PeriodFrames;

        // Assert
        Assert.Null(period);
    }

    /// <summary>A sample too short to measure has no pitch.</summary>
    [Fact]
    public void Measure_WithVeryShortSample_HasNoPitch()
    {
        // Arrange
        Sample sample = new("click", Sine(100, 20), null);

        // Act
        double? period = SamplePitch.Measure(sample).PeriodFrames;

        // Assert
        Assert.Null(period);
    }

    /// <summary>The frequency at a playback rate is the rate over the cycle length.</summary>
    [Fact]
    public void FrequencyAt_WithCycle_ReturnsRateOverCycle()
    {
        // Arrange
        SamplePitch sut = new(32);

        // Act
        double? hz = sut.FrequencyAt(8288);

        // Assert
        Assert.Equal(259.0, hz ?? double.NaN, 0.01);
    }

    /// <summary>Without a pitch there is no frequency.</summary>
    [Fact]
    public void FrequencyAt_WithoutPitch_ReturnsNull()
    {
        // Arrange
        SamplePitch sut = new(null);

        // Act
        double? hz = sut.FrequencyAt(8288);

        // Assert
        Assert.Null(hz);
    }

    /// <summary>A sine of <paramref name="period"/> frames, <paramref name="frames"/> long.</summary>
    private static float[] Sine(int frames, double period, double phase = 0) =>
        [.. Enumerable.Range(0, frames).Select(i => (float)Math.Sin((2 * Math.PI * i / period) + phase))];
}
