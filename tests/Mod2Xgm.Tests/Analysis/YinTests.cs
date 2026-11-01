using Mod2Xgm.Analysis;

namespace Mod2Xgm.Tests.Analysis;

/// <summary>Tests for <see cref="Yin"/>: the normalized difference and the dips that give a pitch.</summary>
public sealed class YinTests
{
    /// <summary>A sine of <paramref name="period"/> frames, <paramref name="frames"/> long.</summary>
    private static float[] Sine(double period, int frames) =>
        [.. Enumerable.Range(0, frames).Select(i => (float)Math.Sin(2 * Math.PI * i / period))];

    /// <summary>A sine's difference falls to 0 at its period, and the first dip finds it between whole lags.</summary>
    [Fact]
    public void TheFirstDipIsThePeriod()
    {
        double[] normalized = Yin.Normalized(Sine(20.5, 600), 256, 100);

        Assert.Equal(1, normalized[0]);
        Assert.True(normalized[41] < 0.01, $"lag 41: {normalized[41]}");
        Assert.Equal(20.5, Yin.FirstDip(normalized, 2, 0.15)!.Value, 0.05);
    }

    /// <summary>Near a guess, the dip is the lowest lag in range, even where an earlier lag also repeats the sound.</summary>
    [Fact]
    public void TheDipNearAGuessSkipsEarlierPeriods()
    {
        double[] normalized = Yin.Normalized(Sine(20, 600), 256, 100);

        Assert.Equal(40, Yin.DipNear(normalized, 35, 45, 0.15)!.Value, 0.05);
        Assert.Equal(20, Yin.DipNear(normalized, 15, 25, 0.15)!.Value, 0.05);
    }

    /// <summary>Without a lag that repeats the sound well enough, there is no dip.</summary>
    [Fact]
    public void NoDipWithoutARepeat()
    {
        double[] normalized = Yin.Normalized(Sine(20, 600), 256, 100);

        Assert.Null(Yin.DipNear(normalized, 5, 15, 0.15));
        Assert.Null(Yin.FirstDip(Yin.Normalized(new float[400], 256, 100), 2, 0.15));
    }

    /// <summary>The window and the lags must fit in the frames.</summary>
    [Fact]
    public void TooFewFramesThrow() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Yin.Normalized(new float[300], 256, 100));
}
