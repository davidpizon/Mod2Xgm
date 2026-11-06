using Mod2Xgm.Scoring;

namespace Mod2Xgm.Tests.Scoring;

/// <summary>Tests for the spectral and note-start metrics and the alignment (plan T6.2).</summary>
public sealed class MetricsTests
{
    private static readonly double[] Starts = [0.5, 1.0, 1.5, 2.0, 2.5];

    /// <summary>Identical signals are 0 apart by the mel distance.</summary>
    [Fact]
    public void MelDistanceDb_WithIdenticalSignals_ReturnsZero()
    {
        // Arrange
        AudioSignal noise = TestSignals.Noise(1);

        // Act
        double distance = SpectralMetrics.MelDistanceDb(noise, noise);

        // Assert
        Assert.Equal(0, distance);
    }

    /// <summary>Identical signals are 0 apart by the MFCC distance.</summary>
    [Fact]
    public void MfccDistance_WithIdenticalSignals_ReturnsZero()
    {
        // Arrange
        AudioSignal noise = TestSignals.Noise(1);

        // Act
        double distance = SpectralMetrics.MfccDistance(noise, noise);

        // Assert
        Assert.Equal(0, distance);
    }

    /// <summary>The mel distance is the same in both directions.</summary>
    [Fact]
    public void MelDistanceDb_WithSwappedSignals_IsSymmetric()
    {
        // Arrange
        AudioSignal noise = TestSignals.Noise(1);
        AudioSignal sine = TestSignals.Sine(440, 1);

        // Act
        double forth = SpectralMetrics.MelDistanceDb(noise, sine);
        double back = SpectralMetrics.MelDistanceDb(sine, noise);

        // Assert
        Assert.Equal(forth, back, 9);
    }

    /// <summary>Halving white noise is 6.02 dB quieter in every band, so the mel distance is 6.02 dB.</summary>
    [Fact]
    public void MelDistanceDb_WithHalvedLevel_ReturnsAboutSixDb()
    {
        // Arrange
        AudioSignal noise = TestSignals.Noise(1);

        // Act
        double distance = SpectralMetrics.MelDistanceDb(noise, TestSignals.Scaled(noise, 0.5));

        // Assert
        Assert.Equal(6.02, distance, 1);
    }

    /// <summary>The mel distance grows as the sound gets further away.</summary>
    [Fact]
    public void MelDistanceDb_WithFurtherSounds_Grows()
    {
        // Arrange
        AudioSignal reference = TestSignals.Sine(440, 1);

        // Act
        double slightlyQuieter = SpectralMetrics.MelDistanceDb(reference, TestSignals.Scaled(reference, 0.9));
        double nearbyPitch = SpectralMetrics.MelDistanceDb(reference, TestSignals.Sine(466, 1));
        double otherPitch = SpectralMetrics.MelDistanceDb(reference, TestSignals.Sine(1760, 1));
        double noise = SpectralMetrics.MelDistanceDb(reference, TestSignals.Noise(1));

        // Assert
        Assert.True(slightlyQuieter < nearbyPitch, $"{slightlyQuieter} vs {nearbyPitch}");
        Assert.True(nearbyPitch < otherPitch, $"{nearbyPitch} vs {otherPitch}");
        Assert.True(otherPitch < noise, $"{otherPitch} vs {noise}");
    }

    /// <summary>Music that is missing from the end of the candidate is penalized.</summary>
    [Fact]
    public void MelDistanceDb_WithCutOffCandidate_ScoresWorse()
    {
        // Arrange
        AudioSignal reference = TestSignals.Noise(2);
        AudioSignal half = new(reference.Samples[..(reference.Length / 2)].ToArray(), reference.SampleRate);

        // Act
        double distance = SpectralMetrics.MelDistanceDb(reference, half);

        // Assert
        Assert.True(distance > 20, $"{distance} dB");
    }

    /// <summary>The MFCC distance also penalizes music missing from the end.</summary>
    [Fact]
    public void MfccDistance_WithCutOffCandidate_ScoresWorse()
    {
        // Arrange
        AudioSignal reference = TestSignals.Noise(2);
        AudioSignal half = new(reference.Samples[..(reference.Length / 2)].ToArray(), reference.SampleRate);

        // Act
        double distance = SpectralMetrics.MfccDistance(reference, half);

        // Assert
        Assert.True(distance > 0, $"{distance}");
    }

    /// <summary>MFCCs leave out overall loudness, so a level change leaves them alone.</summary>
    [Fact]
    public void MfccDistance_WithLevelChange_StaysNearZero()
    {
        // Arrange
        AudioSignal noise = TestSignals.Noise(1);

        // Act
        double distance = SpectralMetrics.MfccDistance(noise, TestSignals.Scaled(noise, 0.5));

        // Assert
        Assert.InRange(distance, 0, 0.01);
    }

    /// <summary>MFCCs follow timbre: a sine is far from noise.</summary>
    [Fact]
    public void MfccDistance_WithOtherTimbre_IsLarge()
    {
        // Arrange
        AudioSignal noise = TestSignals.Noise(1);

        // Act
        double distance = SpectralMetrics.MfccDistance(noise, TestSignals.Sine(440, 1));

        // Assert
        Assert.True(distance > 10, $"{distance}");
    }

    /// <summary>Signals at different sample rates can't be compared by the mel distance.</summary>
    [Fact]
    public void MelDistanceDb_WithDifferentRates_Throws()
    {
        // Arrange
        AudioSignal a = TestSignals.Sine(440, 0.2);
        AudioSignal b = TestSignals.Sine(440, 0.2, rate: 44100);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => SpectralMetrics.MelDistanceDb(a, b));
    }

    /// <summary>Signals at different sample rates can't be compared by the MFCC distance.</summary>
    [Fact]
    public void MfccDistance_WithDifferentRates_Throws()
    {
        // Arrange
        AudioSignal a = TestSignals.Sine(440, 0.2);
        AudioSignal b = TestSignals.Sine(440, 0.2, rate: 44100);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => SpectralMetrics.MfccDistance(a, b));
    }

    /// <summary>An empty list of scales is refused.</summary>
    [Fact]
    public void MelDistanceDb_WithNoScales_Throws()
    {
        // Arrange
        AudioSignal a = TestSignals.Sine(440, 0.2);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => SpectralMetrics.MelDistanceDb(a, a, []));
    }

    /// <summary>Tone bursts are found where they start, within a few milliseconds.</summary>
    [Fact]
    public void Detect_WithBursts_FindsTheirStarts()
    {
        // Arrange
        AudioSignal bursts = TestSignals.Bursts(Starts, 3);

        // Act
        IReadOnlyList<double> found = OnsetMetrics.Detect(bursts);

        // Assert
        Assert.Equal(Starts.Length, found.Count);
        Assert.All(found.Zip(Starts), pair => Assert.InRange(pair.First - pair.Second, -0.02, 0.03));
    }

    /// <summary>Silence has no note starts.</summary>
    [Fact]
    public void Detect_WithSilence_FindsNothing()
    {
        // Arrange
        AudioSignal silence = new(new float[22050], TestSignals.Rate);

        // Act
        IReadOnlyList<double> found = OnsetMetrics.Detect(silence);

        // Assert
        Assert.Empty(found);
    }

    /// <summary>A steady tone has no note starts.</summary>
    [Fact]
    public void Detect_WithSteadyTone_FindsNothing()
    {
        // Arrange
        AudioSignal tone = TestSignals.Sine(440, 2);

        // Act
        IReadOnlyList<double> found = OnsetMetrics.Detect(tone);

        // Assert
        Assert.Empty(found);
    }

    /// <summary>The onset curve moves in steps of 256 samples.</summary>
    [Fact]
    public void CurveStepSeconds_WithRate_Is256Samples()
    {
        // Act
        double step = OnsetMetrics.CurveStepSeconds(TestSignals.Rate);

        // Assert
        Assert.Equal(256.0 / TestSignals.Rate, step, 12);
    }

    /// <summary>The same note starts match perfectly.</summary>
    [Fact]
    public void Compare_WithEqualOnsets_MatchesPerfectly()
    {
        // Act
        OnsetComparison result = OnsetMetrics.Compare(Starts, Starts);

        // Assert
        Assert.Equal(1, result.F1);
        Assert.Equal(0, result.MeanTimingErrorSeconds);
        Assert.Equal(Starts.Length, result.Matched);
    }

    /// <summary>Both lists empty is also a perfect match, with no timing to report.</summary>
    [Fact]
    public void Compare_WithBothEmpty_MatchesPerfectly()
    {
        // Act
        OnsetComparison result = OnsetMetrics.Compare([], []);

        // Assert
        Assert.Equal(1, result.F1);
        Assert.Null(result.MeanTimingErrorSeconds);
    }

    /// <summary>A small delay still matches and is reported as timing error.</summary>
    [Fact]
    public void Compare_WithSmallDelay_MeasuresTimingError()
    {
        // Arrange
        double[] late = [.. Starts.Select(s => s + 0.02)];

        // Act
        OnsetComparison result = OnsetMetrics.Compare(Starts, late);

        // Assert
        Assert.Equal(1, result.F1);
        Assert.Equal(0.02, result.MeanTimingErrorSeconds ?? -1, 9);
    }

    /// <summary>A large delay doesn't match.</summary>
    [Fact]
    public void Compare_WithLargeDelay_MatchesNothing()
    {
        // Arrange
        double[] late = [.. Starts.Select(s => s + 0.1)];

        // Act
        OnsetComparison result = OnsetMetrics.Compare(Starts, late);

        // Assert
        Assert.Equal(0, result.F1);
        Assert.Null(result.MeanTimingErrorSeconds);
    }

    /// <summary>Missing notes lower recall.</summary>
    [Fact]
    public void Compare_WithMissingOnsets_LowersRecall()
    {
        // Act
        OnsetComparison result = OnsetMetrics.Compare(Starts, [Starts[0], Starts[2], Starts[4]]);

        // Assert
        Assert.Equal(1, result.Precision);
        Assert.Equal(0.6, result.Recall, 9);
    }

    /// <summary>Invented notes lower precision.</summary>
    [Fact]
    public void Compare_WithExtraOnsets_LowersPrecision()
    {
        // Act
        OnsetComparison result = OnsetMetrics.Compare([Starts[0]], Starts);

        // Assert
        Assert.Equal(1, result.Recall);
        Assert.Equal(0.2, result.Precision, 9);
    }

    /// <summary>One empty list against notes scores 0.</summary>
    [Fact]
    public void Compare_WithOneSideEmpty_ScoresZero()
    {
        // Act
        OnsetComparison noReference = OnsetMetrics.Compare([], Starts);
        OnsetComparison noCandidate = OnsetMetrics.Compare(Starts, []);

        // Assert
        Assert.Equal((0.0, 0.0), (noReference.F1, noCandidate.F1));
    }

    /// <summary>The lag between two renderings is found, late or early, to within a curve step.</summary>
    [Theory]
    [InlineData(0.25)]
    [InlineData(-0.25)]
    [InlineData(0.0)]
    public void EstimateLagSamples_WithDelay_FindsIt(double delaySeconds)
    {
        // Arrange
        AudioSignal reference = TestSignals.Bursts(Starts, 3.5);
        AudioSignal candidate = TestSignals.Bursts(Starts, 3.5, delaySeconds: delaySeconds);
        double expected = delaySeconds * TestSignals.Rate;

        // Act
        int lag = Alignment.EstimateLagSamples(reference, candidate, 1.0);

        // Assert
        Assert.InRange(lag, expected - 256, expected + 256);
    }

    /// <summary>Aligning removes the lag, so the aligned candidate's note starts match the original's.</summary>
    [Fact]
    public void Align_WithLateCandidate_LinesNotesUp()
    {
        // Arrange
        AudioSignal reference = TestSignals.Bursts(Starts, 3.5);
        AudioSignal candidate = TestSignals.Bursts(Starts, 3.5, delaySeconds: 0.3);

        // Act
        AudioSignal aligned = Alignment.Align(reference, candidate, 1.0, out int lag);

        // Assert
        Assert.True(lag > 0, $"lag {lag}");
        Assert.Equal(1, OnsetMetrics.Compare(OnsetMetrics.Detect(reference), OnsetMetrics.Detect(aligned)).F1);
    }

    /// <summary>A signal with no notes is left alone.</summary>
    [Fact]
    public void EstimateLagSamples_WithSilentCandidate_ReturnsZero()
    {
        // Arrange
        AudioSignal reference = TestSignals.Bursts(Starts, 3.5);
        AudioSignal silent = new(new float[reference.Length], reference.SampleRate);

        // Act
        int lag = Alignment.EstimateLagSamples(reference, silent, 1.0);

        // Assert
        Assert.Equal(0, lag);
    }

    /// <summary>A negative search range is refused.</summary>
    [Fact]
    public void EstimateLagSamples_WithNegativeRange_Throws()
    {
        // Arrange
        AudioSignal reference = TestSignals.Bursts(Starts, 3.5);

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => Alignment.EstimateLagSamples(reference, reference, -1));
    }
}
