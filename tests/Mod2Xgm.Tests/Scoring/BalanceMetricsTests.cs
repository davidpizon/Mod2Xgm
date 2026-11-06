using Mod2Xgm.Scoring;

namespace Mod2Xgm.Tests.Scoring;

/// <summary>Tests for the per-instrument balance measure (docs/adr/0051-instrument-balance-metric.md).</summary>
public sealed class BalanceMetricsTests
{
    /// <summary>The same stems on both sides are 0 dB apart, with no gain and no offsets; each instrument keeps its id.</summary>
    [Fact]
    public void Compare_WithIdenticalStems_IsBalanced()
    {
        // Arrange
        (AudioSignal mix, InstrumentStem[] stems) = Song(1.0, 1.0);

        // Act
        BalanceReport report = BalanceMetrics.Compare(mix, mix, stems);

        // Assert
        Assert.Equal(0, report.DistanceDb, 9);
        Assert.Equal(0, report.StaticDb, 9);
        Assert.Equal(0, report.MixRelativeDb, 9);
        Assert.Equal(0, report.GainDb, 9);
        Assert.Equal([1, 2], report.Instruments.Select(i => i.Id));
        Assert.All(report.Instruments, i => Assert.Equal(0, i.OffsetDb, 9));
        Assert.All(report.Instruments, i => Assert.True(i.Frames > 0, $"instrument {i.Id} has no frames"));
    }

    /// <summary>Turning everything down by the same amount is a gain, not an imbalance.</summary>
    [Fact]
    public void Compare_WithOverallGain_TakesItOut()
    {
        // Arrange
        (AudioSignal mix, InstrumentStem[] stems) = Song(1.0, 1.0);
        (AudioSignal quieter, InstrumentStem[] quieterStems) = Song(0.5, 0.5);

        // Act
        BalanceReport report = BalanceMetrics.Compare(mix, quieter, Pair(stems, quieterStems));

        // Assert
        Assert.Equal(-6.02, report.GainDb, 1);
        Assert.Equal(0, report.DistanceDb, 1);
        Assert.Equal(0, report.MixRelativeDb, 1);
    }

    /// <summary>
    /// One instrument 6 dB too loud shows as a 6 dB gap between the two instruments' offsets. The overall gain follows the
    /// louder tone, so most of the gap shows on the noise.
    /// </summary>
    [Fact]
    public void Compare_WithOneInstrumentTooLoud_ShowsGapBetweenOffsets()
    {
        // Arrange
        (AudioSignal mix, InstrumentStem[] stems) = Song(1.0, 1.0);
        (AudioSignal louder, InstrumentStem[] louderStems) = Song(2.0, 1.0);

        // Act
        BalanceReport report = BalanceMetrics.Compare(mix, louder, Pair(stems, louderStems));

        // Assert
        InstrumentBalance tone = report.Instruments[0];
        InstrumentBalance noise = report.Instruments[1];
        Assert.Equal(6.02, tone.OffsetDb - noise.OffsetDb, 1);
        Assert.True(tone.OffsetDb > 0, $"tone offset {tone.OffsetDb}");
        Assert.True(noise.OffsetDb < 0, $"noise offset {noise.OffsetDb}");
        Assert.True(report.DistanceDb > 1.5, $"distance {report.DistanceDb}");
        Assert.True(report.MixRelativeDb > 1.5, $"mix-relative {report.MixRelativeDb}");
        Assert.Equal(report.StaticDb, report.DistanceDb, 1);
    }

    /// <summary>A missing instrument costs about how far it sits above the audible floor, and no more.</summary>
    [Fact]
    public void Compare_WithMissingInstrument_BoundsItsOffset()
    {
        // Arrange
        (AudioSignal mix, InstrumentStem[] stems) = Song(1.0, 1.0);
        (AudioSignal withoutTone, InstrumentStem[] withoutToneStems) = Song(0.0, 1.0);

        // Act
        BalanceReport report = BalanceMetrics.Compare(mix, withoutTone, Pair(stems, withoutToneStems));

        // Assert
        Assert.InRange(report.Instruments[0].OffsetDb, -BalanceMetrics.AudibleBelowMixDb - 7, -20);
    }

    /// <summary>A stem silent on both sides counts no frames and doesn't move the distance.</summary>
    [Fact]
    public void Compare_WithSilentStem_CountsNothing()
    {
        // Arrange
        (AudioSignal mix, InstrumentStem[] stems) = Song(1.0, 1.0);
        AudioSignal silence = new(new float[mix.Length], mix.SampleRate);

        // Act
        BalanceReport report = BalanceMetrics.Compare(mix, mix, [.. stems, new InstrumentStem(9, silence, silence)]);

        // Assert
        Assert.Equal(0, report.Instruments[2].Frames);
        Assert.Equal(BalanceMetrics.SilenceDb, report.Instruments[2].ReferenceLevelDb);
        Assert.Equal(0, report.DistanceDb, 9);
    }

    /// <summary>With no audible stem at all, every number is 0.</summary>
    [Fact]
    public void Compare_WithNoStems_ScoresZero()
    {
        // Arrange
        (AudioSignal mix, _) = Song(1.0, 1.0);

        // Act
        BalanceReport report = BalanceMetrics.Compare(mix, mix, []);

        // Assert
        Assert.Equal(0, report.DistanceDb);
        Assert.Empty(report.Instruments);
    }

    /// <summary>A candidate shorter than the reference is padded with silence: what's missing at the end costs.</summary>
    [Fact]
    public void Compare_WithShortCandidate_PadsWithSilence()
    {
        // Arrange
        (AudioSignal mix, InstrumentStem[] stems) = Song(1.0, 1.0);
        AudioSignal half = new(mix.Samples[..(mix.Length / 2)].ToArray(), mix.SampleRate);
        InstrumentStem[] halves = [.. stems.Select(s => s with { Candidate = new AudioSignal(s.Candidate.Samples[..(s.Candidate.Length / 2)].ToArray(), s.Candidate.SampleRate) })];

        // Act
        BalanceReport report = BalanceMetrics.Compare(mix, half, halves);

        // Assert
        Assert.True(report.DistanceDb > 5, $"distance {report.DistanceDb}");
    }

    /// <summary>Stems at another rate than the mixes are refused.</summary>
    [Fact]
    public void Compare_WithStemsAtOtherRate_Throws()
    {
        // Arrange
        (AudioSignal mix, _) = Song(1.0, 1.0);
        AudioSignal other = TestSignals.Sine(440, 1, rate: 11025);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => BalanceMetrics.Compare(mix, mix, [new InstrumentStem(1, other, other)]));
    }

    /// <summary>A null list of stems is refused.</summary>
    [Fact]
    public void Compare_WithNullStems_Throws()
    {
        // Arrange
        (AudioSignal mix, _) = Song(1.0, 1.0);

        // Act & Assert: the null is what this test is about.
        Assert.Throws<ArgumentNullException>(() => BalanceMetrics.Compare(mix, mix, null!));
    }

    /// <summary>One level per 100 ms once the first 400 ms window fits.</summary>
    [Fact]
    public void Levels_WithOneSecond_ReturnsOneLevelPer100Ms()
    {
        // Arrange
        AudioSignal second = TestSignals.Sine(440, 1);

        // Act
        double[] levels = BalanceMetrics.Levels(second, second.Length);

        // Assert
        Assert.Equal(7, levels.Length);
    }

    /// <summary>Audio shorter than the first window still has one level.</summary>
    [Fact]
    public void Levels_WithLessThanOneWindow_ReturnsOneLevel()
    {
        // Arrange
        AudioSignal second = TestSignals.Sine(440, 1);

        // Act
        double[] levels = BalanceMetrics.Levels(second, second.Length / 10);

        // Assert
        Assert.Single(levels);
    }

    /// <summary>Silence measures at the floor.</summary>
    [Fact]
    public void Levels_WithSilence_ReturnsFloor()
    {
        // Arrange
        AudioSignal silence = new(new float[100], TestSignals.Rate);

        // Act
        double[] levels = BalanceMetrics.Levels(silence, 100);

        // Assert
        Assert.Equal(BalanceMetrics.SilenceDb, levels[0]);
    }

    /// <summary>K-weighting lifts high frequencies by about 4 dB, as BS.1770 specifies.</summary>
    [Fact]
    public void Levels_WithHighTone_LiftsItByAboutFourDb()
    {
        // Act
        double difference = KWeightedLevel(10000) - KWeightedLevel(1000);

        // Assert
        Assert.InRange(difference, 3.3, 4.3);
    }

    /// <summary>K-weighting cuts deep bass, as BS.1770 specifies.</summary>
    [Fact]
    public void Levels_WithDeepBass_CutsIt()
    {
        // Act
        double difference = KWeightedLevel(20) - KWeightedLevel(1000);

        // Assert
        Assert.True(difference < -10, $"{difference} dB");
    }

    /// <summary>A 1 kHz sine of amplitude 0.5 has power 0.125 (−9.03 dB); K-weighting adds about 0.7 dB there.</summary>
    [Fact]
    public void Levels_WithReferenceTone_MeasuresItsPower()
    {
        // Act
        double level = KWeightedLevel(1000);

        // Assert
        Assert.InRange(level, -9.03, -7.8);
    }

    /// <summary>Stems that add up to the mix match its loudness.</summary>
    [Fact]
    public void SumErrorDb_WithAllStems_IsNearZero()
    {
        // Arrange
        (AudioSignal mix, InstrumentStem[] stems) = Song(1.0, 1.0);

        // Act
        double error = BalanceMetrics.SumErrorDb(mix, stems.Select(s => s.Reference));

        // Assert
        Assert.InRange(error, 0, 0.1);
    }

    /// <summary>A missing stem shows where it played.</summary>
    [Fact]
    public void SumErrorDb_WithMissingStem_ShowsItsPart()
    {
        // Arrange
        (AudioSignal mix, InstrumentStem[] stems) = Song(1.0, 1.0);

        // Act
        double error = BalanceMetrics.SumErrorDb(mix, [stems[0].Reference]);

        // Assert
        Assert.InRange(error, 0.2, 1.5);
    }

    /// <summary>No stems at all are far from the mix.</summary>
    [Fact]
    public void SumErrorDb_WithNoStems_IsLarge()
    {
        // Arrange
        (AudioSignal mix, _) = Song(1.0, 1.0);

        // Act
        double error = BalanceMetrics.SumErrorDb(mix, []);

        // Assert
        Assert.InRange(error, 25, 31);
    }

    /// <summary>Silent stems match a silent mix.</summary>
    [Fact]
    public void SumErrorDb_WithSilence_IsZero()
    {
        // Arrange
        AudioSignal silence = new(new float[TestSignals.Rate], TestSignals.Rate);

        // Act
        double error = BalanceMetrics.SumErrorDb(silence, [silence]);

        // Assert
        Assert.Equal(0, error);
    }

    /// <summary>A null list of stems is refused.</summary>
    [Fact]
    public void SumErrorDb_WithNullStems_Throws()
    {
        // Arrange
        (AudioSignal mix, _) = Song(1.0, 1.0);

        // Act & Assert: the null is what this test is about.
        Assert.Throws<ArgumentNullException>(() => BalanceMetrics.SumErrorDb(mix, null!));
    }

    /// <summary>The last level of a one-second sine of <paramref name="hertz"/> at 48 kHz.</summary>
    private static double KWeightedLevel(double hertz)
    {
        AudioSignal sine = TestSignals.Sine(hertz, 1, rate: 48000);
        return BalanceMetrics.Levels(sine, sine.Length)[^1];
    }

    /// <summary>
    /// A two-instrument song, 2 s: a 440 Hz tone all the way through and noise in the second second, each scaled. Both
    /// sides of each stem are the same audio; <see cref="Pair"/> combines two songs into reference and candidate.
    /// </summary>
    private static (AudioSignal Mix, InstrumentStem[] Stems) Song(double toneGain, double noiseGain)
    {
        AudioSignal tone = TestSignals.Scaled(TestSignals.Sine(440, 2, 0.3), toneGain);
        AudioSignal noise = TestSignals.Noise(2, 0.2);
        float[] noiseLater = new float[noise.Length];
        for (int i = noise.Length / 2; i < noise.Length; i++)
        {
            noiseLater[i] = (float)(noise.Samples[i] * noiseGain);
        }

        AudioSignal noiseStem = new(noiseLater, noise.SampleRate);
        float[] mix = new float[tone.Length];
        for (int i = 0; i < mix.Length; i++)
        {
            mix[i] = tone.Samples[i] + noiseLater[i];
        }

        return (new AudioSignal(mix, tone.SampleRate), [new InstrumentStem(1, tone, tone), new InstrumentStem(2, noiseStem, noiseStem)]);
    }

    /// <summary>Stems with the reference side from <paramref name="reference"/> and the candidate side from <paramref name="candidate"/>.</summary>
    private static InstrumentStem[] Pair(InstrumentStem[] reference, InstrumentStem[] candidate) =>
        [.. reference.Zip(candidate, (r, c) => new InstrumentStem(r.Id, r.Reference, c.Candidate))];
}
