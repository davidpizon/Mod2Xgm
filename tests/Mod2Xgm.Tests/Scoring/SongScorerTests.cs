using Mod2Xgm.Scoring;
using Serilog.Events;

namespace Mod2Xgm.Tests.Scoring;

/// <summary>Tests for <see cref="SongScorer"/> (plan T6.2): the combined score and what it logs.</summary>
public sealed class SongScorerTests
{
    private static readonly double[] Starts = [0.5, 1.0, 1.5, 2.0, 2.5];

    /// <summary>A rendering identical to the original scores 0 on every metric, and its noise-to-mask ratio is silence.</summary>
    [Fact]
    public void Compare_WithIdenticalRendering_ScoresZero()
    {
        // Arrange
        AudioSignal song = TestSignals.Bursts(Starts, 3.5);

        // Act
        ScoreReport report = Score(song, song);

        // Assert
        Assert.Equal(0, report.MelDistanceDb);
        Assert.Equal(0, report.MfccDistance);
        Assert.Equal(1, report.OnsetF1);
        Assert.Equal(1, report.OnsetPrecision);
        Assert.Equal(1, report.OnsetRecall);
        Assert.Equal(0, report.OnsetTimingErrorSeconds);
        Assert.Equal(0, report.LagSeconds);
        Assert.Equal(0, report.LengthDifferenceSeconds);
        Assert.Equal(0, report.Combined);
        Assert.Equal(PerceptualMetrics.SilenceDb, report.Perceptual?.NoiseToMaskDb);
        Assert.Equal(0, report.Perceptual?.NsimDistance ?? double.NaN, 9);
    }

    /// <summary>A late rendering scores nearly as well as an on-time one when aligned.</summary>
    [Fact]
    public void Compare_WithLateRendering_RemovesDelay()
    {
        // Arrange
        AudioSignal reference = TestSignals.Bursts(Starts, 3.5);
        AudioSignal late = TestSignals.Bursts(Starts, 3.5, delaySeconds: 0.2);

        // Act
        ScoreReport report = Score(reference, late);

        // Assert
        Assert.InRange(report.LagSeconds, 0.19, 0.21);
        Assert.InRange(report.Combined, 0, 1);
    }

    /// <summary>Without alignment a late rendering scores much worse, and none of its note starts match.</summary>
    [Fact]
    public void Compare_WithoutAlignment_PenalizesDelay()
    {
        // Arrange
        AudioSignal reference = TestSignals.Bursts(Starts, 3.5);
        AudioSignal late = TestSignals.Bursts(Starts, 3.5, delaySeconds: 0.2);
        double aligned = Score(reference, late).Combined;

        // Act
        ScoreReport report = Score(reference, late, new ScoreOptions(Align: false));

        // Assert
        Assert.Equal(0, report.LagSeconds);
        Assert.True(report.Combined > aligned + 3, $"{report.Combined} against {aligned} aligned");
        Assert.Equal(0, report.OnsetF1);
        Assert.Equal(0, report.OnsetPrecision);
        Assert.Equal(0, report.OnsetRecall);
        Assert.Null(report.OnsetTimingErrorSeconds);
    }

    /// <summary>A rendering of a different sound is scored worse than a close one, on the combined number.</summary>
    [Fact]
    public void Compare_WithDifferentSound_ScoresWorse()
    {
        // Arrange
        AudioSignal reference = TestSignals.Bursts(Starts, 3.5);
        AudioSignal quieter = TestSignals.Scaled(reference, 0.7);
        AudioSignal wrongNotes = TestSignals.Bursts([0.3, 0.9, 1.7], 3.5);

        // Act
        double close = Score(reference, quieter).Combined;
        double far = Score(reference, wrongNotes).Combined;

        // Assert
        Assert.True(close < far, $"{close} vs {far}");
    }

    /// <summary>The length difference is reported with its sign; the combined number is the log-mel distance by default.</summary>
    [Fact]
    public void Compare_WithDefaultOptions_ReportsLengthAndMelDistance()
    {
        // Arrange
        AudioSignal reference = TestSignals.Bursts(Starts, 3.5);
        AudioSignal wrongNotes = TestSignals.Bursts([0.3, 0.9, 1.7], 4.0);

        // Act
        ScoreReport report = Score(reference, wrongNotes);

        // Assert
        Assert.Equal(0.5, report.LengthDifferenceSeconds, 3);
        Assert.Equal(report.MelDistanceDb, report.Combined, 9);
    }

    /// <summary>The weight of the onset term can be changed.</summary>
    [Fact]
    public void Compare_WithOnsetWeight_AddsOnsetTerm()
    {
        // Arrange
        AudioSignal reference = TestSignals.Bursts(Starts, 3.5);
        AudioSignal wrongNotes = TestSignals.Bursts([0.3, 0.9, 1.7], 4.0);

        // Act
        ScoreReport report = Score(reference, wrongNotes, new ScoreOptions(OnsetWeight: 10));

        // Assert
        Assert.Equal(report.MelDistanceDb + (10 * (1 - report.OnsetF1)), report.Combined, 9);
    }

    /// <summary>The same inputs always give the same report.</summary>
    [Fact]
    public void Compare_WithSameInputs_GivesSameReport()
    {
        // Arrange
        AudioSignal reference = TestSignals.Bursts(Starts, 3.5);
        AudioSignal candidate = TestSignals.Bursts(Starts, 3.5, delaySeconds: 0.1);

        // Act
        ScoreReport first = Score(reference, candidate);
        ScoreReport second = Score(reference, candidate);

        // Assert
        Assert.Equal(first, second);
    }

    /// <summary>Different sample rates are refused.</summary>
    [Fact]
    public void Compare_WithDifferentRates_Throws()
    {
        // Arrange
        AudioSignal reference = TestSignals.Sine(440, 0.2);
        AudioSignal candidate = TestSignals.Sine(440, 0.2, rate: 44100);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => Score(reference, candidate));
    }

    /// <summary>Every step of a comparison is described in the log.</summary>
    [Fact]
    public void Compare_WithLateRendering_LogsEveryStep()
    {
        // Arrange
        CollectingSink sink = new();
        AudioSignal song = TestSignals.Bursts(Starts, 3.5);
        AudioSignal late = TestSignals.Bursts(Starts, 3.5, delaySeconds: 0.1);

        // Act
        SongScorer.Compare(song, late, null, sink.CreateLogger());

        // Assert
        string[] messages = [.. sink.Messages(LogEventLevel.Debug)];
        Assert.Contains(messages, m => m.StartsWith("Scoring: reference", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("Alignment: the candidate is", StringComparison.Ordinal) && m.Contains("late", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("Log-mel distance", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("MFCC distance", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("Note starts:", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("Combined score", StringComparison.Ordinal));
    }

    /// <summary>Compares two signals, logging to a sink nothing reads.</summary>
    private static ScoreReport Score(AudioSignal reference, AudioSignal candidate, ScoreOptions? options = null) =>
        SongScorer.Compare(reference, candidate, options, new CollectingSink().CreateLogger());
}
