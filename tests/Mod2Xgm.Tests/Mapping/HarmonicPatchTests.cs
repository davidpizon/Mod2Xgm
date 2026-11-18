using Mod2Xgm.Mapping;
using Mod2Xgm.Model;

namespace Mod2Xgm.Tests.Mapping;

/// <summary>Tests for <see cref="HarmonicPatch"/>: FM patches built from a loop's harmonics (docs/adr/0118-harmonic-patches.md).</summary>
public sealed class HarmonicPatchTests
{
    /// <summary>
    /// A loop whose second harmonic is louder than its fundamental sounds both, each at its level: 0.3 and 1 of a 1.3 sum
    /// are 12.7 dB and 2.3 dB down, TL 17 and 3.
    /// </summary>
    [Fact]
    public void Of_WithLoudSecondHarmonic_SoundsBothAtTheirLevels()
    {
        // Arrange
        Sample sample = new("bass", Wave(128, 128, (1, 0.3), (2, 1.0)), new SampleLoop(0, 128));

        // Act
        HarmonicPatch? sut = HarmonicPatch.Of(sample, 128);

        // Assert
        Assert.NotNull(sut?.Patch);
        Assert.Equal([1, 2], sut.Harmonics);
        Assert.Equal(1, sut.Capture, 3);
        Assert.Equal("Harmonic 1+2", sut.Patch.Name);
        Assert.Equal(7, sut.Patch.Algorithm);
        Assert.Equal([1, 2, 1, 1], sut.Patch.Operators.Select(o => o.Mult));
        Assert.Equal([17, 3, 127, 127], sut.Patch.Operators.Select(o => o.Tl));
    }

    /// <summary>A loop of several cycles is measured per cycle: its harmonics are those of the pitch, not of the loop.</summary>
    [Fact]
    public void Of_WithLoopOfTwoCycles_MeasuresHarmonicsOfOneCycle()
    {
        // Arrange
        Sample sample = new("tone", Wave(64, 32, (1, 1.0), (3, 0.5)), new SampleLoop(0, 64));

        // Act
        HarmonicPatch? sut = HarmonicPatch.Of(sample, 32);

        // Assert
        Assert.Equal([1, 3], sut?.Harmonics);
    }

    /// <summary>A lone fundamental is the library's plain sine, and harmonics 40 dB below the loudest are left out.</summary>
    [Fact]
    public void Of_WithSineAndInaudibleHarmonic_IsLibrarySine()
    {
        // Arrange
        Sample sample = new("sine", Wave(64, 64, (1, 1.0), (5, 0.005)), new SampleLoop(0, 64));

        // Act
        HarmonicPatch? sut = HarmonicPatch.Of(sample, 64);

        // Assert
        Assert.Same(FmPatchLibrary.Sine, sut?.Patch);
    }

    /// <summary>A sawtooth spreads its power over many harmonics: four hold less than 90 %, so no patch is built.</summary>
    [Fact]
    public void Of_WithSawtooth_HasNoPatch()
    {
        // Arrange
        float[] saw = [.. Enumerable.Range(0, 64).Select(i => (float)((i / 32.0) - 1))];
        Sample sample = new("saw", saw, new SampleLoop(0, 64));

        // Act
        HarmonicPatch? sut = HarmonicPatch.Of(sample, 64);

        // Assert
        Assert.NotNull(sut);
        Assert.Null(sut.Patch);
        Assert.Equal([1, 2, 3, 4], sut.Harmonics);
        Assert.InRange(sut.Capture, 0.8, 0.9);
    }

    /// <summary>A sample can't be measured without a loop, with a loop that isn't a whole number of cycles, or when silent.</summary>
    [Theory]
    [InlineData(false, 64, 1.0)]
    [InlineData(true, 40, 1.0)]
    [InlineData(true, 64, 0.0)]
    public void Of_WithUnmeasurableLoop_ReturnsNull(bool loops, double period, double amplitude)
    {
        // Arrange
        Sample sample = new("tone", Wave(64, 64, (1, amplitude)), loops ? new SampleLoop(0, 64) : null);

        // Act
        HarmonicPatch? sut = HarmonicPatch.Of(sample, period);

        // Assert
        Assert.Null(sut);
    }

    /// <summary><paramref name="frames"/> frames of sines at multiples of a <paramref name="period"/>-frame cycle, with the given amplitudes.</summary>
    private static float[] Wave(int frames, int period, params (int Harmonic, double Amplitude)[] partials) =>
        [.. Enumerable.Range(0, frames).Select(i => (float)partials.Sum(p => p.Amplitude * Math.Sin(2 * Math.PI * p.Harmonic * i / period)))];
}
