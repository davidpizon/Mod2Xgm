// Written by mod2xgm-harness --fit (docs/harness.md#the-fit). A fit rewrites this whole file; don't edit it by hand.
namespace Mod2Xgm.Scoring;

/// <summary>
/// The learned listening score's weights, fitted on David's blind answers (docs/adr/0101-listening-score-fit.md).
/// Empty until there are enough answers for one weight.
/// </summary>
internal static class ListeningWeights
{
    /// <summary>The day of the fit, <c>yyyy-MM-dd</c>; empty when not fitted.</summary>
    public const string Fitted = "";

    /// <summary>How many answers the fit used.</summary>
    public const int Answers = 0;

    /// <summary>How many songs those answers came from.</summary>
    public const int Songs = 0;

    /// <summary>How often, in percent, the fit guessed David's pick on songs it was not fitted on (leave one song out).</summary>
    public const double LeaveOneSongOutAgreement = 0;

    /// <summary>How often, in percent, the log-mel distance alone guessed it on the same answers.</summary>
    public const double LogMelAgreement = 0;

    /// <summary>One weight per feature, in the order of <see cref="ListeningFeatures.Names"/>; empty when not fitted.</summary>
    public static IReadOnlyList<double> Weights { get; } = [];
}
