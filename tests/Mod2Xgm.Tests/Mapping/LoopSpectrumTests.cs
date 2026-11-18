using Mod2Xgm.Mapping;
using Mod2Xgm.Model;

namespace Mod2Xgm.Tests.Mapping;

/// <summary>
/// Tests for <see cref="LoopSpectrum"/>: the power of a repeating loop near a frequency, and the bottom note of a chord or
/// chorused tone (docs/adr/0125-chord-samples.md).
/// </summary>
public sealed class LoopSpectrumTests
{
    /// <summary>A sample without a loop, or with a silent loop, has no spectrum.</summary>
    [Theory]
    [InlineData(false, 1.0)]
    [InlineData(true, 0.0)]
    public void Of_WithoutLoopOrSound_ReturnsNull(bool loops, double amplitude)
    {
        // Arrange
        Sample sample = new("tone", Partials(64, (1, amplitude)), loops ? new SampleLoop(0, 64) : null);

        // Act
        LoopSpectrum? sut = LoopSpectrum.Of(sample);

        // Assert
        Assert.Null(sut);
    }

    /// <summary>A sine's power lies all in the band around its frequency, none an octave up, and nothing is above Nyquist.</summary>
    [Fact]
    public void Share_WithSine_HoldsItsPowerAtItsFrequency()
    {
        // Arrange
        LoopSpectrum? sut = LoopSpectrum.Of(new Sample("sine", Partials(64, (1, 1.0)), new SampleLoop(0, 64)));

        // Act
        double at = sut?.Share(1 / 64.0, SamplePitch.ChordCents) ?? double.NaN;
        double octave = sut?.Share(1 / 32.0, SamplePitch.ChordCents) ?? double.NaN;
        double beyond = sut?.Share(0.6, SamplePitch.ChordCents) ?? double.NaN;

        // Assert
        Assert.InRange(at, 0.999, 1.001);
        Assert.InRange(octave, 0, 1e-6);
        Assert.Equal(0, beyond);
    }

    /// <summary>The bottom note skips a partial more than 20 dB below the loudest (here 34 dB): it isn't heard as a note.</summary>
    [Fact]
    public void BottomNote_WithQuietLowPartial_SkipsIt()
    {
        // Arrange
        float[] frames = Partials(6400, (50, 0.02), (100, 1.0));
        LoopSpectrum? sut = LoopSpectrum.Of(new Sample("tone", frames, new SampleLoop(0, 6400)));

        // Act
        double? bottom = sut?.BottomNote(SamplePitch.ChordCents, SamplePitch.StrongDb, 1 / 1024.0);

        // Assert
        Assert.Equal(100 / 6400.0, bottom ?? double.NaN, 1e-5);
    }

    /// <summary>A chorused partial (two copies 17 cents apart) is one note, at its middle.</summary>
    [Fact]
    public void BottomNote_WithChorusedPartial_ReturnsItsMiddle()
    {
        // Arrange
        LoopSpectrum? sut = LoopSpectrum.Of(new Sample("tone", Partials(16384, (100, 0.5), (101, 0.5)), new SampleLoop(0, 16384)));

        // Act
        double? bottom = sut?.BottomNote(SamplePitch.ChordCents, SamplePitch.StrongDb, 1 / 1024.0);

        // Assert
        Assert.InRange(bottom ?? double.NaN, 100 / 16384.0, 101 / 16384.0);
    }

    /// <summary><paramref name="frames"/> frames of sines of whole numbers of cycles over them, at the given amplitudes.</summary>
    private static float[] Partials(int frames, params (int Cycles, double Amplitude)[] partials) =>
        [.. Enumerable.Range(0, frames).Select(i => (float)partials.Sum(p => p.Amplitude * Math.Sin(2 * Math.PI * p.Cycles * i / frames)))];
}
