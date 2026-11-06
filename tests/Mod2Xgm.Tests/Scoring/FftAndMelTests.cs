using Mod2Xgm.Scoring;

namespace Mod2Xgm.Tests.Scoring;

/// <summary>Tests for <see cref="AudioSignal"/>, <see cref="FftPlan"/> and <see cref="MelAnalyzer"/> (plan T6.2).</summary>
public sealed class FftAndMelTests
{
    /// <summary>Only powers of two from 2 up are accepted as FFT sizes.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(1000)]
    public void FftPlanConstructor_WithSizeNotPowerOfTwo_Throws(int size)
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new FftPlan(size));
    }

    /// <summary>Arrays of the wrong length are refused.</summary>
    [Fact]
    public void Forward_WithWrongLength_Throws()
    {
        // Arrange
        FftPlan sut = new(8);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => sut.Forward(new double[8], new double[4]));
    }

    /// <summary>A sine of exactly k cycles per frame puts all its energy in bin k (magnitude N/2).</summary>
    [Fact]
    public void Forward_WithSine_PeaksAtItsBin()
    {
        // Arrange
        const int n = 64;
        const int k = 5;
        double[] real = [.. Enumerable.Range(0, n).Select(i => Math.Sin(2 * Math.PI * k * i / n))];
        double[] imag = new double[n];
        FftPlan sut = new(n);

        // Act
        sut.Forward(real, imag);

        // Assert
        Assert.Equal(n / 2.0, Math.Sqrt((real[k] * real[k]) + (imag[k] * imag[k])), 6);
        Assert.Equal(n / 2.0, Math.Sqrt((real[n - k] * real[n - k]) + (imag[n - k] * imag[n - k])), 6);
        Assert.Equal(0, Math.Sqrt((real[k + 1] * real[k + 1]) + (imag[k + 1] * imag[k + 1])), 6);
    }

    /// <summary>An impulse has a flat spectrum of 1.</summary>
    [Fact]
    public void Forward_WithImpulse_IsFlat()
    {
        // Arrange
        double[] real = new double[16];
        double[] imag = new double[16];
        real[0] = 1;
        FftPlan sut = new(16);

        // Act
        sut.Forward(real, imag);

        // Assert
        Assert.All(real, v => Assert.Equal(1, v, 9));
        Assert.All(imag, v => Assert.Equal(0, v, 9));
    }

    /// <summary>Parseval: energy in time equals energy in frequency divided by N.</summary>
    [Fact]
    public void Forward_WithRandomSignal_KeepsEnergy()
    {
        // Arrange
        Random random = new(5);
        double[] real = [.. Enumerable.Range(0, 128).Select(_ => random.NextDouble() - 0.5)];
        double[] imag = new double[128];
        double timeEnergy = real.Sum(v => v * v);
        FftPlan sut = new(128);

        // Act
        sut.Forward(real, imag);

        // Assert
        Assert.Equal(timeEnergy, real.Zip(imag, (r, i) => (r * r) + (i * i)).Sum() / 128, 9);
    }

    /// <summary>Stereo channels are averaged.</summary>
    [Fact]
    public void FromStereo_WithTwoChannels_AveragesThem()
    {
        // Act
        AudioSignal mono = AudioSignal.FromStereo([1f, 0f], [0f, -1f], 100);

        // Assert
        Assert.Equal([0.5f, -0.5f], mono.Samples.ToArray());
    }

    /// <summary>Channels of different lengths are refused.</summary>
    [Fact]
    public void FromStereo_WithMismatchedLengths_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => AudioSignal.FromStereo([1f], [1f, 2f], 100));
    }

    /// <summary>A positive shift delays (silence first); a negative one cuts the start, down to nothing.</summary>
    [Theory]
    [InlineData(2, new[] { 0f, 0f, 1f, 2f, 3f })]
    [InlineData(-2, new[] { 3f })]
    [InlineData(-10, new float[0])]
    public void Shifted_WithFrames_MovesSignalInTime(int frames, float[] expected)
    {
        // Arrange
        AudioSignal sut = new([1f, 2f, 3f], 100);

        // Act
        AudioSignal shifted = sut.Shifted(frames);

        // Assert
        Assert.Equal(expected, shifted.Samples.ToArray());
    }

    /// <summary>A shift of zero is the same object.</summary>
    [Fact]
    public void Shifted_WithZero_ReturnsSameSignal()
    {
        // Arrange
        AudioSignal sut = new([1f, 2f, 3f], 100);

        // Act
        AudioSignal shifted = sut.Shifted(0);

        // Assert
        Assert.Same(sut, shifted);
    }

    /// <summary>A signal knows its length in seconds.</summary>
    [Fact]
    public void Seconds_WithSamples_ReturnsLength()
    {
        // Arrange
        AudioSignal sut = new([1f, 2f, 3f], 100);

        // Act
        double seconds = sut.Seconds;

        // Assert
        Assert.Equal(0.03, seconds, 9);
    }

    /// <summary>A signal needs a positive sample rate.</summary>
    [Fact]
    public void AudioSignalConstructor_WithZeroRate_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioSignal([0f], 0));
    }

    /// <summary>Frames cover the audio, the last one zero-padded.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1024, 1)]
    [InlineData(1025, 2)]
    [InlineData(1280, 2)]
    [InlineData(1281, 3)]
    public void FrameCount_WithSamples_CoversAudio(int samples, int expected)
    {
        // Arrange
        MelAnalyzer sut = new(22050, 1024, 256, 32);

        // Act
        int count = sut.FrameCount(samples);

        // Assert
        Assert.Equal(expected, count);
    }

    /// <summary>Silence is reported at the floor in every band.</summary>
    [Fact]
    public void LogMel_WithSilence_ReturnsFloor()
    {
        // Arrange
        MelAnalyzer sut = new(22050, 1024, 256, 32);

        // Act
        double[][] frames = sut.LogMel(new float[4096], 4096);

        // Assert
        Assert.All(frames, frame => Assert.All(frame, db => Assert.Equal(MelAnalyzer.FloorDb, db)));
    }

    /// <summary>A full-scale sine measures near 0 dB in its band and far less elsewhere.</summary>
    [Fact]
    public void LogMel_WithSine_LightsOneBand()
    {
        // Arrange
        MelAnalyzer sut = new(22050, 2048, 512, 64);
        AudioSignal sine = TestSignals.Sine(1000, 1, amplitude: 1);

        // Act
        double[] frame = sut.LogMel(sine.Samples, sine.Length)[10];

        // Assert
        int loudest = Array.IndexOf(frame, frame.Max());
        Assert.InRange(frame[loudest], -6, 3);
        Assert.True(frame[loudest] - frame[(loudest + 10) % frame.Length] > 30, $"{frame[loudest]} dB against {frame[(loudest + 10) % frame.Length]} dB");
    }

    /// <summary>Audio shorter than the analyzed length is treated as followed by silence.</summary>
    [Fact]
    public void LogMel_WithShortAudio_PadsWithSilence()
    {
        // Arrange
        MelAnalyzer sut = new(22050, 1024, 256, 32);
        AudioSignal sine = TestSignals.Sine(1000, 0.1, amplitude: 1);

        // Act
        double[][] frames = sut.LogMel(sine.Samples, sine.Length * 4);

        // Assert
        Assert.Equal(sut.FrameCount(sine.Length * 4), frames.Length);
        Assert.Equal(MelAnalyzer.FloorDb, frames[^1].Max());
    }

    /// <summary>An analyzed length shorter than the audio is refused.</summary>
    [Fact]
    public void LogMel_WithLengthShorterThanAudio_Throws()
    {
        // Arrange
        MelAnalyzer sut = new(22050, 1024, 256, 32);
        AudioSignal sine = TestSignals.Sine(1000, 0.1, amplitude: 1);

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => sut.LogMel(sine.Samples, 10));
    }

    /// <summary>The centre of a frame is half a frame after its start.</summary>
    [Fact]
    public void FrameCenterSeconds_WithFrame_IsHalfAFrameIn()
    {
        // Arrange
        MelAnalyzer sut = new(22050, 1024, 256, 32);

        // Act
        double center = sut.FrameCenterSeconds(1);

        // Assert
        Assert.Equal((512 + sut.HopSize) / 22050.0, center, 9);
        Assert.Equal(256, sut.HopSize);
    }
}
