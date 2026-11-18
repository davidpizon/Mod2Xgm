using Mod2Xgm.Analysis;

namespace Mod2Xgm.Tests.Analysis;

/// <summary>Tests for <see cref="NoteFade"/>: a note's volume, tick by tick, baked into its recording (docs/adr/0120-faded-recordings.md).</summary>
public sealed class NoteFadeTests
{
    private const double Tick = 0.02;

    /// <summary>A note whose volume never moves has no fade: its copy at its starting volume plays it as it is.</summary>
    [Fact]
    public void Of_WithSteadyVolume_IsNull()
    {
        // Act
        NoteFade? sut = NoteFade.Of([0.5, 0.5, 0.5], Ticks(3));

        // Assert
        Assert.Null(sut);
    }

    /// <summary>A note that starts silent has no fade: nothing of it is stored.</summary>
    [Fact]
    public void Of_WithSilentStart_IsNull()
    {
        // Act
        NoteFade? sut = NoteFade.Of([0, 0.5], Ticks(2));

        // Assert
        Assert.Null(sut);
    }

    /// <summary>A slide down to 0 that stays there ends the fade at the last tick that sounds.</summary>
    [Fact]
    public void Of_WithSlideToSilence_EndsSilentAfterTheLastAudibleTick()
    {
        // Act
        NoteFade? sut = NoteFade.Of([1, 0.5, 0.25, 0, 0, 0], Ticks(6));

        // Assert
        Assert.NotNull(sut);
        Assert.True(sut.EndsSilent);
        Assert.Equal([1, 0.5, 0.25], sut.Gains);
        Assert.Equal(3 * Tick, sut.AudibleSeconds, 9);
    }

    /// <summary>A fade that comes back up from 0 doesn't end silent: the note is heard again.</summary>
    [Fact]
    public void Of_WithSilenceThenSound_KeepsEveryTick()
    {
        // Act
        NoteFade? sut = NoteFade.Of([0.8, 0, 0.4], Ticks(3));

        // Assert
        Assert.NotNull(sut);
        Assert.False(sut.EndsSilent);
        Assert.Equal([1, 0, 0.5], sut.Gains);
    }

    /// <summary>Each tick's gain applies from its start, after a short ramp from the tick before.</summary>
    [Fact]
    public void Apply_WithTwoTicks_ScalesEachTick()
    {
        // Arrange
        NoteFade sut = NoteFade.Of([1, 0.5], Ticks(2))!;
        float[] data = Ones(4000);

        // Act
        float[] faded = sut.Apply(data, 100_000);

        // Assert
        Assert.Equal(4000, faded.Length);
        Assert.Equal(1, faded[1000], 6);
        Assert.Equal(0.5, faded[3000], 6);
        Assert.InRange(faded[2050], 0.5f, 1f);
    }

    /// <summary>A fade to silence stops the recording a ramp after its last audible tick.</summary>
    [Fact]
    public void Apply_WithFadeToSilence_StopsAfterTheRampDown()
    {
        // Arrange
        NoteFade sut = NoteFade.Of([1, 0.5, 0, 0], Ticks(4))!;

        // Act
        float[] faded = sut.Apply(Ones(10_000), 100_000);

        // Assert
        Assert.Equal((int)Math.Ceiling(sut.StoredSeconds * 100_000), faded.Length);
        Assert.Equal(0, faded[^1], 2);
    }

    /// <summary>Past the last tick a fade that doesn't end silent holds its last gain.</summary>
    [Fact]
    public void Apply_PastTheLastTick_HoldsTheLastGain()
    {
        // Arrange
        NoteFade sut = NoteFade.Of([1, 0.25], Ticks(2))!;

        // Act
        float[] faded = sut.Apply(Ones(10_000), 100_000);

        // Assert
        Assert.Equal(0.25, faded[^1], 6);
    }

    /// <summary>Notes that fade alike share one copy: their fades are equal and hash alike.</summary>
    [Fact]
    public void Equals_WithSameFadeAtAnotherVolume_IsEqual()
    {
        // Arrange
        NoteFade sut = NoteFade.Of([1, 0.5], Ticks(2))!;
        NoteFade other = NoteFade.Of([0.5, 0.25], Ticks(2))!;

        // Act
        bool equal = sut.Equals(other);

        // Assert
        Assert.True(equal);
        Assert.Equal(sut.GetHashCode(), other.GetHashCode());
        Assert.NotEqual(sut, NoteFade.Of([1, 0.25], Ticks(2)));
    }

    /// <summary><paramref name="count"/> ticks of 20 ms.</summary>
    private static double[] Ticks(int count) => [.. Enumerable.Repeat(Tick, count)];

    /// <summary><paramref name="count"/> points at full scale.</summary>
    private static float[] Ones(int count) => [.. Enumerable.Repeat(1f, count)];
}
