using Mod2Xgm.Scoring;
using Serilog.Core;

namespace Mod2Xgm.Harness.Tests;

/// <summary>Tests for the hold-out score and its bar (<see cref="HoldoutScore"/>, plan T9.4l, docs/adr/0105-hold-out-songs.md).</summary>
[Trait("Category", "Integration")]
public sealed class HoldoutTests
{
    /// <summary>Weights that keep only the envelope modulation distance, as today's fit does.</summary>
    private static readonly double[] ModulationOnly = [0, 0, 0, 0, 0, 6, 0];

    /// <summary>With 30 a/b picks the learned score always guesses and log-mel guesses half, the bar is passed.</summary>
    [Fact]
    public void Score_WithThirtyPairsTheLearnedScoreGuesses_Passes()
    {
        // Arrange
        FitAnswer[] answers = Answers(30);

        // Act
        HoldoutResult sut = HoldoutScore.Score(answers, ModulationOnly);

        // Assert
        Assert.True(sut.Passed);
        Assert.Equal(30, sut.Pairs);
        Assert.Equal(30, sut.AbPicks);
        Assert.Equal(100, sut.LearnedAgreement);
        Assert.Equal(50, sut.LogMelAgreement);
    }

    /// <summary>The same agreement on 10 pairs isn't enough: the bar asks for 30.</summary>
    [Fact]
    public void Score_WithTenPairs_IsNotPassed()
    {
        // Act
        HoldoutResult sut = HoldoutScore.Score(Answers(10), ModulationOnly);

        // Assert
        Assert.False(sut.Passed);
        Assert.Contains("20 more pairs needed", string.Join('\n', HoldoutScore.Describe(sut, fitted: true)), StringComparison.Ordinal);
    }

    /// <summary>When log-mel guesses as well as the learned score, a high agreement still fails the 10-point lead.</summary>
    [Fact]
    public void Score_WhenLogMelGuessesAsWell_IsNotPassed()
    {
        // Arrange
        FitAnswer[] answers = [.. Answers(30).Select(a => a with { A = a.A with { MelDb = a.Pick == "a" ? 1 : 9 }, B = a.B with { MelDb = a.Pick == "a" ? 9 : 1 } })];

        // Act
        HoldoutResult sut = HoldoutScore.Score(answers, ModulationOnly);

        // Assert
        Assert.False(sut.Passed);
        Assert.Equal(100, sut.LogMelAgreement);
        Assert.Contains("less than 10 points above log-mel", string.Join('\n', HoldoutScore.Describe(sut, fitted: true)), StringComparison.Ordinal);
    }

    /// <summary>"Can't tell" answers count as pairs but for neither score's agreement.</summary>
    [Fact]
    public void Score_WithCantTellAnswers_CountsThemAsPairsOnly()
    {
        // Arrange
        FitAnswer[] answers = [.. Answers(30), .. Answers(4).Select(a => a with { Name = a.Name + "s", Pick = "same" })];

        // Act
        HoldoutResult sut = HoldoutScore.Score(answers, ModulationOnly);

        // Assert
        Assert.Equal(34, sut.Pairs);
        Assert.Equal(30, sut.AbPicks);
        Assert.Equal(100, sut.LearnedAgreement);
    }

    /// <summary>
    /// A "can't tell" the smaller file decides counts as a pick of the smaller version (docs/adr/0106-cant-tell-smaller-wins.md):
    /// here the smaller is the version the learned score finds farther, so each one is a miss for it.
    /// </summary>
    [Fact]
    public void Score_WithCantTellDecidedBySize_CountsItAsAPick()
    {
        // Arrange
        FitAnswer[] answers = [.. Answers(30), .. Answers(4).Select(a => a with { Name = a.Name + "s", Pick = "same", Smaller = a.Pick == "a" ? "b" : "a" })];

        // Act
        HoldoutResult sut = HoldoutScore.Score(answers, ModulationOnly);

        // Assert
        Assert.Equal(34, sut.Pairs);
        Assert.Equal(34, sut.AbPicks);
        Assert.Equal(100.0 * 30 / 34, sut.LearnedAgreement);
        Assert.Contains("(smaller)", string.Join('\n', HoldoutScore.Describe(sut, fitted: true)), StringComparison.Ordinal);
    }

    /// <summary>On a "can't tell" the size decided, finding both versions equally close is right; on an a/b pick it is a miss.</summary>
    [Fact]
    public void Score_WithEqualDistances_TieIsRightOnlyForCantTell()
    {
        // Arrange
        ListeningFeatures same = new(5, 30, 0.1, -10, 0.2, 3, 6);
        FitAnswer[] answers =
        [
            new("r22-q1", "song1.mod", "same", same, same, "a"),
            new("r22-q2", "song2.mod", "b", same, same, "a"),
        ];

        // Act
        HoldoutResult sut = HoldoutScore.Score(answers, ModulationOnly);

        // Assert
        Assert.Equal(50, sut.LearnedAgreement);
        Assert.Equal(50, sut.LogMelAgreement);
    }

    /// <summary>Without fitted weights the learned score guesses nothing and the bar can't be passed.</summary>
    [Fact]
    public void Score_WithoutWeights_IsNotPassed()
    {
        // Act
        HoldoutResult sut = HoldoutScore.Score(Answers(30), [0, 0, 0, 0, 0, 0, 0]);

        // Assert
        Assert.False(sut.Passed);
        Assert.Equal(0, sut.LearnedAgreement);
        Assert.All(sut.Lines, l => Assert.Equal("tie", l.LearnedGuess));
    }

    /// <summary>The hold-out songs are the folder's .mod files, by name in any case; a missing folder has none.</summary>
    [Fact]
    public void In_WithFolderAndMissingFolder_ListsModFiles()
    {
        // Arrange
        using TempDirectory temp = new();
        File.WriteAllBytes(temp.Combine("one.mod"), [0]);
        File.WriteAllBytes(temp.Combine("notes.txt"), [0]);

        // Act
        IReadOnlySet<string> sut = HoldoutSongs.In(temp.Path);
        IReadOnlySet<string> missing = HoldoutSongs.In(temp.Combine("nowhere"));

        // Assert
        Assert.Single(sut);
        Assert.Contains("ONE.MOD", sut);
        Assert.Empty(missing);
    }

    /// <summary>The run writes <c>holdout.txt</c> with the verdict and lists rows that can't be scored, and why.</summary>
    [Fact]
    public void Run_WithRows_WritesReport()
    {
        // Arrange
        using TempDirectory temp = new();
        FitAnswer answer = Answers(1)[0];
        ListeningRow row = new("2026-11-12", "r22", "q1", answer.Song, 0, 20, "xgm2", "", "", "c", "x", "y", "a", "", "", "");
        RemakeResult remake = new(0, [new RemadeRow(row, true, answer.A, answer.B), new RemadeRow(row with { Question = "q2", Pick = "" }, true, answer.A, answer.B)]);

        // Act
        int code = HoldoutRun.Run(remake, temp.Path, ModulationOnly, Logger.None);

        // Assert
        Assert.Equal(0, code);
        string report = File.ReadAllText(temp.Combine("listening", "holdout.txt"));
        Assert.Contains("Hold-out: 1 answered pairs from 1 songs, 1 of them a/b picks", report, StringComparison.Ordinal);
        Assert.Contains("Verdict: not passed yet", report, StringComparison.Ordinal);
        Assert.Contains("Left out r22-q2: not answered", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// <paramref name="count"/> a/b answers over 10 songs. The picked version always has the lower envelope modulation
    /// distance; it has the lower log-mel distance only in every other answer.
    /// </summary>
    private static FitAnswer[] Answers(int count) =>
        [.. Enumerable.Range(0, count).Select(i =>
        {
            string pick = i % 3 == 0 ? "b" : "a";
            bool melAgrees = i % 2 == 0;
            ListeningFeatures picked = new(melAgrees ? 4 : 6, 30, 0.1, -10, 0.2, 2, 6);
            ListeningFeatures other = picked with { MelDb = melAgrees ? 6 : 4, ModulationDb = 4 };
            return pick == "a"
                ? new FitAnswer($"r22-q{i}", $"song{i % 10}.mod", pick, picked, other)
                : new FitAnswer($"r22-q{i}", $"song{i % 10}.mod", pick, other, picked);
        })];
}
