namespace Mod2Xgm.Scoring;

/// <summary>
/// The measures the learned listening score weighs (docs/adr/0097-learned-listening-score.md,
/// docs/adr/0101-listening-score-fit.md): each one a distance from the original, lower closer.
/// </summary>
/// <param name="MelDb">The multi-resolution log-mel distance, in dB.</param>
/// <param name="Mfcc">The MFCC distance.</param>
/// <param name="OnsetMiss">1 − onset F1: 0 when every note start is found.</param>
/// <param name="NoiseToMaskDb">The noise-to-mask ratio, in dB (docs/adr/0100-perceptual-features.md).</param>
/// <param name="NsimDistance">1 − NSIM on the ERB spectrograms.</param>
/// <param name="ModulationDb">The envelope modulation distance, in dB.</param>
/// <param name="HarmonicityDb">The harmonics-to-noise distance, in dB.</param>
internal sealed record ListeningFeatures(
    double MelDb,
    double Mfcc,
    double OnsetMiss,
    double NoiseToMaskDb,
    double NsimDistance,
    double ModulationDb,
    double HarmonicityDb)
{
    /// <summary>The features' names, in the order of <see cref="Values"/> and of <see cref="ListeningWeights.Weights"/>.</summary>
    public static IReadOnlyList<string> Names { get; } =
        ["mel_db", "mfcc", "onset_miss", "nmr_db", "nsim_distance", "modulation_db", "harmonicity_db"];

    /// <summary>The features as numbers, in the order of <see cref="Names"/>.</summary>
    public IReadOnlyList<double> Values => [MelDb, Mfcc, OnsetMiss, NoiseToMaskDb, NsimDistance, ModulationDb, HarmonicityDb];

    /// <summary>The features of <paramref name="report"/>, or <c>null</c> when it was made without the perceptual ones.</summary>
    public static ListeningFeatures? From(ScoreReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Perceptual is { } p
            ? new ListeningFeatures(report.MelDistanceDb, report.MfccDistance, 1 - report.OnsetF1, p.NoiseToMaskDb, p.NsimDistance, p.ModulationDistanceDb, p.HarmonicityDistanceDb)
            : null;
    }
}

/// <summary>
/// The learned listening score (docs/adr/0097-learned-listening-score.md): <c>s(x) = w · f(x)</c>, one number per
/// render, lower closer, with the weights <c>mod2xgm-harness --fit</c> wrote to <see cref="ListeningWeights"/>. Reported
/// next to log-mel; nothing in the converter reads it until it is adopted (plan T9.4l).
/// </summary>
internal static class ListeningScore
{
    /// <summary>True when <see cref="ListeningWeights"/> holds a fit.</summary>
    public static bool Fitted => IsFit(ListeningWeights.Weights);

    /// <summary>The score of <paramref name="report"/> with the fitted weights; <c>null</c> when there is no fit or no perceptual features.</summary>
    public static double? Of(ScoreReport? report) => Of(report, ListeningWeights.Weights);

    /// <summary>
    /// The score of <paramref name="report"/> with <paramref name="weights"/> (in the order of
    /// <see cref="ListeningFeatures.Names"/>); <c>null</c> when the weights are empty or the report has no perceptual features.
    /// </summary>
    public static double? Of(ScoreReport? report, IReadOnlyList<double> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        return report is not null && IsFit(weights) && ListeningFeatures.From(report) is { } features ? Of(features, weights) : null;
    }

    /// <summary>The score <c>w · f</c> of <paramref name="features"/>; the weights must be one per feature.</summary>
    public static double Of(ListeningFeatures features, IReadOnlyList<double> weights)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(weights);
        IReadOnlyList<double> values = features.Values;
        if (weights.Count != values.Count)
        {
            throw new ArgumentException($"{weights.Count} weights for {values.Count} features", nameof(weights));
        }

        double score = 0;
        for (int i = 0; i < values.Count; i++)
        {
            score += weights[i] * values[i];
        }

        return score;
    }

    private static bool IsFit(IReadOnlyList<double> weights) => weights.Count > 0;
}
