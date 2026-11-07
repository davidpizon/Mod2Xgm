using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;
using Mod2Xgm.Optimizer;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="SoundMenu"/>: how a fitted patch is made ready for the plan.</summary>
public sealed class SoundMenuTests
{
    /// <summary>Checks that a chord is known by its loud upper notes over a root that doesn't sound, as kc's SynthFMaj is.</summary>
    [Fact]
    public void IsChord_WithSilentRoot_IsTrue()
    {
        // Arrange
        float[][] harmonics = [[-60, -60, -13, -20, -14, -24]];

        // Act
        bool sut = SoundMenu.IsChord(harmonics);

        // Assert
        Assert.True(sut);
    }

    /// <summary>Checks that a bright note isn't taken for a chord: cancan's Banjo has its loudest harmonic high, but its second is only 10 dB down.</summary>
    [Fact]
    public void IsChord_WithBrightNote_IsFalse()
    {
        // Arrange
        float[][] harmonics = [[-40, -23, -21, -17, -13, -15]];

        // Act
        bool sut = SoundMenu.IsChord(harmonics);

        // Assert
        Assert.False(sut);
    }

    /// <summary>Checks that a note whose fundamental is loudest isn't taken for a chord.</summary>
    [Fact]
    public void IsChord_WithLoudFundamental_IsFalse()
    {
        // Arrange
        float[][] harmonics = [[-13, -40, -40, -40]];

        // Act
        bool sut = SoundMenu.IsChord(harmonics);

        // Assert
        Assert.False(sut);
    }

    /// <summary>Checks that no harmonics to go by say it isn't a chord.</summary>
    [Fact]
    public void IsChord_WithNoHarmonics_IsFalse()
    {
        // Arrange
        float[][] harmonics = [];

        // Act
        bool sut = SoundMenu.IsChord(harmonics);

        // Assert
        Assert.False(sut);
    }

    /// <summary>Checks that a chord's second voice is a copy of its patch, spread by 22 cents, with the first turned down at least the 3 dB the pair sits under the sample.</summary>
    [Fact]
    public void SecondVoice_WithChord_ReturnsDetunedCopy()
    {
        // Arrange
        FmPatch first = FmPatchLibrary.Sine with { HeardRms = 0.5 };
        PitchRegion region = RegionOf([-60, -60, -13, -20, -14, -24]);

        // Act
        (FmPatch? second, double firstLevel, double spread) = SoundMenu.SecondVoice(region, first, true, "second");

        // Assert
        Assert.Equal("second", second?.Name);
        Assert.Equal(first.Operators, second?.Operators);
        Assert.Equal(22, spread);
        Assert.InRange(firstLevel, 0.01, 0.71);
    }

    /// <summary>Checks that a sample that isn't a chord gets no second voice.</summary>
    [Fact]
    public void SecondVoice_WithSingleNote_ReturnsNone()
    {
        // Arrange
        FmPatch first = FmPatchLibrary.Sine with { HeardRms = 0.5 };
        PitchRegion region = RegionOf([-13, -40, -40, -40]);

        // Act
        (FmPatch? second, double firstLevel, double spread) = SoundMenu.SecondVoice(region, first, true, "second");

        // Assert
        Assert.Null(second);
        Assert.Equal(1, firstLevel);
        Assert.Equal(0, spread);
    }

    /// <summary>A pitch region whose one frame has these harmonic levels.</summary>
    private static PitchRegion RegionOf(float[] harmonics) =>
        new(8000, 8000, 8000, 1, 100, new RegionSpectrum(0.01, [[-20f]], [harmonics]));

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
