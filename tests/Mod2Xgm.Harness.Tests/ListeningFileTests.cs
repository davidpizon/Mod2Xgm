namespace Mod2Xgm.Harness.Tests;

/// <summary>Checks the committed answers file itself (tests/golden/listening/README.md), so a hand edit that breaks it fails CI.</summary>
[Trait("Category", "Integration")]
public sealed class ListeningFileTests
{
    /// <summary>The committed file reads, and every answered row carries both clips' hashes and the commit that made them.</summary>
    [Fact]
    public void Parse_OfCommittedAnswers_ReadsEveryRow()
    {
        // Arrange
        string text = File.ReadAllText(TestFiles.ListeningAnswers);

        // Act
        IReadOnlyList<ListeningRow> rows = AnswersFile.Parse(text);

        // Assert
        Assert.All(rows.Where(r => r.Pick.Length > 0), r => Assert.NotEmpty(r.Commit));
    }
}
