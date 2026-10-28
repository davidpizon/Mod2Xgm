using Mod2Xgm.Scoring;
using Serilog.Events;

namespace Mod2Xgm.Tests.Scoring;

/// <summary>Tests for <see cref="SongScorer"/> (plan T6.2): the combined score and what it logs.</summary>
public sealed class SongScorerTests
{
    private static readonly double[] Starts = [0.5, 1.0, 1.5, 2.0, 2.5];

    private static ScoreReport Score(AudioSignal reference, AudioSignal candidate, ScoreOptions? options = null) =>
        SongScorer.Compare(reference, candidate, options, new CollectingSink().CreateLogger());

    /// <summary>A rendering identical to the original scores 0 on every metric.</summary>
    [Fact]
    public void AnIdenticalRenderingScoresZero()
    {
        AudioSignal song = TestSignals.Bursts(Starts, 3.5);

        ScoreReport report = Score(song, song);

        Assert.Equal(0, report.MelDistanceDb);
        Assert.Equal(0, report.MfccDistance);
        Assert.Equal(1, report.OnsetF1);
        Assert.Equal(1, report.OnsetPrecision);
        Assert.Equal(1, report.OnsetRecall);
        Assert.Equal(0, report.OnsetTimingErrorSeconds);
        Assert.Equal(0, report.LagSeconds);
        Assert.Equal(0, report.LengthDifferenceSeconds);
        Assert.Equal(0, report.Combined);
    }

    /// <summary>A late rendering scores nearly as well as an on-time one when aligned, and much worse when not.</summary>
    [Fact]
    public void AlignmentRemovesAConstantDelay()
    {
        AudioSignal reference = TestSignals.Bursts(Starts, 3.5);
        AudioSignal late = TestSignals.Bursts(Starts, 3.5, delaySeconds: 0.2);

        ScoreReport aligned = Score(reference, late);
        ScoreReport unaligned = Score(reference, late, new ScoreOptions(Align: false));

        Assert.InRange(aligned.LagSeconds, 0.19, 0.21);
        Assert.True(aligned.Combined < 1, $"aligned: {aligned.Combined}");
        Assert.Equal(0, unaligned.LagSeconds);
        Assert.True(unaligned.Combined > aligned.Combined + 3);
        Assert.Equal(0, unaligned.OnsetF1);
        Assert.Equal(0, unaligned.OnsetPrecision);
        Assert.Equal(0, unaligned.OnsetRecall);
        Assert.Null(unaligned.OnsetTimingErrorSeconds);
    }

    /// <summary>A rendering of a different sound is scored worse than a close one, on the combined number.</summary>
    [Fact]
    public void ADifferentSoundScoresWorse()
    {
        AudioSignal reference = TestSignals.Bursts(Starts, 3.5);
        AudioSignal quieter = TestSignals.Scaled(reference, 0.7);
        AudioSignal wrongNotes = TestSignals.Bursts([0.3, 0.9, 1.7], 3.5);

        double close = Score(reference, quieter).Combined;
        double far = Score(reference, wrongNotes).Combined;

        Assert.True(close < far, $"{close} vs {far}");
    }

    /// <summary>The length difference is reported with its sign, and the weight of the onset term can be changed.</summary>
    [Fact]
    public void LengthDifferenceAndOnsetWeightAreReported()
    {
        AudioSignal reference = TestSignals.Bursts(Starts, 3.5);
        AudioSignal wrongNotes = TestSignals.Bursts([0.3, 0.9, 1.7], 4.0);

        ScoreReport light = Score(reference, wrongNotes, new ScoreOptions(OnsetWeight: 0));
        ScoreReport heavy = Score(reference, wrongNotes, new ScoreOptions(OnsetWeight: 10));

        Assert.Equal(0.5, light.LengthDifferenceSeconds, 3);
        Assert.Equal(light.MelDistanceDb, light.Combined, 9);
        Assert.Equal(light.MelDistanceDb + (10 * (1 - heavy.OnsetF1)), heavy.Combined, 9);
    }

    /// <summary>The same inputs always give the same report.</summary>
    [Fact]
    public void ScoringIsDeterministic()
    {
        AudioSignal reference = TestSignals.Bursts(Starts, 3.5);
        AudioSignal candidate = TestSignals.Bursts(Starts, 3.5, delaySeconds: 0.1);

        Assert.Equal(Score(reference, candidate), Score(reference, candidate));
    }

    /// <summary>Different sample rates are refused.</summary>
    [Fact]
    public void DifferentSampleRatesAreRefused() =>
        Assert.Throws<ArgumentException>(() => Score(TestSignals.Sine(440, 0.2), TestSignals.Sine(440, 0.2, rate: 44100)));

    /// <summary>Every step of a comparison is described in the log.</summary>
    [Fact]
    public void EveryStepIsLogged()
    {
        CollectingSink sink = new();
        AudioSignal song = TestSignals.Bursts(Starts, 3.5);

        SongScorer.Compare(song, TestSignals.Bursts(Starts, 3.5, delaySeconds: 0.1), null, sink.CreateLogger());

        string[] messages = [.. sink.Messages(LogEventLevel.Debug)];
        Assert.Contains(messages, m => m.StartsWith("Scoring: reference", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("Alignment: the candidate is", StringComparison.Ordinal) && m.Contains("late", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("Log-mel distance", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("MFCC distance", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("Note starts:", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("Combined score", StringComparison.Ordinal));
    }
}
