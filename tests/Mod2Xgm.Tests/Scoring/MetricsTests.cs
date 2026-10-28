using Mod2Xgm.Scoring;

namespace Mod2Xgm.Tests.Scoring;

/// <summary>Tests for the spectral and note-start metrics and the alignment (plan T6.2).</summary>
public sealed class MetricsTests
{
    private static readonly double[] Starts = [0.5, 1.0, 1.5, 2.0, 2.5];

    /// <summary>Identical signals are 0 apart by both spectral metrics, and the distance is symmetric.</summary>
    [Fact]
    public void IdenticalSignalsAreZeroApart()
    {
        AudioSignal a = TestSignals.Noise(1);
        AudioSignal b = TestSignals.Sine(440, 1);

        Assert.Equal(0, SpectralMetrics.MelDistanceDb(a, a));
        Assert.Equal(0, SpectralMetrics.MfccDistance(a, a));
        Assert.Equal(SpectralMetrics.MelDistanceDb(a, b), SpectralMetrics.MelDistanceDb(b, a), 9);
    }

    /// <summary>Halving white noise is 6.02 dB quieter in every band, so the mel distance is 6.02 dB.</summary>
    [Fact]
    public void HalvingTheLevelIsAboutSixDecibels()
    {
        AudioSignal noise = TestSignals.Noise(1);

        double distance = SpectralMetrics.MelDistanceDb(noise, TestSignals.Scaled(noise, 0.5));

        Assert.Equal(6.02, distance, 1);
    }

    /// <summary>The mel distance grows as the sound gets further away.</summary>
    [Fact]
    public void FurtherSoundsScoreWorse()
    {
        AudioSignal reference = TestSignals.Sine(440, 1);

        double slightlyQuieter = SpectralMetrics.MelDistanceDb(reference, TestSignals.Scaled(reference, 0.9));
        double nearbyPitch = SpectralMetrics.MelDistanceDb(reference, TestSignals.Sine(466, 1));
        double otherPitch = SpectralMetrics.MelDistanceDb(reference, TestSignals.Sine(1760, 1));
        double noise = SpectralMetrics.MelDistanceDb(reference, TestSignals.Noise(1));

        Assert.True(slightlyQuieter < nearbyPitch);
        Assert.True(nearbyPitch < otherPitch);
        Assert.True(otherPitch < noise);
    }

    /// <summary>Music that is missing from the end of the candidate is penalized.</summary>
    [Fact]
    public void ACutOffCandidateScoresWorse()
    {
        AudioSignal reference = TestSignals.Noise(2);
        AudioSignal half = new(reference.Samples[..(reference.Length / 2)].ToArray(), reference.SampleRate);

        Assert.True(SpectralMetrics.MelDistanceDb(reference, half) > 20);
        Assert.True(SpectralMetrics.MfccDistance(reference, half) > 0);
    }

    /// <summary>MFCCs leave out overall loudness, so a level change leaves them alone while a timbre change does not.</summary>
    [Fact]
    public void MfccFollowsTimbreNotLoudness()
    {
        AudioSignal noise = TestSignals.Noise(1);

        double quieter = SpectralMetrics.MfccDistance(noise, TestSignals.Scaled(noise, 0.5));
        double otherTimbre = SpectralMetrics.MfccDistance(noise, TestSignals.Sine(440, 1));

        Assert.True(quieter < 0.01, $"quieter noise: {quieter}");
        Assert.True(otherTimbre > 10, $"sine vs noise: {otherTimbre}");
    }

    /// <summary>Signals at different sample rates can't be compared, and an empty list of scales is refused.</summary>
    [Fact]
    public void MismatchedInputsAreRefused()
    {
        AudioSignal a = TestSignals.Sine(440, 0.2);
        AudioSignal b = TestSignals.Sine(440, 0.2, rate: 44100);

        Assert.Throws<ArgumentException>(() => SpectralMetrics.MelDistanceDb(a, b));
        Assert.Throws<ArgumentException>(() => SpectralMetrics.MfccDistance(a, b));
        Assert.Throws<ArgumentException>(() => SpectralMetrics.MelDistanceDb(a, a, []));
    }

    /// <summary>Tone bursts are found where they start, within a few milliseconds.</summary>
    [Fact]
    public void BurstsAreFoundWhereTheyStart()
    {
        IReadOnlyList<double> found = OnsetMetrics.Detect(TestSignals.Bursts(Starts, 3));

        Assert.Equal(Starts.Length, found.Count);
        for (int i = 0; i < Starts.Length; i++)
        {
            Assert.InRange(found[i] - Starts[i], -0.02, 0.03);
        }
    }

    /// <summary>Silence and a steady tone have no note starts.</summary>
    [Fact]
    public void NothingStartsInSilenceOrASteadyTone()
    {
        Assert.Empty(OnsetMetrics.Detect(new AudioSignal(new float[22050], TestSignals.Rate)));
        Assert.Empty(OnsetMetrics.Detect(TestSignals.Sine(440, 2)));
        Assert.Equal(256.0 / TestSignals.Rate, OnsetMetrics.CurveStepSeconds(TestSignals.Rate), 12);
    }

    /// <summary>The same note starts match perfectly; both lists empty is also a perfect match.</summary>
    [Fact]
    public void EqualOnsetListsMatchPerfectly()
    {
        OnsetComparison same = OnsetMetrics.Compare(Starts, Starts);

        Assert.Equal(1, same.F1);
        Assert.Equal(0, same.MeanTimingErrorSeconds);
        Assert.Equal(Starts.Length, same.Matched);
        Assert.Equal(1, OnsetMetrics.Compare([], []).F1);
        Assert.Null(OnsetMetrics.Compare([], []).MeanTimingErrorSeconds);
    }

    /// <summary>A small delay still matches and is reported as timing error; a large one doesn't match.</summary>
    [Fact]
    public void LateOnsetsAreMeasuredOrMissed()
    {
        double[] late20 = [.. Starts.Select(s => s + 0.02)];
        double[] late100 = [.. Starts.Select(s => s + 0.1)];

        OnsetComparison near = OnsetMetrics.Compare(Starts, late20);
        OnsetComparison far = OnsetMetrics.Compare(Starts, late100);

        Assert.Equal(1, near.F1);
        Assert.Equal(0.02, near.MeanTimingErrorSeconds!.Value, 9);
        Assert.Equal(0, far.F1);
        Assert.Null(far.MeanTimingErrorSeconds);
    }

    /// <summary>Missing notes lower recall; invented notes lower precision.</summary>
    [Fact]
    public void MissingAndExtraOnsetsAreCounted()
    {
        OnsetComparison missing = OnsetMetrics.Compare(Starts, [Starts[0], Starts[2], Starts[4]]);
        OnsetComparison extra = OnsetMetrics.Compare([Starts[0]], Starts);

        Assert.Equal(1, missing.Precision);
        Assert.Equal(0.6, missing.Recall, 9);
        Assert.Equal(1, extra.Recall);
        Assert.Equal(0.2, extra.Precision, 9);
        Assert.Equal(0, OnsetMetrics.Compare([], Starts).F1);
        Assert.Equal(0, OnsetMetrics.Compare(Starts, []).F1);
    }

    /// <summary>The lag between two renderings is found, late or early, to within a curve step.</summary>
    [Theory]
    [InlineData(0.25)]
    [InlineData(-0.25)]
    [InlineData(0.0)]
    public void TheLagIsFound(double delaySeconds)
    {
        AudioSignal reference = TestSignals.Bursts(Starts, 3.5);
        AudioSignal candidate = TestSignals.Bursts(Starts, 3.5, delaySeconds: delaySeconds);

        int lag = Alignment.EstimateLagSamples(reference, candidate, 1.0);

        double expected = delaySeconds * TestSignals.Rate;
        Assert.InRange(lag, expected - 256, expected + 256);
    }

    /// <summary>Aligning removes the lag, so the aligned candidate's note starts match the original's.</summary>
    [Fact]
    public void AligningLinesTheNotesUp()
    {
        AudioSignal reference = TestSignals.Bursts(Starts, 3.5);
        AudioSignal candidate = TestSignals.Bursts(Starts, 3.5, delaySeconds: 0.3);

        AudioSignal aligned = Alignment.Align(reference, candidate, 1.0, out int lag);

        Assert.True(lag > 0);
        Assert.Equal(1, OnsetMetrics.Compare(OnsetMetrics.Detect(reference), OnsetMetrics.Detect(aligned)).F1);
    }

    /// <summary>A lag beyond the search range is not found, and signals with no notes are left alone.</summary>
    [Fact]
    public void LagsBeyondTheRangeAndSilenceGiveZero()
    {
        AudioSignal reference = TestSignals.Bursts(Starts, 3.5);
        AudioSignal silent = new(new float[reference.Length], reference.SampleRate);

        Assert.Equal(0, Alignment.EstimateLagSamples(reference, silent, 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Alignment.EstimateLagSamples(reference, reference, -1));
    }
}
