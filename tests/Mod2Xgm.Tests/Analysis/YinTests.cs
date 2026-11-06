using Mod2Xgm.Analysis;

namespace Mod2Xgm.Tests.Analysis;

/// <summary>Tests for <see cref="Yin"/>: the normalized difference and the dips that give a pitch.</summary>
public sealed class YinTests
{
    /// <summary>A sine's difference starts at 1 and falls to about 0 at twice its period.</summary>
    [Fact]
    public void Normalized_WithSine_FallsAtPeriod()
    {
        // Act
        double[] normalized = Yin.Normalized(Sine(20.5, 600), 256, 100);

        // Assert
        Assert.Equal(1, normalized[0]);
        Assert.True(normalized[41] < 0.01, $"lag 41: {normalized[41]}");
    }

    /// <summary>The first dip finds the period between whole lags.</summary>
    [Fact]
    public void FirstDip_WithSine_ReturnsPeriod()
    {
        // Arrange
        double[] normalized = Yin.Normalized(Sine(20.5, 600), 256, 100);

        // Act
        double? dip = Yin.FirstDip(normalized, 2, 0.15);

        // Assert
        Assert.Equal(20.5, dip ?? double.NaN, 0.05);
    }

    /// <summary>Near a guess, the dip is the lowest lag in range, even where an earlier lag also repeats the sound.</summary>
    [Theory]
    [InlineData(35, 45, 40.0)]
    [InlineData(15, 25, 20.0)]
    public void DipNear_WithGuess_ReturnsDipInRange(int low, int high, double expected)
    {
        // Arrange
        double[] normalized = Yin.Normalized(Sine(20, 600), 256, 100);

        // Act
        double? dip = Yin.DipNear(normalized, low, high, 0.15);

        // Assert
        Assert.Equal(expected, dip ?? double.NaN, 0.05);
    }

    /// <summary>Without a lag in range that repeats the sound well enough, there is no dip.</summary>
    [Fact]
    public void DipNear_WithoutRepeatInRange_ReturnsNull()
    {
        // Arrange
        double[] normalized = Yin.Normalized(Sine(20, 600), 256, 100);

        // Act
        double? dip = Yin.DipNear(normalized, 5, 15, 0.15);

        // Assert
        Assert.Null(dip);
    }

    /// <summary>Silence has no dip.</summary>
    [Fact]
    public void FirstDip_WithSilence_ReturnsNull()
    {
        // Arrange
        double[] normalized = Yin.Normalized(new float[400], 256, 100);

        // Act
        double? dip = Yin.FirstDip(normalized, 2, 0.15);

        // Assert
        Assert.Null(dip);
    }

    /// <summary>The window and the lags must fit in the frames.</summary>
    [Fact]
    public void Normalized_WithTooFewFrames_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => Yin.Normalized(new float[300], 256, 100));
    }

    /// <summary>A sine of <paramref name="period"/> frames, <paramref name="frames"/> long.</summary>
    private static float[] Sine(double period, int frames) =>
        [.. Enumerable.Range(0, frames).Select(i => (float)Math.Sin(2 * Math.PI * i / period))];
}
