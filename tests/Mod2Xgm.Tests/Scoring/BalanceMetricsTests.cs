using Mod2Xgm.Scoring;

namespace Mod2Xgm.Tests.Scoring;

/// <summary>Tests for the per-instrument balance measure (docs/adr/0051-instrument-balance-metric.md).</summary>
public sealed class BalanceMetricsTests
{
    /// <summary>The same stems on both sides are 0 dB apart, with no gain and no offsets.</summary>
    [Fact]
    public void IdenticalStemsAreBalanced()
    {
        (AudioSignal mix, InstrumentStem[] stems) = Song(1.0, 1.0);

        BalanceReport report = BalanceMetrics.Compare(mix, mix, stems);

        Assert.Equal(0, report.DistanceDb, 9);
        Assert.Equal(0, report.StaticDb, 9);
        Assert.Equal(0, report.MixRelativeDb, 9);
        Assert.Equal(0, report.GainDb, 9);
        Assert.All(report.Instruments, i => Assert.Equal(0, i.OffsetDb, 9));
        Assert.All(report.Instruments, i => Assert.True(i.Frames > 0));
    }

    /// <summary>Turning everything down by the same amount is a gain, not an imbalance.</summary>
    [Fact]
    public void AnOverallGainIsTakenOut()
    {
        (AudioSignal mix, InstrumentStem[] stems) = Song(1.0, 1.0);
        (AudioSignal quieter, InstrumentStem[] quieterStems) = Song(0.5, 0.5);

        BalanceReport report = BalanceMetrics.Compare(mix, quieter, Pair(stems, quieterStems));

        Assert.Equal(-6.02, report.GainDb, 1);
        Assert.Equal(0, report.DistanceDb, 1);
        Assert.Equal(0, report.MixRelativeDb, 1);
    }

    /// <summary>
    /// One instrument 6 dB too loud shows as a 6 dB gap between the two instruments' offsets. The overall gain follows the
    /// louder tone, so most of the gap shows on the noise.
    /// </summary>
    [Fact]
    public void OneInstrumentTooLoudIsSeen()
    {
        (AudioSignal mix, InstrumentStem[] stems) = Song(1.0, 1.0);
        (AudioSignal louder, InstrumentStem[] louderStems) = Song(2.0, 1.0);

        BalanceReport report = BalanceMetrics.Compare(mix, louder, Pair(stems, louderStems));

        InstrumentBalance tone = report.Instruments[0];
        InstrumentBalance noise = report.Instruments[1];
        Assert.Equal(6.02, tone.OffsetDb - noise.OffsetDb, 1);
        Assert.True(tone.OffsetDb > 0);
        Assert.True(noise.OffsetDb < 0);
        Assert.True(report.DistanceDb > 1.5);
        Assert.True(report.MixRelativeDb > 1.5);
        Assert.Equal(report.StaticDb, report.DistanceDb, 1);
    }

    /// <summary>A missing instrument costs about how far it sits above the audible floor, and no more.</summary>
    [Fact]
    public void AMissingInstrumentIsBounded()
    {
        (AudioSignal mix, InstrumentStem[] stems) = Song(1.0, 1.0);
        (AudioSignal withoutTone, InstrumentStem[] withoutToneStems) = Song(0.0, 1.0);

        BalanceReport report = BalanceMetrics.Compare(mix, withoutTone, Pair(stems, withoutToneStems));

        InstrumentBalance tone = report.Instruments[0];
        Assert.True(tone.OffsetDb < -20);
        Assert.True(tone.OffsetDb >= -BalanceMetrics.AudibleBelowMixDb - 7);
    }

    /// <summary>A stem silent on both sides counts no frames and doesn't move the distance.</summary>
    [Fact]
    public void SilentStemsCountNothing()
    {
        (AudioSignal mix, InstrumentStem[] stems) = Song(1.0, 1.0);
        AudioSignal silence = new(new float[mix.Length], mix.SampleRate);

        BalanceReport report = BalanceMetrics.Compare(mix, mix, [.. stems, new InstrumentStem(9, silence, silence)]);

        Assert.Equal(0, report.Instruments[2].Frames);
        Assert.Equal(BalanceMetrics.SilenceDb, report.Instruments[2].ReferenceLevelDb);
        Assert.Equal(0, report.DistanceDb, 9);
    }

    /// <summary>With no audible stem at all, every number is 0.</summary>
    [Fact]
    public void NoStemsScoreZero()
    {
        (AudioSignal mix, _) = Song(1.0, 1.0);

        BalanceReport report = BalanceMetrics.Compare(mix, mix, []);

        Assert.Equal(0, report.DistanceDb);
        Assert.Empty(report.Instruments);
    }

    /// <summary>A candidate shorter than the reference is padded with silence: what's missing at the end costs.</summary>
    [Fact]
    public void AShortCandidateIsPaddedWithSilence()
    {
        (AudioSignal mix, InstrumentStem[] stems) = Song(1.0, 1.0);
        AudioSignal half = new(mix.Samples[..(mix.Length / 2)].ToArray(), mix.SampleRate);
        InstrumentStem[] halves = [.. stems.Select(s => s with { Candidate = new AudioSignal(s.Candidate.Samples[..(s.Candidate.Length / 2)].ToArray(), s.Candidate.SampleRate) })];

        BalanceReport report = BalanceMetrics.Compare(mix, half, halves);

        Assert.True(report.DistanceDb > 5);
    }

    /// <summary>Stems at another rate than the mixes are refused.</summary>
    [Fact]
    public void StemsAtAnotherRateAreRefused()
    {
        (AudioSignal mix, _) = Song(1.0, 1.0);
        AudioSignal other = TestSignals.Sine(440, 1, rate: 11025);

        Assert.Throws<ArgumentException>(() => BalanceMetrics.Compare(mix, mix, [new InstrumentStem(1, other, other)]));
        Assert.Throws<ArgumentNullException>(() => BalanceMetrics.Compare(mix, mix, null!));
    }

    /// <summary>One level per 100 ms once the first 400 ms window fits, and one level for anything shorter.</summary>
    [Fact]
    public void LevelsComeEveryHundredMilliseconds()
    {
        AudioSignal second = TestSignals.Sine(440, 1);

        Assert.Equal(7, BalanceMetrics.Levels(second, second.Length).Length);
        Assert.Single(BalanceMetrics.Levels(second, second.Length / 10));
        Assert.Equal(BalanceMetrics.SilenceDb, BalanceMetrics.Levels(new AudioSignal(new float[100], TestSignals.Rate), 100)[0]);
    }

    /// <summary>K-weighting lifts high frequencies by about 4 dB and cuts deep bass, as BS.1770 specifies.</summary>
    [Fact]
    public void KWeightingFollowsTheStandard()
    {
        const int Rate = 48000;
        double Level(double hertz)
        {
            AudioSignal sine = TestSignals.Sine(hertz, 1, rate: Rate);
            return BalanceMetrics.Levels(sine, sine.Length)[^1];
        }

        double reference = Level(1000);
        Assert.InRange(Level(10000) - reference, 3.3, 4.3);
        Assert.True(Level(20) - reference < -10);

        // A full-scale-ish 1 kHz sine of amplitude 0.5 has power 0.125 (−9.03 dB); K-weighting adds about 0.7 dB there.
        Assert.InRange(reference, -9.03, -7.8);
    }

    /// <summary>Stems that add up to the mix match its loudness; a missing stem shows where it played.</summary>
    [Fact]
    public void SumErrorShowsWhatTheStemsMiss()
    {
        (AudioSignal mix, InstrumentStem[] stems) = Song(1.0, 1.0);
        AudioSignal silence = new(new float[mix.Length], mix.SampleRate);

        Assert.InRange(BalanceMetrics.SumErrorDb(mix, stems.Select(s => s.Reference)), 0, 0.1);
        double withoutNoise = BalanceMetrics.SumErrorDb(mix, [stems[0].Reference]);
        Assert.InRange(withoutNoise, 0.2, 1.5);
        Assert.InRange(BalanceMetrics.SumErrorDb(mix, []), 25, 31);
        Assert.Equal(0, BalanceMetrics.SumErrorDb(silence, [silence]));
        Assert.Throws<ArgumentNullException>(() => BalanceMetrics.SumErrorDb(mix, null!));
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
