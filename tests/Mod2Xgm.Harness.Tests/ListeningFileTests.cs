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

    /// <summary>Every <c>neither</c> row keeps David's words saying why both versions were unacceptable (ADR 0111, plan T9.4s1).</summary>
    [Fact]
    public void Parse_OfCommittedAnswers_NeitherRowsKeepTheirWords()
    {
        // Arrange
        string text = File.ReadAllText(TestFiles.ListeningAnswers);

        // Act
        ListeningRow[] neither = [.. AnswersFile.Parse(text).Where(r => r.Pick == "neither")];

        // Assert
        Assert.Equal(["r23-q5", "r23-q6", "r24-q1", "r25-q7", "r25-q8", "r25-q9", "r25-q10", "r31-q6", "r31-q7", "r31-q8", "r31-q9"], neither.Select(r => r.Name));
        Assert.All(neither, r => Assert.NotEmpty(r.Words));
    }
}
