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
    public void FftSizeMustBeAPowerOfTwo(int size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FftPlan(size));

    /// <summary>Arrays of the wrong length are refused.</summary>
    [Fact]
    public void FftRefusesTheWrongLength() =>
        Assert.Throws<ArgumentException>(() => new FftPlan(8).Forward(new double[8], new double[4]));

    /// <summary>A sine of exactly k cycles per frame puts all its energy in bin k (magnitude N/2).</summary>
    [Fact]
    public void FftOfASinePeaksAtItsBin()
    {
        const int n = 64;
        const int k = 5;
        double[] real = [.. Enumerable.Range(0, n).Select(i => Math.Sin(2 * Math.PI * k * i / n))];
        double[] imag = new double[n];

        new FftPlan(n).Forward(real, imag);

        Assert.Equal(n / 2.0, Math.Sqrt((real[k] * real[k]) + (imag[k] * imag[k])), 6);
        Assert.Equal(n / 2.0, Math.Sqrt((real[n - k] * real[n - k]) + (imag[n - k] * imag[n - k])), 6);
        Assert.Equal(0, Math.Sqrt((real[k + 1] * real[k + 1]) + (imag[k + 1] * imag[k + 1])), 6);
    }

    /// <summary>An impulse has a flat spectrum of 1.</summary>
    [Fact]
    public void FftOfAnImpulseIsFlat()
    {
        double[] real = new double[16];
        double[] imag = new double[16];
        real[0] = 1;

        new FftPlan(16).Forward(real, imag);

        Assert.All(real, v => Assert.Equal(1, v, 9));
        Assert.All(imag, v => Assert.Equal(0, v, 9));
    }

    /// <summary>Parseval: energy in time equals energy in frequency divided by N.</summary>
    [Fact]
    public void FftKeepsEnergy()
    {
        Random random = new(5);
        double[] real = [.. Enumerable.Range(0, 128).Select(_ => random.NextDouble() - 0.5)];
        double[] imag = new double[128];
        double timeEnergy = real.Sum(v => v * v);

        new FftPlan(128).Forward(real, imag);

        double frequencyEnergy = real.Zip(imag, (r, i) => (r * r) + (i * i)).Sum() / 128;
        Assert.Equal(timeEnergy, frequencyEnergy, 9);
    }

    /// <summary>Stereo channels are averaged; mismatched lengths are refused.</summary>
    [Fact]
    public void StereoIsMixedDown()
    {
        AudioSignal mono = AudioSignal.FromStereo([1f, 0f], [0f, -1f], 100);

        Assert.Equal([0.5f, -0.5f], mono.Samples.ToArray());
        Assert.Throws<ArgumentException>(() => AudioSignal.FromStereo([1f], [1f, 2f], 100));
    }

    /// <summary>A positive shift delays (silence first); a negative one cuts the start; zero is the same object.</summary>
    [Fact]
    public void ShiftingMovesTheSignalInTime()
    {
        AudioSignal signal = new([1f, 2f, 3f], 100);

        Assert.Equal([0f, 0f, 1f, 2f, 3f], signal.Shifted(2).Samples.ToArray());
        Assert.Equal([3f], signal.Shifted(-2).Samples.ToArray());
        Assert.Empty(signal.Shifted(-10).Samples.ToArray());
        Assert.Same(signal, signal.Shifted(0));
        Assert.Equal(0.03, signal.Seconds, 9);
    }

    /// <summary>A signal needs a positive sample rate.</summary>
    [Fact]
    public void ASignalNeedsASampleRate() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioSignal([0f], 0));

    /// <summary>Frames cover the audio, the last one zero-padded.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1024, 1)]
    [InlineData(1025, 2)]
    [InlineData(1280, 2)]
    [InlineData(1281, 3)]
    public void FrameCountCoversTheAudio(int samples, int expected) =>
        Assert.Equal(expected, new MelAnalyzer(22050, 1024, 256, 32).FrameCount(samples));

    /// <summary>Silence is reported at the floor in every band.</summary>
    [Fact]
    public void SilenceIsAtTheFloor()
    {
        double[][] frames = new MelAnalyzer(22050, 1024, 256, 32).LogMel(new float[4096], 4096);

        Assert.All(frames, frame => Assert.All(frame, db => Assert.Equal(MelAnalyzer.FloorDb, db)));
    }

    /// <summary>A full-scale sine measures near 0 dB in its band and far less elsewhere.</summary>
    [Fact]
    public void ASineLightsOneBand()
    {
        MelAnalyzer analyzer = new(22050, 2048, 512, 64);
        AudioSignal sine = TestSignals.Sine(1000, 1, amplitude: 1);

        double[] frame = analyzer.LogMel(sine.Samples, sine.Length)[10];

        int loudest = Array.IndexOf(frame, frame.Max());
        Assert.InRange(frame[loudest], -6, 3);
        Assert.True(frame[loudest] - frame[(loudest + 10) % frame.Length] > 30);
    }

    /// <summary>Audio shorter than the analyzed length is treated as followed by silence.</summary>
    [Fact]
    public void ShortAudioIsPaddedWithSilence()
    {
        MelAnalyzer analyzer = new(22050, 1024, 256, 32);
        AudioSignal sine = TestSignals.Sine(1000, 0.1, amplitude: 1);

        double[][] frames = analyzer.LogMel(sine.Samples, sine.Length * 4);

        Assert.Equal(analyzer.FrameCount(sine.Length * 4), frames.Length);
        Assert.Equal(MelAnalyzer.FloorDb, frames[^1].Max());
        Assert.Throws<ArgumentOutOfRangeException>(() => analyzer.LogMel(sine.Samples, 10));
    }

    /// <summary>The centre of a frame is half a frame after its start.</summary>
    [Fact]
    public void FrameCentersAreHalfAFrameIn()
    {
        MelAnalyzer analyzer = new(22050, 1024, 256, 32);

        Assert.Equal((512 + 256) / 22050.0, analyzer.FrameCenterSeconds(1), 9);
        Assert.Equal(256, analyzer.HopSize);
    }
}
