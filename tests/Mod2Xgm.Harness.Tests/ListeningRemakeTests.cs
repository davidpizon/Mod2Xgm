using Serilog.Core;
using Mod2Xgm.Output;

namespace Mod2Xgm.Harness.Tests;

/// <summary>Tests for <see cref="ListeningRemake"/> on a real corpus song: what it refuses, and the XGM render it still cuts.</summary>
[Trait("Category", "Integration")]
public sealed class ListeningRemakeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mod2xgm-remake-" + Guid.NewGuid().ToString("N"));

    /// <summary>Makes the scratch folder.</summary>
    public ListeningRemakeTests() => Directory.CreateDirectory(_folder);

    /// <summary>Removes the scratch folder.</summary>
    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>A rule without the optimizer, the only mapper that reads it, is refused rather than recorded as a version it didn't make.</summary>
    [Fact]
    public void Run_WithRuleButNoOptimizer_Throws()
    {
        // Arrange
        string answers = Answers("xgm2", "rule:half-speed=1", "");
        ListeningRemake sut = new([TestFiles.ShortSong], _folder, null, Logger.None);

        // Act
        HarnessException ex = Assert.Throws<HarnessException>(() => sut.Run(answers, NoProgress.Instance));

        // Assert
        Assert.Contains("--mapping optimizer", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A render left by an earlier run never stands in: a recipe that converts nothing fails though the old file was there.</summary>
    [Fact]
    public void Run_WithRecipeThatConvertsNothing_IgnoresTheOldRender()
    {
        // Arrange
        string answers = Answers("xgm2", "--help", "");
        string work = Path.Combine(_folder, "listening", "work");
        Directory.CreateDirectory(work);
        File.WriteAllBytes(Path.Combine(work, "r1-q1-a.genesis.wav"), [1, 2, 3]);
        ListeningRemake sut = new([TestFiles.ShortSong], _folder, null, Logger.None);

        // Act
        HarnessException ex = Assert.Throws<HarnessException>(() => sut.Run(answers, NoProgress.Instance));

        // Assert
        Assert.Contains("made no render", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>XGM has no writer yet, but its render is made: the row's clips are cut from it and the run succeeds.</summary>
    [Fact]
    public void Run_WithXgmRow_CutsClipsFromTheRender()
    {
        // Arrange
        string answers = Answers("xgm", "", "");
        ListeningRemake sut = new([TestFiles.ShortSong], _folder, null, Logger.None);

        // Act
        int code = sut.Run(answers, NoProgress.Instance);

        // Assert
        Assert.Equal(0, code);
        Assert.Equal(44 + (44_100 * 4), new FileInfo(Path.Combine(_folder, "listening", "r1-q1-a.wav")).Length);
        Assert.Single(AnswersFile.Parse(File.ReadAllText(Path.Combine(_folder, "listening", "remade.tsv"))), r => r.Sha256A.Length == 64);
    }

    /// <summary>
    /// With a reference player, each row's two clips are scored against the original's clip: <c>features.tsv</c> has a
    /// line per clip with every measure, the perceptual features included (plan T9.4k1).
    /// </summary>
    [Fact]
    public void Run_WithReference_WritesFeaturesOfBothClips()
    {
        // Arrange
        string answers = Answers("xgm2", "", "");
        ListeningRemake sut = new([TestFiles.ShortSong], _folder, new ToneReference(), Logger.None);

        // Act
        int code = sut.Run(answers, NoProgress.Instance);

        // Assert
        Assert.Equal(0, code);
        string[] lines = File.ReadAllText(Path.Combine(_folder, "listening", "features.tsv")).Split('\n');
        Assert.Equal(ListeningRemake.FeaturesHeader, lines[0]);
        Assert.StartsWith("r1-q1\ta\t", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("r1-q1\tb\t", lines[2], StringComparison.Ordinal);
        Assert.All(lines[1].Split('\t')[..^1], Assert.NotEmpty);
        Assert.Equal(ListeningRemake.FeaturesHeader.Split('\t').Length, lines[1].Split('\t').Length);
        Assert.Equal(string.Empty, lines[3]);
    }

    /// <summary>Without a reference player there is nothing to score the clips against, so no <c>features.tsv</c> is written.</summary>
    [Fact]
    public void Run_WithoutReference_WritesNoFeatures()
    {
        // Arrange
        string answers = Answers("xgm2", "", "");
        ListeningRemake sut = new([TestFiles.ShortSong], _folder, null, Logger.None);

        // Act
        sut.Run(answers, NoProgress.Instance);

        // Assert
        Assert.False(File.Exists(Path.Combine(_folder, "listening", "features.tsv")));
    }

    /// <summary>A reference player that plays a two-second tone, enough to cut a one-second clip from.</summary>
    private sealed class ToneReference : IReferenceRenderer
    {
        public string Name => "tone reference";

        public ReferenceRender Render(byte[] mod, int sampleRate) => new(TestFiles.Tone(2.0, sampleRate), 2.0);

        public IReadOnlyList<double> RowTimes(byte[] mod, IReadOnlyList<(int Order, int Row)> rows) => [.. rows.Select(_ => 0.0)];
    }

    /// <summary>An answers file with one unanswered one-second question on the short song.</summary>
    private string Answers(string format, string recipeA, string recipeB)
    {
        string path = Path.Combine(_folder, "answers.tsv");
        string row = string.Join('\t', "2026-11-10", "r1", "q1", Path.GetFileName(TestFiles.ShortSong), "1", "1", format, recipeA, recipeB, "", "", "", "", "", "", "");
        File.WriteAllText(path, AnswersFile.Header + "\n" + row + "\n");
        return path;
    }
}
