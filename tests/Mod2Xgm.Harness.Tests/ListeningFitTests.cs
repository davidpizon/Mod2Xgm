using Mod2Xgm.Scoring;
using Serilog.Core;

namespace Mod2Xgm.Harness.Tests;

/// <summary>Tests for <see cref="ListeningFit"/> on made-up answers (plan T9.4k2).</summary>
public sealed class ListeningFitTests
{
    /// <summary>Five answers, as the file has today, are too few for one weight: nothing is fitted, and log-mel's guesses are still reported.</summary>
    [Fact]
    public void Fit_WithFiveAnswers_ReportsNotEnoughAnswers()
    {
        // Arrange
        FitAnswer[] answers = Answers(5, songs: 5, NmrDecides);

        // Act
        FitResult sut = ListeningFit.Fit(answers);

        // Assert
        Assert.Equal(FitOutcome.NotEnoughAnswers, sut.Outcome);
        Assert.Contains("not enough answers", sut.Reason, StringComparison.Ordinal);
        Assert.Empty(sut.Weights);
        Assert.Equal(5, sut.Lines.Count);
        Assert.All(sut.Lines, l => Assert.Null(l.LeftOutChance));
    }

    /// <summary>Answers from one song can't be checked by leaving a song out, so nothing is fitted.</summary>
    [Fact]
    public void Fit_WithOneSong_ReportsNotEnoughSongs()
    {
        // Arrange
        FitAnswer[] answers = Answers(20, songs: 1, NmrDecides);

        // Act
        FitResult sut = ListeningFit.Fit(answers);

        // Assert
        Assert.Equal(FitOutcome.NotEnoughAnswers, sut.Outcome);
        Assert.Contains("songs", sut.Reason, StringComparison.Ordinal);
    }

    /// <summary>When the noise-to-mask ratio decides every pick and log-mel only half of them, the fit keeps NMR with a positive weight and guesses better than log-mel.</summary>
    [Fact]
    public void Fit_WhenOneFeatureDecidesThePicks_KeepsIt()
    {
        // Arrange
        FitAnswer[] answers = Answers(40, songs: 4, NmrDecides);
        int nmr = 3;

        // Act
        FitResult sut = ListeningFit.Fit(answers);

        // Assert
        Assert.Equal(FitOutcome.Fitted, sut.Outcome);
        Assert.True(sut.Weights[nmr] > 0, "a farther NMR should lose");
        Assert.True(sut.Agreement >= 95, $"agreement {sut.Agreement}");
        Assert.True(sut.Agreement > sut.LogMelAgreement, $"learned {sut.Agreement}, log-mel {sut.LogMelAgreement}");
        Assert.True(sut.LeftOutLogLoss < Math.Log(2));
        Assert.All(sut.Lines, l => Assert.NotNull(l.LeftOutChance));
    }

    /// <summary>When every answer is "can't tell", no feature beats a coin, so nothing is fitted.</summary>
    [Fact]
    public void Fit_WithOnlyCantTell_ReportsNothingHelps()
    {
        // Arrange
        FitAnswer[] answers = Answers(20, songs: 4, _ => "same");

        // Act
        FitResult sut = ListeningFit.Fit(answers);

        // Assert
        Assert.Equal(FitOutcome.NothingHelps, sut.Outcome);
        Assert.Null(sut.Agreement);
        Assert.Null(sut.LogMelAgreement);
    }

    /// <summary>
    /// "Can't tell" answers decided by the smaller file teach the fit like a/b picks (docs/adr/0106-cant-tell-smaller-wins.md):
    /// here the smaller version always has the lower NMR, so the fit keeps NMR where the same answers undecided teach nothing.
    /// </summary>
    [Fact]
    public void Fit_WithCantTellDecidedBySize_LearnsFromThem()
    {
        // Arrange
        FitAnswer[] answers = [.. Answers(40, songs: 4, NmrDecides).Select(a => a with { Pick = "same", Smaller = a.Pick })];
        int nmr = 3;

        // Act
        FitResult sut = ListeningFit.Fit(answers);

        // Assert
        Assert.Equal(FitOutcome.Fitted, sut.Outcome);
        Assert.True(sut.Weights[nmr] > 0, "a farther NMR should lose");
        Assert.True(sut.Agreement >= 95, $"agreement {sut.Agreement}");
    }

    /// <summary>A "can't tell" goes to the smaller file; an a/b pick never changes, and equal sizes leave it at half each.</summary>
    [Theory]
    [InlineData("same", "a", "a", 1.0)]
    [InlineData("same", "b", "b", 0.0)]
    [InlineData("same", "", "same", 0.5)]
    [InlineData("a", "b", "a", 1.0)]
    [InlineData("b", "a", "b", 0.0)]
    public void Decided_WithPickAndSmaller_FollowsTheRule(string pick, string smaller, string decided, double target)
    {
        // Arrange
        ListeningFeatures features = new(5, 30, 0.1, -10, 0.2, 3, 6);

        // Act
        FitAnswer sut = new("r1-q1", "song.mod", pick, features, features, smaller);

        // Assert
        Assert.Equal(decided, sut.Decided);
        Assert.Equal(target, sut.Target);
    }

    /// <summary>The smaller of two sizes names its side; equal or unknown sizes name none.</summary>
    [Theory]
    [InlineData(10L, 20L, "a")]
    [InlineData(20L, 10L, "b")]
    [InlineData(10L, 10L, "")]
    [InlineData(null, 10L, "")]
    [InlineData(10L, null, "")]
    public void SmallerOf_WithSizes_NamesTheSmaller(long? bytesA, long? bytesB, string expected)
    {
        // Act
        string sut = FitAnswer.SmallerOf(bytesA, bytesB);

        // Assert
        Assert.Equal(expected, sut);
    }

    /// <summary>A report shows a "can't tell" the size decided as such, so the answer David gave stays visible.</summary>
    [Theory]
    [InlineData("a", "b", "a")]
    [InlineData("same", "b", "can't tell → b (smaller)")]
    [InlineData("same", "", "can't tell")]
    public void Describe_WithPick_ShowsWhatDecidedIt(string pick, string smaller, string expected)
    {
        // Arrange
        ListeningFeatures features = new(5, 30, 0.1, -10, 0.2, 3, 6);
        FitAnswer sut = new("r1-q1", "song.mod", pick, features, features, smaller);

        // Act
        string text = sut.Describe();

        // Assert
        Assert.Equal(expected, text);
    }

    /// <summary>A remade row's answer takes the smaller side from the two output sizes.</summary>
    [Fact]
    public void From_WithRowSizes_SetsSmaller()
    {
        // Arrange
        ListeningFeatures features = new(5, 30, 0.1, -10, 0.2, 3, 6);
        ListeningRow row = new("2026-11-13", "r23", "q1", "song.mod", 0, 20, "xgm2", "", "", "c", "x", "y", "same", "", "", "");

        // Act
        FitAnswer sut = FitAnswer.From(new RemadeRow(row, true, features, features, 900, 800), features, features);

        // Assert
        Assert.Equal("r23-q1", sut.Name);
        Assert.Equal("b", sut.Smaller);
        Assert.Equal("b", sut.Decided);
    }

    /// <summary>The report has the summary, the kept weights and one line per answer.</summary>
    [Fact]
    public void Describe_AfterFit_ListsWeightsAndEveryAnswer()
    {
        // Arrange
        FitResult result = ListeningFit.Fit(Answers(40, songs: 4, NmrDecides));

        // Act
        IReadOnlyList<string> sut = ListeningFit.Describe(result);

        // Assert
        Assert.StartsWith("Answers: 40 from 4 songs; at most 4 weights", sut[0], StringComparison.Ordinal);
        Assert.Contains(sut, l => l.StartsWith("  nmr_db: ", StringComparison.Ordinal));
        Assert.Contains(sut, l => l.StartsWith("Guessed David's pick", StringComparison.Ordinal));
        Assert.Equal(40, sut.Count(l => l.StartsWith("  r1-q", StringComparison.Ordinal)));
    }

    /// <summary>Without a fit the report says why, and still lists every answer with log-mel's guess.</summary>
    [Fact]
    public void Describe_WithoutFit_SaysWhy()
    {
        // Arrange
        FitResult result = ListeningFit.Fit(Answers(5, songs: 5, NmrDecides));

        // Act
        IReadOnlyList<string> sut = ListeningFit.Describe(result);

        // Assert
        Assert.StartsWith("Not fitted: not enough answers", sut[1], StringComparison.Ordinal);
        Assert.Contains("learned gave it –, log-mel says", sut[^1], StringComparison.Ordinal);
    }

    /// <summary>The weights file has the date, the counts and one literal per feature that reads back exactly.</summary>
    [Fact]
    public void WeightsFile_AfterFit_WritesEveryWeight()
    {
        // Arrange
        FitResult result = ListeningFit.Fit(Answers(40, songs: 4, NmrDecides));

        // Act
        string sut = ListeningFit.WeightsFile(result, new DateOnly(2026, 11, 10));

        // Assert
        Assert.Contains("public const string Fitted = \"2026-11-10\";", sut, StringComparison.Ordinal);
        Assert.Contains("public const int Answers = 40;", sut, StringComparison.Ordinal);
        Assert.Contains("public const int Songs = 4;", sut, StringComparison.Ordinal);
        string list = sut[(sut.IndexOf("= [", StringComparison.Ordinal) + 3)..sut.IndexOf("];", StringComparison.Ordinal)];
        double[] weights = [.. list.Split(", ").Select(w => double.Parse(w, System.Globalization.CultureInfo.InvariantCulture))];
        Assert.Equal(result.Weights, weights);
        Assert.Contains("0.0", list, StringComparison.Ordinal);
    }

    /// <summary>Only a fit has weights to write.</summary>
    [Fact]
    public void WeightsFile_WithoutFit_Throws()
    {
        // Arrange
        FitResult result = ListeningFit.Fit(Answers(5, songs: 5, NmrDecides));

        // Act
        ArgumentException ex = Assert.Throws<ArgumentException>(() => ListeningFit.WeightsFile(result, new DateOnly(2026, 11, 10)));

        // Assert
        Assert.Equal("result", ex.ParamName);
    }

    /// <summary>A fixed set holding the feature that decides the picks guesses every one on songs left out.</summary>
    [Fact]
    public void Trial_WithDecidingFeature_GuessesEveryPick()
    {
        // Arrange
        FitAnswer[] answers = Answers(20, songs: 4, NmrDecides);
        double[][] differences = [.. answers.Select(a => a.A.Values.Select((v, j) => a.B.Values[j] - v).ToArray())];

        // Act
        FitTrial? sut = ListeningFit.Trial(answers, differences, [3]);

        // Assert
        Assert.NotNull(sut);
        Assert.Equal(100, sut.Agreement);
        Assert.True(sut.LogLoss < Math.Log(2));
    }

    /// <summary>A feature that never differs, or answers from one song, can't be compared.</summary>
    [Fact]
    public void Trial_WithConstantFeatureOrOneSong_IsNull()
    {
        // Arrange
        FitAnswer[] answers = Answers(20, songs: 4, NmrDecides);
        FitAnswer[] oneSong = Answers(20, songs: 1, NmrDecides);
        double[][] differences = [.. answers.Select(a => a.A.Values.Select((v, j) => a.B.Values[j] - v).ToArray())];

        // Act
        FitTrial? constant = ListeningFit.Trial(answers, differences, [1]);
        FitTrial? sut = ListeningFit.Trial(oneSong, differences, [3]);

        // Assert
        Assert.Null(constant);
        Assert.Null(sut);
    }

    /// <summary>A row of differences per answer is required.</summary>
    [Fact]
    public void Trial_WithMismatchedRows_Throws()
    {
        // Act
        ArgumentException ex = Assert.Throws<ArgumentException>(() => ListeningFit.Trial(Answers(4, songs: 2, NmrDecides), [], [0]));

        // Assert
        Assert.Equal("differences", ex.ParamName);
    }

    /// <summary>NMR farther in A than in B makes David pick B (stands in for a measure the ears follow).</summary>
    private static string NmrDecides(int i) => i % 2 == 0 ? "b" : "a";

    /// <summary>
    /// <paramref name="count"/> answers over <paramref name="songs"/> songs: in each, the version the pick loses has the
    /// higher NMR (by 1–3 dB), and log-mel differs the right way only every other pair.
    /// </summary>
    private static FitAnswer[] Answers(int count, int songs, Func<int, string> pick) =>
        [.. Enumerable.Range(0, count).Select(i => Answer(i, songs, pick(i)))];

    private static FitAnswer Answer(int i, int songs, string pick)
    {
        double gap = 1 + (i % 3);
        double mel = i % 4 < 2 ? 0.5 : -0.5;
        ListeningFeatures closer = new(5 + mel, 30, 0.1, -10, 0.2, 3, 6);
        ListeningFeatures farther = new(5 - mel, 30, 0.1, -10 + gap, 0.2, 3, 6);
        return pick switch
        {
            "a" => new FitAnswer($"r1-q{i}", $"song{i % songs}.mod", pick, closer, farther),
            "b" => new FitAnswer($"r1-q{i}", $"song{i % songs}.mod", pick, farther, closer),
            _ => new FitAnswer($"r1-q{i}", $"song{i % songs}.mod", pick, closer, closer with { MelDb = 5 + gap, NoiseToMaskDb = -10 + gap }),
        };
    }
}

/// <summary>Tests for <see cref="ListeningFitRun"/> and <see cref="ListeningFit.FindWeightsFile"/>: what is left out, what is written.</summary>
[Trait("Category", "Integration")]
public sealed class ListeningFitRunTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <summary>Removes the scratch folder.</summary>
    public void Dispose() => _temp.Dispose();

    /// <summary>After a fit the weights file is rewritten and <c>fit.txt</c> lists rows that couldn't teach the fit, and why.</summary>
    [Fact]
    public void Run_WithEnoughAnswers_WritesWeightsAndReport()
    {
        // Arrange
        string weights = _temp.Combine("ListeningWeights.cs");
        RemakeResult remake = new(1, [.. Rows(40), Row("r9-q1", heard: false, pick: "a"), Row("r9-q2", heard: true, pick: "")]);

        // Act
        int code = ListeningFitRun.Run(remake, _temp.Path, weights, new DateOnly(2026, 11, 10), Logger.None);

        // Assert
        Assert.Equal(0, code);
        Assert.Contains("Fitted = \"2026-11-10\"", File.ReadAllText(weights), StringComparison.Ordinal);
        string report = File.ReadAllText(_temp.Combine("listening", "fit.txt"));
        Assert.Contains("Answers: 40 from 4 songs", report, StringComparison.Ordinal);
        Assert.Contains("Left out r9-q1: its clips differ from those David heard", report, StringComparison.Ordinal);
        Assert.Contains("Left out r9-q2: not answered", report, StringComparison.Ordinal);
    }

    /// <summary>With too few answers the weights file is left alone.</summary>
    [Fact]
    public void Run_WithTooFewAnswers_LeavesWeightsAlone()
    {
        // Arrange
        string weights = _temp.Combine("ListeningWeights.cs");
        File.WriteAllText(weights, "unchanged");
        RemakeResult remake = new(0, Rows(5));

        // Act
        ListeningFitRun.Run(remake, _temp.Path, weights, new DateOnly(2026, 11, 10), Logger.None);

        // Assert
        Assert.Equal("unchanged", File.ReadAllText(weights));
        Assert.Contains("Not fitted: not enough answers", File.ReadAllText(_temp.Combine("listening", "fit.txt")), StringComparison.Ordinal);
    }

    /// <summary>Outside the repository there is no weights file to rewrite, so a fit says so.</summary>
    [Fact]
    public void Run_WithoutWeightsFile_SaysNotWritten()
    {
        // Act
        ListeningFitRun.Run(new RemakeResult(0, Rows(40)), _temp.Path, null, new DateOnly(2026, 11, 10), Logger.None);

        // Assert
        Assert.Contains("Weights not written", File.ReadAllText(_temp.Combine("listening", "fit.txt")), StringComparison.Ordinal);
    }

    /// <summary>The weights file is found from inside the repository, and not from a folder outside it.</summary>
    [Fact]
    public void FindWeightsFile_InsideAndOutsideRepository_FindsItOnlyInside()
    {
        // Act
        string? inside = ListeningFit.FindWeightsFile(AppContext.BaseDirectory);
        string? outside = ListeningFit.FindWeightsFile(_temp.Path);

        // Assert
        Assert.NotNull(inside);
        Assert.EndsWith(Path.Combine("src", "Scoring", "ListeningWeights.cs"), inside, StringComparison.Ordinal);
        Assert.Null(outside);
    }

    /// <summary><paramref name="count"/> heard rows over 4 songs, whose pick always goes against the higher NMR.</summary>
    private static RemadeRow[] Rows(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Row($"r1-q{i}", heard: true, pick: i % 2 == 0 ? "b" : "a", song: $"song{i % 4}.mod", gap: 1 + (i % 3)))];

    private static RemadeRow Row(string name, bool heard, string pick, string song = "song0.mod", double gap = 1)
    {
        ListeningFeatures closer = new(5, 30, 0.1, -10, 0.2, 3, 6);
        ListeningFeatures farther = closer with { NoiseToMaskDb = -10 + gap };
        ListeningRow row = new("2026-11-10", name.Split('-')[0], name.Split('-')[1], song, 0, 1, "xgm2", "", "", "c", "x", "y", pick, "", "", "");
        return pick == "b" ? new RemadeRow(row, heard, farther, closer) : new RemadeRow(row, heard, closer, farther);
    }
}
