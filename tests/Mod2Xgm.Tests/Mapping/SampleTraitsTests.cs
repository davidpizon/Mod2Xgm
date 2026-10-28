using Mod2Xgm.Mapping;
using Mod2Xgm.Model;

namespace Mod2Xgm.Tests.Mapping;

/// <summary>Tests for <see cref="SampleTraits"/> and <see cref="FmPatchLibrary"/> (plan T5.3).</summary>
public sealed class SampleTraitsTests
{
    private static float[] Sine(int frames, double periodFrames) =>
        [.. Enumerable.Range(0, frames).Select(i => (float)Math.Sin(2 * Math.PI * i / periodFrames))];

    private static float[] Noise(int frames) => [.. new Random(3).GetItems([-1f, 1f], frames)];

    /// <summary>A slow sine is dark and not noise; brightness follows sin(π·f/rate).</summary>
    [Theory]
    [InlineData(256.0)]
    [InlineData(64.0)]
    [InlineData(16.0)]
    public void SineBrightnessIsTheSineOfItsFrequency(double period)
    {
        SampleTraits traits = SampleTraits.Analyze(new Sample("s", Sine(2048, period), null));

        Assert.Equal(Math.Sin(Math.PI / period), traits.Brightness, 0.01);
        Assert.False(traits.NoiseLike);
    }

    /// <summary>White noise is bright and has no repeating shape.</summary>
    [Fact]
    public void WhiteNoiseIsNoise()
    {
        SampleTraits traits = SampleTraits.Analyze(new Sample("n", Noise(2000), null));

        Assert.True(traits.NoiseLike);
        Assert.InRange(traits.Brightness, 0.6, 1.0);
    }

    /// <summary>A bright but repeating tone (a square wave) is not noise.</summary>
    [Fact]
    public void ABrightRepeatingToneIsNotNoise()
    {
        float[] square = [.. Enumerable.Range(0, 2048).Select(i => i % 8 < 4 ? 1f : -1f)];

        SampleTraits traits = SampleTraits.Analyze(new Sample("sq", square, null));

        Assert.False(traits.NoiseLike);
        Assert.True(traits.Brightness > 0.3);
    }

    /// <summary>Too few frames, or silence, say nothing: dark and not noise.</summary>
    [Fact]
    public void ShortOrSilentSamplesAreDark()
    {
        Assert.Equal(new SampleTraits(0, false), SampleTraits.Analyze(new Sample("empty", [], null)));
        Assert.Equal(new SampleTraits(0, false), SampleTraits.Analyze(new Sample("one", [0.5f], null)));
        Assert.Equal(new SampleTraits(0, false), SampleTraits.Analyze(new Sample("silent", new float[500], null)));
        Assert.False(SampleTraits.Analyze(new Sample("short", Noise(100), null)).NoiseLike);
    }

    /// <summary>With a loop, only the repeating part is measured.</summary>
    [Fact]
    public void OnlyTheLoopIsMeasured()
    {
        float[] data = [.. Noise(1000), .. Sine(1024, 256)];

        SampleTraits looped = SampleTraits.Analyze(new Sample("l", data, new SampleLoop(1000, 1024)));
        SampleTraits whole = SampleTraits.Analyze(new Sample("w", data, null));

        Assert.True(looped.Brightness < 0.05);
        Assert.True(whole.Brightness > 0.3);
    }

    /// <summary>Only the first frames of a long sample are examined.</summary>
    [Fact]
    public void LongSamplesAreMeasuredOverTheirStart()
    {
        float[] data = [.. Sine(4096, 256), .. Noise(4096)];

        Assert.True(SampleTraits.Analyze(new Sample("long", data, null)).Brightness < 0.05);
    }

    /// <summary>The patch library: brightness picks a sustained patch, and one-shots get a struck one.</summary>
    [Fact]
    public void PatchesAreChosenByBrightness()
    {
        Assert.Equal(FmPatchLibrary.Sine, FmPatchLibrary.ForSustained(0.05));
        Assert.Equal(FmPatchLibrary.Organ, FmPatchLibrary.ForSustained(0.2));
        Assert.Equal(FmPatchLibrary.Lead, FmPatchLibrary.ForSustained(0.3));
        Assert.Equal(FmPatchLibrary.Brass, FmPatchLibrary.ForSustained(0.6));
        Assert.Equal(FmPatchLibrary.Pluck, FmPatchLibrary.ForOneShot(0.2));
        Assert.Equal(FmPatchLibrary.Brass, FmPatchLibrary.ForOneShot(0.6));
    }

    /// <summary>Every built-in patch holds values the YM2612 and the DMF writer accept.</summary>
    [Fact]
    public void PatchesHoldValidRegisterValues()
    {
        Assert.Equal(5, FmPatchLibrary.All.Count);
        Assert.Equal(FmPatchLibrary.All.Count, FmPatchLibrary.All.Select(p => p.Name).Distinct().Count());
        foreach (FmPatch patch in FmPatchLibrary.All)
        {
            Assert.InRange(patch.Algorithm, 0, 7);
            Assert.InRange(patch.Feedback, 0, 7);
            Assert.Equal(4, patch.Operators.Count);
            Assert.Contains(patch.Operators, op => op.Tl < 127);
            foreach (FmOperator op in patch.Operators)
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
        }
    }
}
