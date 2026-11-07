using Mod2Xgm.Optimizer;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="NoteMix"/>.</summary>
public sealed class NoteMixTests
{
    /// <summary>Checks that two voices add sample by sample, for as long as the shorter lasts.</summary>
    [Fact]
    public void Add_WithUnequalLengths_AddsUpToTheShorter()
    {
        // Arrange
        float[] a = [0.25f, 0.5f, 0.75f];
        float[] b = [0.5f, -0.5f];

        // Act
        float[] sut = NoteMix.Add(a, b);

        // Assert
        Assert.Equal([0.75f, 0f], sut);
    }
}
