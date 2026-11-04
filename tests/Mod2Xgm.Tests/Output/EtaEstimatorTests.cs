using Mod2Xgm.Output;

namespace Mod2Xgm.Tests.Output;

/// <summary>Tests of <see cref="EtaEstimator"/> without a timing history (plan T17.6, docs/cli-output.md#time-estimate).</summary>
public sealed class EtaEstimatorTests
{
    private static readonly PhaseInfo Write = new(7, "write", "Write DMF", string.Empty);
    private static readonly PhaseInfo Render = new(8, "render", "Render WAV", "ticks");

    private readonly EtaEstimator _sut = new();

    /// <summary>A phase's own pace doesn't count before half a second, or before 2 % of its work.</summary>
    [Theory]
    [InlineData(0.4, 50)]
    [InlineData(1.0, 1)]
    public void Remaining_BeforeEnoughTimeOrWork_ReturnsNull(double elapsedSeconds, long done)
    {
        // Act
        TimeSpan? remaining = _sut.Remaining(Render, TimeSpan.FromSeconds(elapsedSeconds), done, 100);

        // Assert
        Assert.Null(remaining);
    }

    /// <summary>Once it counts, the phase's own pace gives the time left: half done in a second is a second left.</summary>
    [Fact]
    public void Remaining_AfterEnoughTimeAndWork_ReturnsTimeAtOwnPace()
    {
        // Act
        TimeSpan? remaining = _sut.Remaining(Render, TimeSpan.FromSeconds(1), 50, 100);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(1), remaining);
    }

    /// <summary>A later estimate moves from the last one by <see cref="EtaEstimator.Smoothing"/> of the change.</summary>
    [Fact]
    public void Remaining_CalledAgain_SmoothsTowardNewPace()
    {
        // Arrange
        _sut.Remaining(Render, TimeSpan.FromSeconds(1), 50, 100);

        // Act
        TimeSpan? smoothed = _sut.Remaining(Render, TimeSpan.FromSeconds(2), 50, 100);

        // Assert
        Assert.NotNull(smoothed);
        Assert.Equal(1 + (EtaEstimator.Smoothing * (2 - 1)), smoothed.Value.TotalSeconds, 6);
    }

    /// <summary>Without history, the run's estimate is the current phase's, partial while phases are left.</summary>
    [Theory]
    [InlineData(null, true, null, true)]
    [InlineData(3.0, true, 3.0, true)]
    [InlineData(3.0, false, 3.0, false)]
    public void RunRemaining_WithoutHistory_ReturnsCurrentPhaseEstimate(double? currentSeconds, bool phasesLeft, double? expectedSeconds, bool expectedPartial)
    {
        // Arrange
        TimeSpan? current = currentSeconds is { } c ? TimeSpan.FromSeconds(c) : null;
        TimeSpan? expected = expectedSeconds is { } e ? TimeSpan.FromSeconds(e) : null;
        PhaseInfo[] waiting = phasesLeft ? [Write] : [];

        // Act
        (TimeSpan? remaining, bool partial) = _sut.RunRemaining(current, waiting);

        // Assert
        Assert.Equal(expected, remaining);
        Assert.Equal(expectedPartial, partial);
    }

    /// <summary>The render scales with ticks, the analysis with samples, every other phase with rows.</summary>
    [Theory]
    [InlineData("render", "ticks", SongSize.Ticks)]
    [InlineData("analyze", "samples", SongSize.Samples)]
    [InlineData("read", "bytes", SongSize.Rows)]
    public void SongSizeOf_WithPhase_ReturnsUnitItScalesWith(string id, string unit, string expected)
    {
        // Arrange
        PhaseInfo phase = new(2, id, id, unit);

        // Act
        string actual = SongSize.Of(phase);

        // Assert
        Assert.Equal(expected, actual);
    }
}
