using Mod2Xgm.Mapping;
using Mod2Xgm.Model;

namespace Mod2Xgm.Tests.Mapping;

/// <summary>Tests for <see cref="SampleTraits"/> and <see cref="FmPatchLibrary"/> (plan T5.3).</summary>
public sealed class SampleTraitsTests
{
    /// <summary>A slow sine is dark and not noise; brightness follows sin(π·f/rate).</summary>
    [Theory]
    [InlineData(256.0)]
    [InlineData(64.0)]
    [InlineData(16.0)]
    public void Analyze_WithSine_ReturnsSineOfItsFrequency(double period)
    {
        // Arrange
        Sample sample = new("s", Sine(2048, period), null);

        // Act
        SampleTraits traits = SampleTraits.Analyze(sample);

        // Assert
        Assert.Equal(Math.Sin(Math.PI / period), traits.Brightness, 0.01);
        Assert.False(traits.NoiseLike);
    }

    /// <summary>White noise is bright and has no repeating shape.</summary>
    [Fact]
    public void Analyze_WithWhiteNoise_ReturnsNoiseLike()
    {
        // Arrange
        Sample sample = new("n", Noise(2000), null);

        // Act
        SampleTraits traits = SampleTraits.Analyze(sample);

        // Assert
        Assert.True(traits.NoiseLike);
        Assert.InRange(traits.Brightness, 0.6, 1.0);
    }

    /// <summary>A bright but repeating tone (a square wave) is not noise.</summary>
    [Fact]
    public void Analyze_WithSquareWave_IsBrightButNotNoise()
    {
        // Arrange
        float[] square = [.. Enumerable.Range(0, 2048).Select(i => i % 8 < 4 ? 1f : -1f)];

        // Act
        SampleTraits traits = SampleTraits.Analyze(new Sample("sq", square, null));

        // Assert
        Assert.False(traits.NoiseLike);
        Assert.True(traits.Brightness > 0.3, $"brightness {traits.Brightness}");
    }

    /// <summary>Too few frames, or silence, say nothing: dark and not noise.</summary>
    [Theory]
    [InlineData(new float[0])]
    [InlineData(new[] { 0.5f })]
    [InlineData(new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f })]
    public void Analyze_WithTooFewFramesOrSilence_ReturnsDark(float[] data)
    {
        // Act
        SampleTraits traits = SampleTraits.Analyze(new Sample("s", data, null));

        // Assert
        Assert.Equal(new SampleTraits(0, false), traits);
    }

    /// <summary>A long stretch of silence says nothing either.</summary>
    [Fact]
    public void Analyze_WithLongSilence_ReturnsDark()
    {
        // Act
        SampleTraits traits = SampleTraits.Analyze(new Sample("silent", new float[500], null));

        // Assert
        Assert.Equal(new SampleTraits(0, false), traits);
    }

    /// <summary>Short noise is too little to call noise.</summary>
    [Fact]
    public void Analyze_WithShortNoise_IsNotNoiseLike()
    {
        // Act
        SampleTraits traits = SampleTraits.Analyze(new Sample("short", Noise(100), null));

        // Assert
        Assert.False(traits.NoiseLike);
    }

    /// <summary>With a loop, only the repeating part is measured: here a dark sine after a noisy start.</summary>
    [Fact]
    public void Analyze_WithLoop_MeasuresOnlyLoop()
    {
        // Arrange
        float[] data = [.. Noise(1000), .. Sine(1024, 256)];

        // Act
        SampleTraits traits = SampleTraits.Analyze(new Sample("l", data, new SampleLoop(1000, 1024)));

        // Assert
        Assert.True(traits.Brightness < 0.05, $"brightness {traits.Brightness}");
    }

    /// <summary>Without a loop the same data is measured from its noisy start.</summary>
    [Fact]
    public void Analyze_WithoutLoop_MeasuresWholeSample()
    {
        // Arrange
        float[] data = [.. Noise(1000), .. Sine(1024, 256)];

        // Act
        SampleTraits traits = SampleTraits.Analyze(new Sample("w", data, null));

        // Assert
        Assert.True(traits.Brightness > 0.3, $"brightness {traits.Brightness}");
    }

    /// <summary>Only the first frames of a long sample are examined.</summary>
    [Fact]
    public void Analyze_WithLongSample_MeasuresItsStart()
    {
        // Arrange
        float[] data = [.. Sine(4096, 256), .. Noise(4096)];

        // Act
        SampleTraits traits = SampleTraits.Analyze(new Sample("long", data, null));

        // Assert
        Assert.True(traits.Brightness < 0.05, $"brightness {traits.Brightness}");
    }

    /// <summary>The patch library: brightness picks a sustained patch.</summary>
    [Theory]
    [InlineData(0.05, nameof(FmPatchLibrary.Sine))]
    [InlineData(0.2, nameof(FmPatchLibrary.Organ))]
    [InlineData(0.3, nameof(FmPatchLibrary.Lead))]
    [InlineData(0.6, nameof(FmPatchLibrary.Brass))]
    public void ForSustained_WithBrightness_PicksPatch(double brightness, string expected)
    {
        // Act
        FmPatch patch = FmPatchLibrary.ForSustained(brightness);

        // Assert
        Assert.Equal(expected, patch.Name, ignoreCase: true);
    }

    /// <summary>The patch library: one-shots get a struck patch.</summary>
    [Theory]
    [InlineData(0.2, nameof(FmPatchLibrary.Pluck))]
    [InlineData(0.6, nameof(FmPatchLibrary.Brass))]
    public void ForOneShot_WithBrightness_PicksPatch(double brightness, string expected)
    {
        // Act
        FmPatch patch = FmPatchLibrary.ForOneShot(brightness);

        // Assert
        Assert.Equal(expected, patch.Name, ignoreCase: true);
    }

    /// <summary>The library has eleven differently named patches, covering every algorithm.</summary>
    [Fact]
    public void All_ForLibrary_CoversEveryAlgorithm()
    {
        // Act
        IReadOnlyList<FmPatch> patches = FmPatchLibrary.All;

        // Assert
        Assert.Equal(11, patches.Count);
        Assert.Equal(patches.Count, patches.Select(p => p.Name).Distinct().Count());
        Assert.Equal(Enumerable.Range(0, 8), patches.Select(p => p.Algorithm).Distinct().Order());
    }

    /// <summary>Every built-in patch holds values the YM2612 and the DMF writer accept.</summary>
    [Fact]
    public void All_ForEveryPatch_HoldsValidRegisterValues()
    {
        // Act
        IReadOnlyList<FmPatch> patches = FmPatchLibrary.All;

        // Assert
        Assert.All(patches, patch =>
        {
            Assert.InRange(patch.Algorithm, 0, 7);
            Assert.InRange(patch.Feedback, 0, 7);
            Assert.Equal(4, patch.Operators.Count);
            Assert.Contains(patch.Operators, op => op.Tl < 127);
            Assert.All(patch.Operators, AssertValidOperator);
        });
    }

    /// <summary>Asserts that each of an operator's values fits its register.</summary>
    private static void AssertValidOperator(FmOperator op)
    {
        Assert.InRange(op.Mult, 0, 15);
        Assert.InRange(op.Detune, -3, 3);
        Assert.InRange(op.Tl, 0, 127);
        Assert.InRange(op.Rs, 0, 3);
        Assert.InRange(op.Ar, 0, 31);
        Assert.InRange(op.Dr, 0, 31);
        Assert.InRange(op.D2R, 0, 31);
        Assert.False(op.Am);
        Assert.InRange(op.Sl, 0, 15);
        Assert.InRange(op.Rr, 0, 15);
    }

    /// <summary>A sine of <paramref name="periodFrames"/> frames, <paramref name="frames"/> long.</summary>
    private static float[] Sine(int frames, double periodFrames) =>
        [.. Enumerable.Range(0, frames).Select(i => (float)Math.Sin(2 * Math.PI * i / periodFrames))];

    /// <summary>White noise from a fixed seed.</summary>
    private static float[] Noise(int frames) => [.. new Random(3).GetItems([-1f, 1f], frames)];
}
