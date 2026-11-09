namespace Mod2Xgm.Optimizer;

/// <summary>
/// The chance David would pick a region's FM patch, or not tell it apart, over the sample's recording in the song
/// (docs/adr/0096-fm-confidence.md). The model is provisional: a logistic curve on the fit's log-mel distance, centred on
/// the corpus median, until the listening rounds of plan T9.4i fit it to his answers.
/// </summary>
internal static class FmConfidenceModel
{
    /// <summary>
    /// The log-mel distance, in dB, at which FM and the recording are even (provisional): the corpus median of the fits
    /// without the LFO, which is what the plan plays (docs/adr/0069-fm-patch-fitting.md#on-the-corpus).
    /// </summary>
    public const double ProvisionalMidpointDb = 6.96;

    /// <summary>How many dB of distance move the odds by a factor of e (provisional): about the spread of the corpus's fits, so a fit 3 dB past the median is about 12 %.</summary>
    public const double ProvisionalScaleDb = 1.5;

    /// <summary>The chance, 0–1, that the patch measured by <paramref name="features"/> is fine for its region.</summary>
    public static double Probability(FmRegionFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);
        return 1 / (1 + Math.Exp((features.Score.MelDb - ProvisionalMidpointDb) / ProvisionalScaleDb));
    }
}
