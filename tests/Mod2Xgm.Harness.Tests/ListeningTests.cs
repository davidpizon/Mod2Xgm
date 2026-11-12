using System.Buffers.Binary;

namespace Mod2Xgm.Harness.Tests;

/// <summary>Tests for the listening answers file, its recipes and its clips (docs/adr/0097-learned-listening-score.md, plan T9.4j).</summary>
public sealed class ListeningTests
{
    /// <summary>A row with every column filled, as an answered question is.</summary>
    private const string AnsweredRow = "2026-11-10\tr20\tq1\tcancan.mod\t43.5\t20\txgm2\t--mapping optimizer\t--mapping optimizer rule:half-speed=3\tabc123\taa\tbb\tb\t\t4\tthe bass is duller";

    /// <summary>A file read and written again comes out the same, numbers and empty cells included.</summary>
    [Fact]
    public void Format_AfterParse_GivesTheSameFile()
    {
        // Arrange
        string text = AnswersFile.Header + "\n" + AnsweredRow + "\n";

        // Act
        string written = AnswersFile.Format(AnswersFile.Parse(text));

        // Assert
        Assert.Equal(text, written);
    }

    /// <summary>Each column lands in its field: the clip's place, both recipes and the answer.</summary>
    [Fact]
    public void Parse_WithAnsweredRow_ReadsEveryColumn()
    {
        // Arrange
        string text = AnswersFile.Header + "\r\n" + AnsweredRow + "\r\n";

        // Act
        ListeningRow sut = Assert.Single(AnswersFile.Parse(text));

        // Assert
        Assert.Equal("r20-q1", sut.Name);
        Assert.Equal(43.5, sut.StartSeconds);
        Assert.Equal(20, sut.Seconds);
        Assert.Equal("--mapping optimizer rule:half-speed=3", sut.RecipeB);
        Assert.Equal("b", sut.Pick);
        Assert.Equal(string.Empty, sut.GradeA);
        Assert.Equal("4", sut.GradeB);
        Assert.Equal("the bass is duller", sut.Words);
    }

    /// <summary>Tabs and line breaks in David's words become spaces, so a row stays one line of 16 cells.</summary>
    [Fact]
    public void Format_WithTabsInWords_WritesThemAsSpaces()
    {
        // Arrange
        ListeningRow row = AnswersFile.Parse(AnswersFile.Header + "\n" + AnsweredRow)[0] with { Words = "one\ttwo\nthree" };

        // Act
        string written = AnswersFile.Format([row]);

        // Assert
        Assert.EndsWith("\tone two three\n", written, StringComparison.Ordinal);
    }

    /// <summary>A file that doesn't start with the header isn't taken for an answers file.</summary>
    [Fact]
    public void Parse_WithoutHeader_Throws()
    {
        // Arrange
        string text = AnsweredRow + "\n";

        // Act
        HarnessException ex = Assert.Throws<HarnessException>(() => AnswersFile.Parse(text));

        // Assert
        Assert.Contains("line 1", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A pick other than a, b or same is refused, naming the line.</summary>
    [Fact]
    public void Parse_WithUnknownPick_ThrowsNamingTheLine()
    {
        // Arrange
        string text = AnswersFile.Header + "\n" + AnsweredRow.Replace("\tbb\tb\t", "\tbb\tx\t", StringComparison.Ordinal);

        // Act
        HarnessException ex = Assert.Throws<HarnessException>(() => AnswersFile.Parse(text));

        // Assert
        Assert.Contains("line 2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("pick", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>An answer without the clips' hashes can't be checked, so it is refused.</summary>
    [Fact]
    public void Parse_WithAnswerButNoHashes_Throws()
    {
        // Arrange
        string text = AnswersFile.Header + "\n" + AnsweredRow.Replace("\taa\tbb\t", "\t\t\t", StringComparison.Ordinal);

        // Act
        HarnessException ex = Assert.Throws<HarnessException>(() => AnswersFile.Parse(text));

        // Assert
        Assert.Contains("hashes", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A question not answered yet needs no hashes: a new round's rows before their clips are made.</summary>
    [Fact]
    public void Parse_WithPendingQuestion_AcceptsEmptyHashes()
    {
        // Arrange
        string text = AnswersFile.Header + "\n" + AnsweredRow.Replace("\taa\tbb\tb\t", "\t\t\t\t", StringComparison.Ordinal);

        // Act
        ListeningRow sut = Assert.Single(AnswersFile.Parse(text));

        // Assert
        Assert.Equal(string.Empty, sut.Pick);
        Assert.Equal(string.Empty, sut.Sha256A);
    }

    /// <summary>The same round and question twice is refused: each answer counts once.</summary>
    [Fact]
    public void Parse_WithDuplicateQuestion_Throws()
    {
        // Arrange
        string text = AnswersFile.Header + "\n" + AnsweredRow + "\n" + AnsweredRow + "\n";

        // Act
        HarnessException ex = Assert.Throws<HarnessException>(() => AnswersFile.Parse(text));

        // Assert
        Assert.Contains("twice", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A row with a column missing is refused, saying how many it has.</summary>
    [Fact]
    public void Parse_WithMissingColumn_Throws()
    {
        // Arrange
        string text = AnswersFile.Header + "\n" + AnsweredRow[..AnsweredRow.LastIndexOf('\t')] + "\n";

        // Act
        HarnessException ex = Assert.Throws<HarnessException>(() => AnswersFile.Parse(text));

        // Assert
        Assert.Contains("15 columns", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A recipe's rule is taken out of the converter's arguments and turned into shrink rules, 0-based.</summary>
    [Fact]
    public void Parse_WithHalfSpeedRule_SplitsRuleFromArguments()
    {
        // Arrange
        const string text = "--mapping optimizer rule:half-speed=3,12";

        // Act
        Recipe sut = Recipe.Parse(text);

        // Assert
        Assert.Equal(["--mapping", "optimizer"], sut.Arguments);
        Assert.Equal([2, 11], sut.Rules!.ForcedHalfSpeed.Order()); // a half-speed rule always gives rules
    }

    /// <summary>A recipe with no rule leaves the converter's own shrink rules.</summary>
    [Fact]
    public void Parse_WithoutRule_KeepsConverterRules()
    {
        // Arrange
        const string text = "--mapping optimizer";

        // Act
        Recipe sut = Recipe.Parse(text);

        // Assert
        Assert.Null(sut.Rules);
    }

    /// <summary>The layers and pre-mix rules switch those off and leave the shrink rules alone; a recipe without them has neither off.</summary>
    [Fact]
    public void Parse_WithLayersAndPreMixRules_TurnsThemOff()
    {
        // Arrange
        const string text = "--mapping optimizer rule:layers=off rule:pre-mix=off";

        // Act
        Recipe sut = Recipe.Parse(text);
        Recipe plain = Recipe.Parse("--mapping optimizer");

        // Assert
        Assert.Equal(["--mapping", "optimizer"], sut.Arguments);
        Assert.False(sut.Layers);
        Assert.False(sut.PreMix);
        Assert.Null(sut.Rules);
        Assert.True(sut.HasRules);
        Assert.True(plain.Layers);
        Assert.True(plain.PreMix);
        Assert.False(plain.HasRules);
    }

    /// <summary>The layers and pre-mix rules take only <c>off</c>: anything else is refused rather than read as the default.</summary>
    [Theory]
    [InlineData("rule:layers=on")]
    [InlineData("rule:pre-mix")]
    public void Parse_WithLayersOrPreMixNotOff_Throws(string text)
    {
        // Act
        HarnessException ex = Assert.Throws<HarnessException>(() => Recipe.Parse(text));

        // Assert
        Assert.Contains("rule:layers=off", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>An unknown rule is refused, so a typo can't quietly make the default version.</summary>
    [Fact]
    public void Parse_WithUnknownRule_Throws()
    {
        // Arrange
        const string text = "rule:quarter-speed=3";

        // Act
        HarnessException ex = Assert.Throws<HarnessException>(() => Recipe.Parse(text));

        // Assert
        Assert.Contains("unknown rule", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A sample number outside 1–31 is refused.</summary>
    [Fact]
    public void Parse_WithSampleOutOfRange_Throws()
    {
        // Arrange
        const string text = "rule:half-speed=0";

        // Act
        HarnessException ex = Assert.Throws<HarnessException>(() => Recipe.Parse(text));

        // Assert
        Assert.Contains("sample number", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The format and output paths come from the harness, so a recipe can't set them.</summary>
    [Theory]
    [InlineData("--format xgm")]
    [InlineData("--output x.dmf")]
    [InlineData("--render-wav=x.wav")]
    public void Parse_WithOptionTheHarnessSets_Throws(string text)
    {
        // Arrange

        // Act
        HarnessException ex = Assert.Throws<HarnessException>(() => Recipe.Parse(text));

        // Assert
        Assert.Contains("set by the harness", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The clip holds exactly the frames from its start, byte for byte, under a plain 44-byte header.</summary>
    [Fact]
    public void Cut_WithStartAndLength_CopiesThoseFrames()
    {
        // Arrange
        byte[] wav = Stereo16(rate: 10, frames: 30);

        // Act
        byte[] clip = ClipWav.Cut(wav, startSeconds: 1, seconds: 0.5);

        // Assert
        Assert.Equal(44 + (5 * 4), clip.Length);
        Assert.Equal(wav.AsSpan(44 + (10 * 4), 5 * 4).ToArray(), clip.AsSpan(44).ToArray());
        Assert.Equal(5 * 4, BinaryPrimitives.ReadInt32LittleEndian(clip.AsSpan(40)));
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(clip.AsSpan(24)));
    }

    /// <summary>A clip past the song's end stops with the song.</summary>
    [Fact]
    public void Cut_PastTheEnd_StopsWithTheSong()
    {
        // Arrange
        byte[] wav = Stereo16(rate: 10, frames: 30);

        // Act
        byte[] clip = ClipWav.Cut(wav, startSeconds: 2.5, seconds: 60);

        // Assert
        Assert.Equal(44 + (5 * 4), clip.Length);
    }

    /// <summary>The same render cut twice is the same file, with the same hash: what the remake check relies on.</summary>
    [Fact]
    public void Sha256_OfTwoCutsOfOneRender_IsTheSame()
    {
        // Arrange
        byte[] wav = Stereo16(rate: 10, frames: 30);

        // Act
        string first = ClipWav.Sha256(ClipWav.Cut(wav, 1, 1));
        string second = ClipWav.Sha256(ClipWav.Cut(wav, 1, 1));

        // Assert
        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    /// <summary>A mono WAV isn't cut: the clips are always 16-bit stereo, as <c>--render-wav</c> writes them.</summary>
    [Fact]
    public void Cut_WithMono_Throws()
    {
        // Arrange
        byte[] wav = Stereo16(rate: 10, frames: 30);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 1);

        // Act
        HarnessException ex = Assert.Throws<HarnessException>(() => ClipWav.Cut(wav, 0, 1));

        // Assert
        Assert.Contains("16-bit stereo", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A 16-bit stereo WAV whose sample bytes count up, so a cut's place can be checked.</summary>
    private static byte[] Stereo16(int rate, int frames)
    {
        byte[] wav = new byte[44 + (frames * 4)];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 2);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), rate * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), frames * 4);
        for (int i = 44; i < wav.Length; i++)
        {
            wav[i] = (byte)i;
        }

        return wav;
    }
}
