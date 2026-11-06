using Mod2Xgm.Render;

namespace Mod2Xgm.Tests.Render;

/// <summary>Tests for the resampler that brings the Genesis render from the chip's rate to the output rate (docs/adr/0059-genesis-renderer.md).</summary>
public sealed class ChipRateResamplerTests
{
    private const double ChipHz = 53_267.03;

    private static float[] Tone(double hz, double rate, int length) =>
        [.. Enumerable.Range(0, length).Select(i => (float)Math.Sin(2 * Math.PI * hz * i / rate))];

    private static double Rms(float[] data, int from, int to) => Math.Sqrt(data[from..to].Average(v => (double)v * v));

    /// <summary>A tone well inside the band keeps its level; the length follows the rates.</summary>
    [Fact]
    public void Resample_WithToneInBand_KeepsLevel()
    {
        // Act
        float[] result = new ChipRateResampler(ChipHz, 44_100).Resample(Tone(1000, ChipHz, 53_267));

        // Assert
        Assert.Equal((int)Math.Floor(53_267 / (ChipHz / 44_100)), result.Length);
        Assert.Equal(Math.Sqrt(0.5), Rms(result, 1000, 40_000), 3);
    }

    /// <summary>A tone above the output's Nyquist frequency is filtered out rather than folded back.</summary>
    [Fact]
    public void Resample_WithToneAboveOutputBand_RemovesIt()
    {
        // Act
        float[] result = new ChipRateResampler(ChipHz, 44_100).Resample(Tone(25_000, ChipHz, 53_267));

        // Assert
        Assert.True(Rms(result, 1000, 40_000) < 1e-3);
    }

    /// <summary>Going up keeps a constant level; rates must be positive numbers.</summary>
    [Fact]
    public void Resample_WithConstant_KeepsItAndChecksRates()
    {
        // Act
        float[] result = new ChipRateResampler(22_050, 44_100).Resample([.. Enumerable.Repeat(0.5f, 1000)]);

        // Assert
        Assert.Equal(2000, result.Length);
        Assert.Equal(0.5, result[1000], 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChipRateResampler(0, 44_100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChipRateResampler(44_100, double.NaN));
    }
}
