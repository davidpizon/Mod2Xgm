using Mod2Xgm.Scoring;

namespace Mod2Xgm.Tests.Scoring;

/// <summary>Tests for <see cref="PerceptualMetrics"/> and <see cref="ErbSpectrogram"/> on synthetic signals (plan T9.4k1).</summary>
public sealed class PerceptualMetricsTests
{
    /// <summary>A rendering identical to the original is at zero distance on every feature, and its NMR is silence.</summary>
    [Fact]
    public void Compare_WithIdenticalSignals_ReportsNoDifference()
    {
        // Arrange
        AudioSignal tone = Mix(TestSignals.Sine(440, 1), TestSignals.Noise(1, 0.05));

        // Act
        PerceptualReport sut = PerceptualMetrics.Compare(tone, tone);

        // Assert
        Assert.Equal(PerceptualMetrics.SilenceDb, sut.NoiseToMaskDb);
        Assert.Equal(0, sut.NsimDistance, 9);
        Assert.Equal(0, sut.ModulationDistanceDb, 9);
        Assert.Equal(0, sut.HarmonicityDistanceDb, 9);
    }

    /// <summary>Two silent signals are at zero distance and report silence, without dividing by zero.</summary>
    [Fact]
    public void Compare_WithSilence_ReportsNoDifference()
    {
        // Arrange
        AudioSignal silence = new(new float[TestSignals.Rate], TestSignals.Rate);

        // Act
        PerceptualReport sut = PerceptualMetrics.Compare(silence, silence);

        // Assert
        Assert.Equal(new PerceptualReport(PerceptualMetrics.SilenceDb, 0, 0, 0), sut);
    }

    /// <summary>Noise 40 dB under a tone stays below the tone's mask (NMR under 0 dB), and louder noise rises well above it.</summary>
    [Fact]
    public void NoiseToMask_WithLouderNoise_Rises()
    {
        // Arrange
        AudioSignal tone = TestSignals.Sine(440, 1);
        AudioSignal quiet = Mix(tone, TestSignals.Noise(1, 0.005));
        AudioSignal loud = Mix(tone, TestSignals.Noise(1, 0.2));

        // Act
        double quietNmr = PerceptualMetrics.Compare(tone, quiet).NoiseToMaskDb;
        double loudNmr = PerceptualMetrics.Compare(tone, loud).NoiseToMaskDb;

        // Assert
        Assert.True(loudNmr > quietNmr + 20, $"quiet {quietNmr:0.0} dB, loud {loudNmr:0.0} dB");
    }

    /// <summary>A quieter copy keeps its structure, so NSIM moves less than for a tone at another pitch.</summary>
    [Fact]
    public void Nsim_WithOtherPitch_IsFartherThanQuieterCopy()
    {
        // Arrange
        AudioSignal tone = TestSignals.Sine(440, 1);
        AudioSignal quieter = TestSignals.Sine(440, 1, amplitude: 0.25);
        AudioSignal other = TestSignals.Sine(1200, 1);

        // Act
        double quieterDistance = PerceptualMetrics.Compare(tone, quieter).NsimDistance;
        double otherDistance = PerceptualMetrics.Compare(tone, other).NsimDistance;

        // Assert
        Assert.True(otherDistance > quieterDistance + 0.1, $"quieter {quieterDistance:0.000}, other pitch {otherDistance:0.000}");
    }

    /// <summary>A 4 Hz tremolo is a modulation the steady tone lacks; a quieter steady tone has the same depth.</summary>
    [Fact]
    public void Modulation_WithTremolo_IsFartherThanQuieterCopy()
    {
        // Arrange
        AudioSignal steady = TestSignals.Sine(440, 2);
        AudioSignal quieter = TestSignals.Sine(440, 2, amplitude: 0.25);
        AudioSignal tremolo = Tremolo(steady, 4);

        // Act
        double quieterDistance = PerceptualMetrics.Compare(steady, quieter).ModulationDistanceDb;
        double tremoloDistance = PerceptualMetrics.Compare(steady, tremolo).ModulationDistanceDb;

        // Assert
        Assert.InRange(quieterDistance, 0, 1);
        Assert.True(tremoloDistance > 5, $"tremolo {tremoloDistance:0.0} dB");
    }

    /// <summary>A tone turned to noise loses its harmonicity; a tone at another pitch keeps it.</summary>
    [Fact]
    public void Harmonicity_WithNoise_IsFartherThanOtherPitch()
    {
        // Arrange
        AudioSignal tone = TestSignals.Sine(220, 1);
        AudioSignal other = TestSignals.Sine(330, 1);
        AudioSignal noise = TestSignals.Noise(1);

        // Act
        double otherDistance = PerceptualMetrics.Compare(tone, other).HarmonicityDistanceDb;
        double noiseDistance = PerceptualMetrics.Compare(tone, noise).HarmonicityDistanceDb;

        // Assert
        Assert.InRange(otherDistance, 0, 3);
        Assert.True(noiseDistance > 20, $"noise {noiseDistance:0.0} dB");
    }

    /// <summary>Signals at two sample rates are refused.</summary>
    [Fact]
    public void Compare_WithDifferentRates_Throws()
    {
        // Arrange
        AudioSignal a = TestSignals.Sine(440, 0.5);
        AudioSignal b = TestSignals.Sine(440, 0.5, rate: 44_100);

        // Act
        Exception? error = Record.Exception(() => PerceptualMetrics.Compare(a, b));

        // Assert
        Assert.IsType<ArgumentException>(error);
    }

    /// <summary>The frame is the power of two nearest 46 ms: 2048 points at 44.1 kHz, 1024 at 22.05 kHz.</summary>
    [Theory]
    [InlineData(44_100, 2048)]
    [InlineData(22_050, 1024)]
    [InlineData(48_000, 2048)]
    public void FftSize_AtRate_IsNearest46Ms(int rate, int expected)
    {
        // Act
        int sut = PerceptualMetrics.FftSize(rate);

        // Assert
        Assert.Equal(expected, sut);
    }

    /// <summary>ERB numbers follow Glasberg and Moore (1 kHz ≈ 15.6) and convert back to the same frequency.</summary>
    [Fact]
    public void ErbNumber_At1kHz_MatchesGlasbergMoore()
    {
        // Act
        double sut = ErbSpectrogram.ErbNumber(1000);

        // Assert
        Assert.Equal(15.62, sut, 2);
        Assert.Equal(1000, ErbSpectrogram.Hertz(sut), 6);
    }

    /// <summary>A full-scale sine's power, summed over the bands it falls in, is about 1.5: its peak bin is 1 (0 dB) and the Hann window spreads a quarter into each neighbour, as in the mel analysis. The silent second signal, sharing its FFT, stays at 0.</summary>
    [Fact]
    public void Power_WithFullScaleSine_SumsToHannEnergy()
    {
        // Arrange
        ErbSpectrogram sut = new(TestSignals.Rate, 1024, 256, 40);
        AudioSignal sine = TestSignals.Sine(1000, 0.5, amplitude: 1);

        // Act
        (double[][] power, double[][] silent) = sut.Power(sine.Samples, [], sine.Length);

        // Assert
        Assert.Equal(40, sut.Bands);
        Assert.InRange(power[power.Length / 2].Sum(), 1.4, 1.6);
        Assert.All(silent, frame => Assert.All(frame, p => Assert.Equal(0, p, 12)));
    }

    /// <summary>The sum of two signals of the same length and rate.</summary>
    internal static AudioSignal Mix(AudioSignal a, AudioSignal b) =>
        new([.. Enumerable.Range(0, a.Length).Select(i => a.Samples[i] + b.Samples[i])], a.SampleRate);

    /// <summary><paramref name="signal"/> with its level swinging fully at <paramref name="hertz"/>.</summary>
    private static AudioSignal Tremolo(AudioSignal signal, double hertz) =>
        new([.. Enumerable.Range(0, signal.Length).Select(i => (float)(signal.Samples[i] * 0.5 * (1 + Math.Sin(2 * Math.PI * hertz * i / signal.SampleRate))))], signal.SampleRate);
}
