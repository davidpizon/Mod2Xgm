using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;
using Mod2Xgm.Optimizer;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="SoundMenu"/>: how a fitted patch is made ready for the plan.</summary>
public sealed class SoundMenuTests
{
    /// <summary>Checks that a quiet fit is lifted until its loudest carrier is at TL 0, with the other carrier as far below as it was.</summary>
    [Fact]
    public void AtFullLevel_WithQuietCarriers_LiftsThemTogether()
    {
        // Arrange: algorithm 4 has carriers OP2 and OP4.
        FmPatch patch = Patch(algorithm: 4, tl1: 30, tl2: 20, tl3: 40, tl4: 25);

        // Act
        FmPatch sut = SoundMenu.AtFullLevel(patch);

        // Assert
        Assert.Equal([30, 0, 40, 5], sut.Operators.Select(o => o.Tl));
    }

    /// <summary>Checks that a patch already at full level comes back as it was.</summary>
    [Fact]
    public void AtFullLevel_WithCarrierAtTlZero_ReturnsSamePatch()
    {
        // Arrange
        FmPatch patch = Patch(algorithm: 0, tl1: 30, tl2: 20, tl3: 40, tl4: 0);

        // Act
        FmPatch sut = SoundMenu.AtFullLevel(patch);

        // Assert
        Assert.Same(patch, sut);
    }

    /// <summary>Checks that a muted carrier stays muted and doesn't count as the loudest.</summary>
    [Fact]
    public void AtFullLevel_WithMutedCarrier_LeavesItMuted()
    {
        // Arrange: algorithm 4 has carriers OP2 and OP4.
        FmPatch patch = Patch(algorithm: 4, tl1: 30, tl2: 127, tl3: 40, tl4: 25);

        // Act
        FmPatch sut = SoundMenu.AtFullLevel(patch);

        // Assert
        Assert.Equal([30, 127, 40, 0], sut.Operators.Select(o => o.Tl));
    }

    /// <summary>Checks that a held sine has a steady pitch.</summary>
    [Fact]
    public void HasSteadyPitch_WithHeldSine_ReturnsTrue()
    {
        // Arrange
        float[] data = [.. Enumerable.Range(0, 64).Select(i => (float)Math.Sin(2 * Math.PI * i / 64))];
        SampleAnalysis analysis = SampleAnalyzer.Analyze(0, new Sample("sine", data, new SampleLoop(0, 64)), [8287]);

        // Act
        bool steady = SoundMenu.HasSteadyPitch(analysis);

        // Assert
        Assert.True(steady);
    }

    /// <summary>Checks that hiss has no steady pitch for a tuned voice to hold.</summary>
    [Fact]
    public void HasSteadyPitch_WithHiss_ReturnsFalse()
    {
        // Arrange
        Random random = new(1);
        float[] data = [.. Enumerable.Range(0, 8000).Select(_ => (float)((random.NextDouble() * 2) - 1))];
        SampleAnalysis analysis = SampleAnalyzer.Analyze(0, new Sample("hiss", data, null), [8287]);

        // Act
        bool steady = SoundMenu.HasSteadyPitch(analysis);

        // Assert
        Assert.False(steady);
    }

    /// <summary>Checks that a held TL-0 sine carrier measures as a full-scale sine's RMS.</summary>
    [Fact]
    public void Measured_WithHeldSineCarrier_ReturnsSineRms()
    {
        // Arrange: algorithm 0's only carrier is OP4; the others are muted.
        FmPatch patch = Patch(algorithm: 0, tl1: 127, tl2: 127, tl3: 127, tl4: 0);

        // Act
        FmPatch sut = SoundMenu.Measured(patch, 440, loops: true);

        // Assert
        Assert.NotNull(sut.HeardRms);
        Assert.Equal(Math.Sqrt(0.5), sut.HeardRms.Value, 1);
    }

    /// <summary>Checks that a patch with no pitch to render at keeps the level worked out from its registers.</summary>
    [Fact]
    public void Measured_WithNoPitch_ReturnsSamePatch()
    {
        // Arrange
        FmPatch patch = Patch(algorithm: 0, tl1: 127, tl2: 127, tl3: 127, tl4: 0);

        // Act
        FmPatch sut = SoundMenu.Measured(patch, null, loops: true);

        // Assert
        Assert.Same(patch, sut);
    }

    /// <summary>Makes a patch with the given operator levels.</summary>
    private static FmPatch Patch(int algorithm, int tl1, int tl2, int tl3, int tl4) => new(
        "test",
        algorithm,
        0,
        [.. new[] { tl1, tl2, tl3, tl4 }.Select(tl => new FmOperator(1, 0, tl, 0, 31, false, 0, 0, 0, 15))]);
}
