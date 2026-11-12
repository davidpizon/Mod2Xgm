using System.Globalization;
using System.Text;
using Mod2Xgm.Scoring;
using Serilog;

namespace Mod2Xgm.Harness;

/// <summary>
/// One answered question the fit can learn from: the song (the leave-one-song-out group), David's pick and each clip's
/// features against the original's clip.
/// </summary>
/// <param name="Name">The row's name, <c>r20-q1</c>.</param>
/// <param name="Song">The song's file name.</param>
/// <param name="Pick"><c>a</c>, <c>b</c> or <c>same</c>.</param>
/// <param name="A">Version A's features.</param>
/// <param name="B">Version B's features.</param>
internal sealed record FitAnswer(string Name, string Song, string Pick, ListeningFeatures A, ListeningFeatures B)
{
    /// <summary>How much A won: 1 for <c>a</c>, 0 for <c>b</c>, ½ for "can't tell" (docs/adr/0097-learned-listening-score.md).</summary>
    public double Target => Pick switch
    {
        "a" => 1,
        "b" => 0,
        _ => 0.5,
    };
}

/// <summary>How a fit ended.</summary>
internal enum FitOutcome
{
    /// <summary>Too few answers or songs for one weight: nothing is fitted.</summary>
    NotEnoughAnswers,

    /// <summary>No feature predicts the picks better than a coin on songs left out: nothing is fitted.</summary>
    NothingHelps,

    /// <summary>Weights were fitted.</summary>
    Fitted,
}

/// <summary>One answer's line in the fit's report.</summary>
/// <param name="Answer">The answer.</param>
/// <param name="LeftOutChance">The chance the fit gave David's pick (A's chance for "can't tell") when the answer's song was left out; <c>null</c> without a fit.</param>
/// <param name="LogMelGuess"><c>a</c> or <c>b</c>, the version closer by log-mel, or <c>tie</c>.</param>
internal sealed record FitLine(FitAnswer Answer, double? LeftOutChance, string LogMelGuess);

/// <summary>What the fit found.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Reason">Why nothing was fitted, in words; empty after a fit.</param>
/// <param name="Answers">How many answers it had.</param>
/// <param name="Songs">How many songs those came from.</param>
/// <param name="MaxWeights">The most weights the answers allow (one per <see cref="ListeningFit.AnswersPerWeight"/>).</param>
/// <param name="Weights">One raw weight per feature (<see cref="ListeningFeatures.Names"/>), 0 for the ones left out; empty without a fit.</param>
/// <param name="Lambda">The L2 strength chosen, on standardized features; 0 without a fit.</param>
/// <param name="LeftOutLogLoss">The mean log-loss on songs left out; ln 2 (a coin) without a fit.</param>
/// <param name="Agreement">How often, in percent, the fit guessed an a/b pick on songs left out; <c>null</c> without a fit or a/b picks.</param>
/// <param name="LogMelAgreement">How often, in percent, log-mel alone guessed an a/b pick; <c>null</c> without a/b picks.</param>
/// <param name="Lines">One line per answer, in the answers' order.</param>
internal sealed record FitResult(
    FitOutcome Outcome,
    string Reason,
    int Answers,
    int Songs,
    int MaxWeights,
    IReadOnlyList<double> Weights,
    double Lambda,
    double LeftOutLogLoss,
    double? Agreement,
    double? LogMelAgreement,
    IReadOnlyList<FitLine> Lines);

/// <summary>How one fixed set of features does on leave-one-song-out checks (<see cref="ListeningFit.Trial"/>).</summary>
/// <param name="Lambda">The L2 strength with the lowest log-loss, on standardized features.</param>
/// <param name="LogLoss">The mean log-loss on songs left out; ln 2 is a coin's.</param>
/// <param name="Agreement">How often, in percent, it guessed an a/b pick on songs left out; <c>null</c> without a/b picks.</param>
internal sealed record FitTrial(double Lambda, double LogLoss, double? Agreement);

/// <summary>
/// Fits the learned listening score (plan T9.4k2, docs/adr/0101-listening-score-fit.md): a pairwise logistic model with no
/// intercept, <c>P(A ≻ B) = σ(w · (f(B) − f(A)))</c>, L2-regularized, its features and strength chosen by forward
/// selection on leave-one-song-out log-loss, at most one weight per <see cref="AnswersPerWeight"/> answers.
/// </summary>
internal static class ListeningFit
{
    /// <summary>At most one weight per this many answers (docs/adr/0097-learned-listening-score.md).</summary>
    public const int AnswersPerWeight = 10;

    /// <summary>The L2 strengths tried, on features scaled to unit root-mean-square difference.</summary>
    private static readonly double[] Lambdas = [0.01, 0.1, 1, 10];

    /// <summary>The log-loss of a coin: what a fit must beat to keep a feature.</summary>
    private static readonly double CoinLogLoss = Math.Log(2);

    /// <summary>Fits <paramref name="answers"/>; nothing is fitted when they are too few or no feature helps.</summary>
    public static FitResult Fit(IReadOnlyList<FitAnswer> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        int songs = answers.Select(a => a.Song).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int maxWeights = Math.Min(answers.Count / AnswersPerWeight, ListeningFeatures.Names.Count);
        double? logMel = Agreement(answers, i => MelGuess(answers[i]) switch
        {
            "a" => 1.0,
            "b" => 0.0,
            _ => 0.5,
        });
        if (maxWeights == 0 || songs < 2)
        {
            string reason = maxWeights == 0
                ? string.Create(CultureInfo.InvariantCulture, $"not enough answers: {answers.Count}, and one weight needs {AnswersPerWeight}")
                : "not enough songs: leaving one song out needs answers from two";
            return NoFit(FitOutcome.NotEnoughAnswers, reason, answers, songs, maxWeights, logMel);
        }

        double[][] differences = [.. answers.Select(Difference)];
        double[] targets = [.. answers.Select(a => a.Target)];
        string[] groups = [.. answers.Select(a => a.Song.ToUpperInvariant())];
        double[] scales = Scales(differences);
        List<int> chosen = [];
        double lambda = 0;
        double best = CoinLogLoss;
        while (chosen.Count < maxWeights)
        {
            (int Feature, double Lambda, double Loss)? step = null;
            for (int j = 0; j < scales.Length; j++)
            {
                if (scales[j] == 0 || chosen.Contains(j))
                {
                    continue;
                }

                int[] features = [.. chosen, j];
                foreach (double l in Lambdas)
                {
                    double loss = LeftOut(differences, targets, groups, scales, features, l).LogLoss;
                    if (step is null || loss < step.Value.Loss - 1e-12)
                    {
                        step = (j, l, loss);
                    }
                }
            }

            if (step is not { } s || s.Loss >= best - 1e-9)
            {
                break;
            }

            chosen.Add(s.Feature);
            lambda = s.Lambda;
            best = s.Loss;
        }

        if (chosen.Count == 0)
        {
            return NoFit(FitOutcome.NothingHelps, "no measure guesses the picks better than a coin on songs left out", answers, songs, maxWeights, logMel);
        }

        int[] set = [.. chosen];
        (double[] chances, double logLoss) = LeftOut(differences, targets, groups, scales, set, lambda);
        double[] standardized = Solve(differences, targets, scales, set, lambda, Enumerable.Range(0, answers.Count));
        double[] weights = new double[scales.Length];
        for (int k = 0; k < set.Length; k++)
        {
            weights[set[k]] = standardized[k] / scales[set[k]];
        }

        FitLine[] lines = [.. answers.Select((a, i) => new FitLine(a, a.Target == 0 ? 1 - chances[i] : chances[i], MelGuess(a)))];
        double? agreement = Agreement(answers, i => chances[i]);
        return new FitResult(FitOutcome.Fitted, string.Empty, answers.Count, songs, maxWeights, weights, lambda, logLoss, agreement, logMel, lines);
    }

    /// <summary>
    /// Leave-one-song-out checks of one fixed set of <paramref name="features"/> (columns of <paramref name="differences"/>,
    /// one row of <c>f(B) − f(A)</c> per answer), with the λ that gives the lowest log-loss: how the pretrained models are
    /// compared with the <c>src/Scoring</c> features (plan T9.4k4, docs/adr/0104-pretrained-embeddings.md). No cap on
    /// weights here, as nothing is written. <c>null</c> with answers from fewer than two songs, or when a feature never differs.
    /// </summary>
    public static FitTrial? Trial(IReadOnlyList<FitAnswer> answers, double[][] differences, IReadOnlyList<int> features)
    {
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(differences);
        ArgumentNullException.ThrowIfNull(features);
        if (differences.Length != answers.Count)
        {
            throw new ArgumentException($"{differences.Length} rows of differences for {answers.Count} answers", nameof(differences));
        }

        string[] groups = [.. answers.Select(a => a.Song.ToUpperInvariant())];
        if (groups.Distinct(StringComparer.Ordinal).Count() < 2)
        {
            return null;
        }

        double[] scales = Scales(differences);
        int[] set = [.. features];
        if (set.Any(j => scales[j] == 0))
        {
            return null;
        }

        double[] targets = [.. answers.Select(a => a.Target)];
        (double Lambda, double[] Chances, double LogLoss)? best = null;
        foreach (double l in Lambdas)
        {
            (double[] chances, double loss) = LeftOut(differences, targets, groups, scales, set, l);
            if (best is null || loss < best.Value.LogLoss - 1e-12)
            {
                best = (l, chances, loss);
            }
        }

        (double lambda, double[] leftOut, double logLoss) = best!.Value; // Lambdas is never empty
        return new FitTrial(lambda, logLoss, Agreement(answers, i => leftOut[i]));
    }

    /// <summary>The fit's report, one line each: the summary, then every answer (David, 2026-11-10).</summary>
    public static IReadOnlyList<string> Describe(FitResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        List<string> lines = [string.Create(CultureInfo.InvariantCulture, $"Answers: {result.Answers} from {result.Songs} songs; at most {result.MaxWeights} weights")];
        if (result.Outcome == FitOutcome.Fitted)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"Fitted: λ = {result.Lambda:0.###}, leave-one-song-out log-loss {result.LeftOutLogLoss:0.###} (a coin: {CoinLogLoss:0.###})"));
            for (int j = 0; j < result.Weights.Count; j++)
            {
                if (result.Weights[j] != 0)
                {
                    lines.Add(string.Create(CultureInfo.InvariantCulture, $"  {ListeningFeatures.Names[j]}: {result.Weights[j]:0.####}"));
                }
            }
        }
        else
        {
            lines.Add("Not fitted: " + result.Reason);
        }

        lines.Add($"Guessed David's pick (a/b answers): learned {Percent(result.Agreement)}, log-mel alone {Percent(result.LogMelAgreement)}");
        foreach (FitLine line in result.Lines)
        {
            string chance = line.LeftOutChance is { } c ? c.ToString("0.00", CultureInfo.InvariantCulture) : "–";
            lines.Add($"  {line.Answer.Name} {line.Answer.Song}: picked {line.Answer.Pick}, learned gave it {chance}, log-mel says {line.LogMelGuess}");
        }

        return lines;
    }

    /// <summary>
    /// The whole of <c>src/Scoring/ListeningWeights.cs</c> for <paramref name="result"/>, a fit, dated <paramref name="date"/>
    /// (David, 2026-11-10: the fit writes the weights into the code itself).
    /// </summary>
    public static string WeightsFile(FitResult result, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome != FitOutcome.Fitted)
        {
            throw new ArgumentException("only a fit has weights to write", nameof(result));
        }

        StringBuilder code = new();
        code.Append("// Written by mod2xgm-harness --fit (docs/harness.md#the-fit). A fit rewrites this whole file; don't edit it by hand.\n");
        code.Append("namespace Mod2Xgm.Scoring;\n\n");
        code.Append("/// <summary>\n");
        code.Append("/// The learned listening score's weights, fitted on David's blind answers (docs/adr/0101-listening-score-fit.md).\n");
        code.Append("/// Empty until there are enough answers for one weight.\n");
        code.Append("/// </summary>\n");
        code.Append("internal static class ListeningWeights\n{\n");
        code.Append("    /// <summary>The day of the fit, <c>yyyy-MM-dd</c>; empty when not fitted.</summary>\n");
        code.Append(CultureInfo.InvariantCulture, $"    public const string Fitted = \"{date:yyyy-MM-dd}\";\n\n");
        code.Append("    /// <summary>How many answers the fit used.</summary>\n");
        code.Append(CultureInfo.InvariantCulture, $"    public const int Answers = {result.Answers};\n\n");
        code.Append("    /// <summary>How many songs those answers came from.</summary>\n");
        code.Append(CultureInfo.InvariantCulture, $"    public const int Songs = {result.Songs};\n\n");
        code.Append("    /// <summary>How often, in percent, the fit guessed David's pick on songs it was not fitted on (leave one song out).</summary>\n");
        code.Append(CultureInfo.InvariantCulture, $"    public const double LeaveOneSongOutAgreement = {Literal(result.Agreement ?? 0)};\n\n");
        code.Append("    /// <summary>How often, in percent, the log-mel distance alone guessed it on the same answers.</summary>\n");
        code.Append(CultureInfo.InvariantCulture, $"    public const double LogMelAgreement = {Literal(result.LogMelAgreement ?? 0)};\n\n");
        code.Append("    /// <summary>One weight per feature, in the order of <see cref=\"ListeningFeatures.Names\"/>; empty when not fitted.</summary>\n");
        code.Append("    public static IReadOnlyList<double> Weights { get; } = [");
        code.AppendJoin(", ", result.Weights.Select(Literal));
        code.Append("];\n}\n");
        return code.ToString();
    }

    /// <summary>The repository's <c>src/Scoring/ListeningWeights.cs</c>, found from <paramref name="start"/> upwards; <c>null</c> outside the repository.</summary>
    public static string? FindWeightsFile(string start)
    {
        for (DirectoryInfo? folder = new(start); folder is not null; folder = folder.Parent)
        {
            string file = Path.Combine(folder.FullName, "src", "Scoring", "ListeningWeights.cs");
            if (File.Exists(Path.Combine(folder.FullName, "Mod2Xgm.slnx")) && File.Exists(file))
            {
                return file;
            }
        }

        return null;
    }

    private static FitResult NoFit(FitOutcome outcome, string reason, IReadOnlyList<FitAnswer> answers, int songs, int maxWeights, double? logMel) =>
        new(outcome, reason, answers.Count, songs, maxWeights, [], 0, CoinLogLoss, null, logMel, [.. answers.Select(a => new FitLine(a, null, MelGuess(a)))]);

    /// <summary>A C# literal that reads back as <paramref name="value"/> exactly.</summary>
    private static string Literal(double value)
    {
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static string Percent(double? value) => value is { } v ? v.ToString("0.0", CultureInfo.InvariantCulture) + " %" : "–";

    /// <summary>The version closer by log-mel alone: <c>a</c>, <c>b</c> or <c>tie</c>.</summary>
    private static string MelGuess(FitAnswer answer) =>
        answer.A.MelDb < answer.B.MelDb ? "a" : answer.B.MelDb < answer.A.MelDb ? "b" : "tie";

    /// <summary>
    /// Percent of the a/b answers guessed, from <paramref name="chanceOfA"/>: a chance over ½ guesses A, exactly ½ counts
    /// half. <c>null</c> without a/b answers.
    /// </summary>
    private static double? Agreement(IReadOnlyList<FitAnswer> answers, Func<int, double> chanceOfA)
    {
        int[] decided = [.. Enumerable.Range(0, answers.Count).Where(i => answers[i].Pick is "a" or "b")];
        if (decided.Length == 0)
        {
            return null;
        }

        double right = decided.Sum(i =>
        {
            double p = chanceOfA(i);
            return Math.Abs(p - 0.5) < 1e-12 ? 0.5 : (p > 0.5) == (answers[i].Pick == "a") ? 1 : 0;
        });
        return 100 * right / decided.Length;
    }

    /// <summary><c>f(B) − f(A)</c>: positive where B is farther from the original.</summary>
    private static double[] Difference(FitAnswer answer)
    {
        IReadOnlyList<double> a = answer.A.Values;
        IReadOnlyList<double> b = answer.B.Values;
        return [.. a.Select((value, j) => b[j] - value)];
    }

    /// <summary>Each feature's root-mean-square difference, the scale it is divided by; 0 for a feature that never differs.</summary>
    private static double[] Scales(double[][] differences)
    {
        int count = differences[0].Length;
        double[] scales = new double[count];
        for (int j = 0; j < count; j++)
        {
            scales[j] = Math.Sqrt(differences.Average(d => d[j] * d[j]));
        }

        return scales;
    }

    /// <summary>Each answer's chance of A, fitted with its song left out, and the mean log-loss of those chances.</summary>
    private static (double[] Chances, double LogLoss) LeftOut(double[][] differences, double[] targets, string[] groups, double[] scales, int[] features, double lambda)
    {
        double[] chances = new double[targets.Length];
        foreach (string song in groups.Distinct(StringComparer.Ordinal))
        {
            double[] w = Solve(differences, targets, scales, features, lambda, Enumerable.Range(0, targets.Length).Where(i => groups[i] != song));
            for (int i = 0; i < targets.Length; i++)
            {
                if (groups[i] == song)
                {
                    chances[i] = Chance(w, differences[i], scales, features);
                }
            }
        }

        double loss = 0;
        for (int i = 0; i < targets.Length; i++)
        {
            double p = Math.Clamp(chances[i], 1e-12, 1 - 1e-12);
            loss -= (targets[i] * Math.Log(p)) + ((1 - targets[i]) * Math.Log(1 - p));
        }

        return (chances, loss / targets.Length);
    }

    private static double Chance(double[] w, double[] difference, double[] scales, int[] features)
    {
        double z = 0;
        for (int k = 0; k < features.Length; k++)
        {
            z += w[k] * difference[features[k]] / scales[features[k]];
        }

        return 1 / (1 + Math.Exp(-z));
    }

    /// <summary>
    /// Newton's method on the L2-regularized log-loss of the answers in <paramref name="rows"/>, on the standardized
    /// <paramref name="features"/>; the regularization keeps the Hessian positive definite, so it always converges.
    /// </summary>
    private static double[] Solve(double[][] differences, double[] targets, double[] scales, int[] features, double lambda, IEnumerable<int> rows)
    {
        int[] used = [.. rows];
        int n = features.Length;
        double[] w = new double[n];
        for (int iteration = 0; iteration < 100; iteration++)
        {
            double[] gradient = new double[n];
            double[,] hessian = new double[n, n];
            for (int k = 0; k < n; k++)
            {
                gradient[k] = lambda * w[k];
                hessian[k, k] = lambda;
            }

            foreach (int i in used)
            {
                double p = Chance(w, differences[i], scales, features);
                double weight = p * (1 - p);
                for (int k = 0; k < n; k++)
                {
                    double xk = differences[i][features[k]] / scales[features[k]];
                    gradient[k] += (p - targets[i]) * xk;
                    for (int m = 0; m < n; m++)
                    {
                        hessian[k, m] += weight * xk * differences[i][features[m]] / scales[features[m]];
                    }
                }
            }

            double[] step = Cholesky(hessian, gradient);
            double size = 0;
            for (int k = 0; k < n; k++)
            {
                w[k] -= step[k];
                size = Math.Max(size, Math.Abs(step[k]));
            }

            if (size < 1e-10)
            {
                break;
            }
        }

        return w;
    }

    /// <summary>Solves <c>H x = g</c> for a symmetric positive-definite <paramref name="h"/>.</summary>
    private static double[] Cholesky(double[,] h, double[] g)
    {
        int n = g.Length;
        double[,] l = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double sum = h[i, j];
                for (int k = 0; k < j; k++)
                {
                    sum -= l[i, k] * l[j, k];
                }

                l[i, j] = i == j ? Math.Sqrt(sum) : sum / l[j, j];
            }
        }

        double[] y = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sum = g[i];
            for (int k = 0; k < i; k++)
            {
                sum -= l[i, k] * y[k];
            }

            y[i] = sum / l[i, i];
        }

        double[] x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double sum = y[i];
            for (int k = i + 1; k < n; k++)
            {
                sum -= l[k, i] * x[k];
            }

            x[i] = sum / l[i, i];
        }

        return x;
    }
}

/// <summary>
/// <c>--fit</c> after the remake (docs/harness.md#the-fit): takes the answered rows whose clips came out as David heard them,
/// fits, writes <c>listening/fit.txt</c>, and after a fit rewrites <c>src/Scoring/ListeningWeights.cs</c>.
/// </summary>
internal static class ListeningFitRun
{
    /// <summary>
    /// Fits on <paramref name="remake"/>'s rows and reports through <paramref name="logger"/>; writes the weights to
    /// <paramref name="weightsFile"/> after a fit, or says why not when it is <c>null</c>. Always 0: rows left out are listed.
    /// </summary>
    public static int Run(RemakeResult remake, string outputDirectory, string? weightsFile, DateOnly date, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(remake);
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
                skipped.Add(r.Row.Name + ": " + Why(r));
            }
        }

        FitResult result = ListeningFit.Fit(answers);
        List<string> lines = [.. ListeningFit.Describe(result), .. skipped.Select(s => "Left out " + s)];
        if (result.Outcome == FitOutcome.Fitted)
        {
            if (weightsFile is null)
            {
                lines.Add("Weights not written: src/Scoring/ListeningWeights.cs not found above the current folder");
            }
            else
            {
                File.WriteAllText(weightsFile, ListeningFit.WeightsFile(result, date));
                lines.Add("Weights written to " + weightsFile);
            }
        }

        string folder = Path.Combine(outputDirectory, "listening");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "fit.txt"), string.Join('\n', lines) + "\n");
        foreach (string line in lines)
        {
            logger.Information("{Line:l}", line);
        }

        return 0;
    }

    /// <summary>Why a row can't teach the fit, in words.</summary>
    private static string Why(RemadeRow row) =>
        row.Row.Pick.Length == 0 ? "not answered"
        : row.Row.Sha256A.Length == 0 ? "no hashes yet (a new question)"
        : !row.Heard ? "its clips differ from those David heard"
        : "no features (no reference player)";
}
