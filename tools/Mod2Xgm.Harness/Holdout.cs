using System.Globalization;
using Mod2Xgm.Scoring;
using Serilog;

namespace Mod2Xgm.Harness;

/// <summary>
/// The hold-out songs (docs/adr/0105-hold-out-songs.md): a row of the answers file is a hold-out row when its song is in
/// the hold-out folder. The fit and the pretrained-model comparison leave those rows out; <c>--holdout</c> scores them.
/// </summary>
internal static class HoldoutSongs
{
    /// <summary>The hold-out folder, relative to the repository root.</summary>
    public const string DefaultFolder = "tests/data/holdout";

    /// <summary>The file names of the <c>.mod</c> files in <paramref name="folder"/>, any case; empty when it doesn't exist.</summary>
    public static IReadOnlySet<string> In(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(folder))
        {
            names.UnionWith(Directory.EnumerateFiles(folder, "*.mod").Select(Path.GetFileName).OfType<string>());
        }

        return names;
    }
}

/// <summary>One hold-out answer's line in the report.</summary>
/// <param name="Answer">The answer and both versions' features.</param>
/// <param name="LearnedGuess"><c>a</c> or <c>b</c>, the version the learned score finds closer, or <c>tie</c>.</param>
/// <param name="LogMelGuess"><c>a</c> or <c>b</c>, the version closer by log-mel, or <c>tie</c>.</param>
internal sealed record HoldoutLine(FitAnswer Answer, string LearnedGuess, string LogMelGuess);

/// <summary>How the learned score did on the hold-out, against the bar David set (docs/adr/0105-hold-out-songs.md).</summary>
/// <param name="Pairs">Answered hold-out pairs whose clips came out as David heard them.</param>
/// <param name="AbPicks">Those with an a or b pick; "can't tell" counts for neither score.</param>
/// <param name="Songs">How many songs the pairs came from.</param>
/// <param name="LearnedAgreement">How often, in percent, the learned score guessed an a/b pick; <c>null</c> without a/b picks.</param>
/// <param name="LogMelAgreement">How often, in percent, log-mel guessed one; <c>null</c> without a/b picks.</param>
/// <param name="Passed">True when every part of the bar is met.</param>
/// <param name="Lines">One line per pair, in the answers' order.</param>
internal sealed record HoldoutResult(
    int Pairs,
    int AbPicks,
    int Songs,
    double? LearnedAgreement,
    double? LogMelAgreement,
    bool Passed,
    IReadOnlyList<HoldoutLine> Lines);

/// <summary>
/// Scores the learned listening score on the hold-out answers with its fitted weights, fixed (no refit), and checks David's
/// bar: at least <see cref="MinPairs"/> pairs, agreement of at least <see cref="MinAgreement"/> %, and at least
/// <see cref="MinLead"/> points above log-mel on the same pairs (docs/adr/0105-hold-out-songs.md).
/// </summary>
internal static class HoldoutScore
{
    /// <summary>The fewest answered hold-out pairs the decision may rest on (David).</summary>
    public const int MinPairs = 30;

    /// <summary>The learned score's agreement with David's a/b picks must reach this, in percent (David).</summary>
    public const double MinAgreement = 70;

    /// <summary>And beat log-mel's agreement on the same pairs by this many points (David).</summary>
    public const double MinLead = 10;

    /// <summary>Scores <paramref name="answers"/> with <paramref name="weights"/> (in the order of <see cref="ListeningFeatures.Names"/>).</summary>
    public static HoldoutResult Score(IReadOnlyList<FitAnswer> answers, IReadOnlyList<double> weights)
    {
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(weights);
        bool fitted = weights.Any(w => w != 0);
        HoldoutLine[] lines =
        [
            .. answers.Select(a => new HoldoutLine(
                a,
                fitted ? Guess(ListeningScore.Of(a.A, weights), ListeningScore.Of(a.B, weights)) : "tie",
                Guess(a.A.MelDb, a.B.MelDb))),
        ];
        HoldoutLine[] picked = [.. lines.Where(l => l.Answer.Pick is "a" or "b")];
        double? learned = Agreement(picked, l => l.LearnedGuess);
        double? logMel = Agreement(picked, l => l.LogMelGuess);
        int songs = answers.Select(a => a.Song).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        bool passed = fitted
            && answers.Count >= MinPairs
            && learned is { } ours && ours >= MinAgreement
            && logMel is { } theirs && ours - theirs >= MinLead;
        return new HoldoutResult(answers.Count, picked.Length, songs, learned, logMel, passed, lines);
    }

    /// <summary>The report's lines: the counts, both agreements, the bar's verdict, then one line per pair.</summary>
    public static IReadOnlyList<string> Describe(HoldoutResult result, bool fitted)
    {
        ArgumentNullException.ThrowIfNull(result);
        List<string> lines =
        [
            string.Create(CultureInfo.InvariantCulture, $"Hold-out: {result.Pairs} answered pairs from {result.Songs} songs, {result.AbPicks} of them a/b picks"),
            fitted
                ? $"Learned score (fixed weights, no refit): {Percent(result.LearnedAgreement)} of a/b picks guessed"
                : "Learned score: not fitted (src/Scoring/ListeningWeights.cs is empty), so it guesses nothing",
            $"Log-mel: {Percent(result.LogMelAgreement)} of a/b picks guessed",
            string.Create(CultureInfo.InvariantCulture, $"Bar (docs/adr/0105-hold-out-songs.md): at least {MinPairs} pairs, at least {MinAgreement:0} %, at least {MinLead:0} points above log-mel"),
            "Verdict: " + Verdict(result, fitted),
        ];
        foreach (HoldoutLine l in result.Lines)
        {
            lines.Add($"  {l.Answer.Name} ({l.Answer.Song}): picked {Pick(l.Answer.Pick)}; learned {l.LearnedGuess}; log-mel {l.LogMelGuess}");
        }

        return lines;
    }

    private static string Verdict(HoldoutResult result, bool fitted)
    {
        List<string> missing = [];
        if (!fitted)
        {
            missing.Add("no fitted weights");
        }

        if (result.Pairs < MinPairs)
        {
            missing.Add(string.Create(CultureInfo.InvariantCulture, $"{MinPairs - result.Pairs} more pairs needed"));
        }

        if (result.LearnedAgreement is not { } l || l < MinAgreement)
        {
            missing.Add(string.Create(CultureInfo.InvariantCulture, $"agreement under {MinAgreement:0} %"));
        }
        else if (result.LogMelAgreement is not { } m || l - m < MinLead)
        {
            missing.Add(string.Create(CultureInfo.InvariantCulture, $"less than {MinLead:0} points above log-mel"));
        }

        return result.Passed ? "the learned score passes the bar" : "not passed yet (" + string.Join("; ", missing) + ")";
    }

    /// <summary><c>a</c> when A's distance is lower (closer), <c>b</c> when B's is, <c>tie</c> when they are equal.</summary>
    private static string Guess(double a, double b) => a < b ? "a" : b < a ? "b" : "tie";

    /// <summary>The percentage of <paramref name="lines"/> whose guess is David's pick; a tie counts as a miss. <c>null</c> when empty.</summary>
    private static double? Agreement(IReadOnlyList<HoldoutLine> lines, Func<HoldoutLine, string> guess) =>
        lines.Count == 0 ? null : 100.0 * lines.Count(l => guess(l) == l.Answer.Pick) / lines.Count;

    private static string Percent(double? value) => value is { } v ? v.ToString("0.0", CultureInfo.InvariantCulture) + " %" : "–";

    private static string Pick(string pick) => pick == "same" ? "can't tell" : pick;
}

/// <summary>
/// <c>--holdout</c> after the remake (docs/harness.md#the-hold-out): scores the hold-out rows whose clips came out as David
/// heard them, writes <c>listening/holdout.txt</c> and logs it. Never changes the weights.
/// </summary>
internal static class HoldoutRun
{
    /// <summary>Scores <paramref name="remake"/>'s answered rows with <paramref name="weights"/>; always 0: rows left out are listed.</summary>
    public static int Run(RemakeResult remake, string outputDirectory, IReadOnlyList<double> weights, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(remake);
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(logger);
        List<FitAnswer> answers = [];
        List<string> skipped = [];
        foreach (RemadeRow r in remake.Rows)
        {
            if (r is { Heard: true, A: { } a, B: { } b, Row.Pick.Length: > 0 })
            {
                answers.Add(new FitAnswer(r.Row.Name, r.Row.Song, r.Row.Pick, a, b));
            }
            else
            {
                skipped.Add("Left out " + r.Row.Name + ": " + ListeningFitRun.Why(r));
            }
        }

        HoldoutResult result = HoldoutScore.Score(answers, weights);
        List<string> lines = [.. HoldoutScore.Describe(result, weights.Any(w => w != 0)), .. skipped];
        string folder = Path.Combine(outputDirectory, "listening");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "holdout.txt"), string.Join('\n', lines) + "\n");
        foreach (string line in lines)
        {
            logger.Information("{Line:l}", line);
        }

        return 0;
    }
}
