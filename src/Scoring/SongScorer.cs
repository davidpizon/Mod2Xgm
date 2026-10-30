using System.Globalization;
using Serilog;

namespace Mod2Xgm.Scoring;

/// <summary>What to measure and how to weigh it.</summary>
/// <param name="Align">Line the candidate up with the reference before comparing (recommended).</param>
/// <param name="MaxLagSeconds">Largest offset the alignment looks for, either way.</param>
/// <param name="OnsetToleranceSeconds">How far apart two note starts may be and still be the same note.</param>
/// <param name="OnsetWeight">
/// dB added to the combined score for a candidate whose note starts all miss (F1 of 0). 0 by default: on the corpus the
/// onset term made the score agree less with listening (docs/adr/0049-simple-mapper-baseline.md).
/// </param>
/// <param name="Resolutions">The spectral scales, or <c>null</c> for <see cref="MelResolution.Default"/>.</param>
internal sealed record ScoreOptions(
    bool Align = true,
    double MaxLagSeconds = 1.0,
    double OnsetToleranceSeconds = OnsetMetrics.DefaultToleranceSeconds,
    double OnsetWeight = 0.0,
    IReadOnlyList<MelResolution>? Resolutions = null)
{
    /// <summary>Alignment on, 1 s search, 50 ms tolerance, no onset weight, default scales.</summary>
    public static ScoreOptions Default { get; } = new();
}

/// <summary>How close one rendering is to the original, by each metric.</summary>
/// <param name="MelDistanceDb">Multi-resolution log-mel distance in dB (0 = identical); the main number.</param>
/// <param name="MfccDistance">Timbre distance from MFCCs (0 = identical), tracked alongside.</param>
/// <param name="OnsetPrecision">Share of the candidate's note starts that are real.</param>
/// <param name="OnsetRecall">Share of the original's note starts the candidate has.</param>
/// <param name="OnsetF1">Harmonic mean of precision and recall.</param>
/// <param name="OnsetTimingErrorSeconds">Mean timing error of the matched note starts, or <c>null</c> when none matched.</param>
/// <param name="LagSeconds">How late the candidate was; it was moved earlier by this much (0 when not aligning).</param>
/// <param name="LengthDifferenceSeconds">Candidate length minus reference length (before alignment).</param>
/// <param name="Combined">
/// The number to minimize: <paramref name="MelDistanceDb"/> plus the onset penalty (none by default). It agrees only
/// weakly with listening so far (docs/adr/0049-simple-mapper-baseline.md).
/// </param>
internal sealed record ScoreReport(
    double MelDistanceDb,
    double MfccDistance,
    double OnsetPrecision,
    double OnsetRecall,
    double OnsetF1,
    double? OnsetTimingErrorSeconds,
    double LagSeconds,
    double LengthDifferenceSeconds,
    double Combined);

/// <summary>
/// Compares a rendering with the original and says how far apart they sound: align, then spectral distance, timbre
/// distance and note-start match (docs/adr/0042-scoring-metrics.md). Used by the accuracy harness (T6.3) and by the
/// optimizer's candidate tournament (M9).
/// </summary>
internal static class SongScorer
{
    /// <summary>Scores <paramref name="candidate"/> against <paramref name="reference"/>; every step is logged at Debug.</summary>
    /// <param name="reference">The original (the MOD rendered).</param>
    /// <param name="candidate">The rendering being judged; the same sample rate.</param>
    /// <param name="options">What to measure; <c>null</c> for <see cref="ScoreOptions.Default"/>.</param>
    /// <param name="logger">Where the steps are described.</param>
    public static ScoreReport Compare(AudioSignal reference, AudioSignal candidate, ScoreOptions? options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        SpectralMetrics.CheckSameRate(reference, candidate);
        ScoreOptions settings = options ?? ScoreOptions.Default;
        double lengthDifference = candidate.Seconds - reference.Seconds;
        logger.Debug(
            "Scoring: reference {Reference} s, candidate {Candidate} s at {Rate} Hz",
            Format(reference.Seconds),
            Format(candidate.Seconds),
            reference.SampleRate);

        AudioSignal aligned = candidate;
        int lag = 0;
        if (settings.Align)
        {
            aligned = Alignment.Align(reference, candidate, settings.MaxLagSeconds, out lag);
            logger.Debug("Alignment: the candidate is {Lag} ms {Direction}; moved to match", Format(Math.Abs(lag) * 1000.0 / reference.SampleRate), lag >= 0 ? "late" : "early");
        }

        double mel = SpectralMetrics.MelDistanceDb(reference, aligned, settings.Resolutions);
        logger.Debug("Log-mel distance {Distance} dB (multi-resolution)", Format(mel));
        double mfcc = SpectralMetrics.MfccDistance(reference, aligned);
        logger.Debug("MFCC distance {Distance}", Format(mfcc));

        OnsetComparison onsets = OnsetMetrics.Compare(OnsetMetrics.Detect(reference), OnsetMetrics.Detect(aligned), settings.OnsetToleranceSeconds);
        logger.Debug(
            "Note starts: precision {Precision}, recall {Recall}, F1 {F1}, {Matched} matched",
            Format(onsets.Precision),
            Format(onsets.Recall),
            Format(onsets.F1),
            onsets.Matched);

        double combined = mel + (settings.OnsetWeight * (1 - onsets.F1));
        logger.Debug("Combined score {Score} (lower is closer)", Format(combined));
        return new ScoreReport(
            mel,
            mfcc,
            onsets.Precision,
            onsets.Recall,
            onsets.F1,
            onsets.MeanTimingErrorSeconds,
            (double)lag / reference.SampleRate,
            lengthDifference,
            combined);
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
