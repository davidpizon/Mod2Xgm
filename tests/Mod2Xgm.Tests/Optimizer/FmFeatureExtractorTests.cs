using Mod2Xgm.Analysis;
using Mod2Xgm.Optimizer;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="FmFeatureExtractor"/>: how a patch's loudness over time and brightness are compared with its sample's (docs/adr/0096-fm-confidence.md).</summary>
public sealed class FmFeatureExtractorTests
{
    /// <summary>Time between test frames: the region spectra's 10 ms.</summary>
    private const double HopSeconds = 0.01;

    /// <summary>Frames in a test spectrum: half a second.</summary>
    private const int Frames = 50;

    /// <summary>The fit's own score, passed through unchanged.</summary>
    private static readonly NoteScore Score = new(5, 20, 4);

    /// <summary>Checks that a patch that sounds like its sample measures no distance.</summary>
    [Fact]
    public void Measure_WithSameSpectrum_IsZero()
    {
        // Arrange
        RegionSpectrum sample = Spectrum(_ => -20, band: 10);

        // Act
        FmRegionFeatures sut = FmFeatureExtractor.Measure(sample, sample, Score);

        // Assert
        Assert.Equal(0, sut.AttackDb, 6);
        Assert.Equal(0, sut.EnvelopeDb, 6);
        Assert.Equal(0, sut.CentroidBands, 6);
        Assert.Same(Score, sut.Score);
    }

    /// <summary>Checks that only the shape counts: a patch 12 dB quieter all through measures next to no distance (the silent bands' floor moves a few thousandths of a dB).</summary>
    [Fact]
    public void Measure_WithQuieterCopy_IsAboutZero()
    {
        // Arrange
        RegionSpectrum sample = Spectrum(t => -20 - (t * 0.2), band: 10);
        RegionSpectrum patch = Spectrum(t => -32 - (t * 0.2), band: 10);

        // Act
        FmRegionFeatures sut = FmFeatureExtractor.Measure(sample, patch, Score);

        // Assert
        Assert.InRange(sut.EnvelopeDb, 0, 0.05);
    }

    /// <summary>Checks that a soft attack on a plucked sample shows up in the attack more than in the whole envelope.</summary>
    [Fact]
    public void Measure_WithSlowAttack_AttackIsFurther()
    {
        // Arrange
        RegionSpectrum sample = Spectrum(_ => -20, band: 10);
        RegionSpectrum patch = Spectrum(t => t < 10 ? -50 + (t * 3) : -20, band: 10);

        // Act
        FmRegionFeatures sut = FmFeatureExtractor.Measure(sample, patch, Score);

        // Assert
        Assert.True(sut.AttackDb > sut.EnvelopeDb);
        Assert.True(sut.EnvelopeDb > 0);
    }

    /// <summary>Checks that a patch brighter than its sample measures how many bands its brightness moved.</summary>
    [Fact]
    public void Measure_WithBrighterPatch_MeasuresCentroid()
    {
        // Arrange
        RegionSpectrum sample = Spectrum(_ => -20, band: 10);
        RegionSpectrum patch = Spectrum(_ => -20, band: 14);

        // Act
        FmRegionFeatures sut = FmFeatureExtractor.Measure(sample, patch, Score);

        // Assert
        Assert.Equal(4, sut.CentroidBands, 3);
    }

    /// <summary>Checks that a silent patch is as far as the ruler goes.</summary>
    [Fact]
    public void Measure_WithSilentPatch_IsRange()
    {
        // Arrange
        RegionSpectrum sample = Spectrum(_ => -20, band: 10);
        RegionSpectrum silent = new(HopSeconds, [], null);

        // Act
        FmRegionFeatures sut = FmFeatureExtractor.Measure(sample, silent, Score);

        // Assert
        Assert.Equal(NoteScorer.RangeDb, sut.AttackDb);
        Assert.Equal(NoteScorer.RangeDb, sut.EnvelopeDb);
    }

    /// <summary>Checks that a frame's brightness is the power-weighted mean of its bands.</summary>
    [Fact]
    public void Centroid_WithTwoEqualBands_IsBetween()
    {
        // Arrange
        float[] frame = [.. Enumerable.Repeat((float)SampleAnalyzer.FloorDb, SampleAnalyzer.MelBands)];
        frame[4] = -10;
        frame[8] = -10;

        // Act
        double sut = FmFeatureExtractor.Centroid(frame);

        // Assert
        Assert.Equal(6, sut, 2);
    }

    /// <summary>A spectrum whose sound is all in one band, at the level <paramref name="levelDb"/> gives each frame.</summary>
    private static RegionSpectrum Spectrum(Func<int, double> levelDb, int band) => new(
        HopSeconds,
        [.. Enumerable.Range(0, Frames).Select(t =>
        {
            float[] frame = [.. Enumerable.Repeat((float)SampleAnalyzer.FloorDb, SampleAnalyzer.MelBands)];
            frame[band] = (float)levelDb(t);
            return frame;
        })],
        null);
}
