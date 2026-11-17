using System.Buffers.Binary;
using Mod2Xgm.Optimizer;

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

    /// <summary>A <c>neither</c> pick ("both unacceptable", ADR 0111) is read and written back as it is.</summary>
    [Fact]
    public void Parse_WithNeitherPick_ReadsAndWritesIt()
    {
        // Arrange
        string text = AnswersFile.Header + "\n" + AnsweredRow.Replace("\tbb\tb\t", "\tbb\tneither\t", StringComparison.Ordinal) + "\n";

        // Act
        IReadOnlyList<ListeningRow> sut = AnswersFile.Parse(text);

        // Assert
        Assert.Equal("neither", Assert.Single(sut).Pick);
        Assert.Equal(text, AnswersFile.Format(sut));
    }

    /// <summary>Only a, b and same rank the two versions; neither and an unanswered question don't.</summary>
    [Theory]
    [InlineData("a", true)]
    [InlineData("b", true)]
    [InlineData("same", true)]
    [InlineData("neither", false)]
    [InlineData("", false)]
    public void Ranks_ForEachPick_IsTrueOnlyForAbOrSame(string pick, bool expected)
    {
        // Arrange
        ListeningRow sut = new("2026-11-14", "r25", "q7", "song.mod", 0, 20, "xgm2", "", "", "c", "x", "y", pick, "", "", "");

        // Act
        bool ranks = sut.Ranks;

        // Assert
        Assert.Equal(expected, ranks);
    }

    /// <summary>A pick other than a, b, same or neither is refused, naming the line.</summary>
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

    /// <summary><c>rule:chords=on</c> plays chords as FM voices of their own (docs/adr/0114-fm-chords.md); without it they stay off.</summary>
    [Fact]
    public void Parse_WithChordsRule_TurnsChordsOn()
    {
        // Act
        Recipe sut = Recipe.Parse("--mapping optimizer rule:chords=on");
        Recipe plain = Recipe.Parse("--mapping optimizer");

        // Assert
        Assert.Equal(["--mapping", "optimizer"], sut.Arguments);
        Assert.True(sut.Chords);
        Assert.True(sut.HasRules);
        Assert.False(plain.Chords);
    }

    /// <summary><c>rule:chords</c> takes only <c>on</c>: anything else is refused, naming the rule.</summary>
    [Theory]
    [InlineData("rule:chords=off")]
    [InlineData("rule:chords")]
    public void Parse_WithChordsNotOn_Throws(string text)
    {
        // Act
        HarnessException ex = Assert.Throws<HarnessException>(() => Recipe.Parse(text));

        // Assert
        Assert.Contains("rule:chords=on", ex.Message, StringComparison.Ordinal);
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

    /// <summary><c>rule:pan=short</c> alone lets stand a swap of 0.1 s or less, at half volume or less, to or from the middle (docs/adr/0109-pan-swap-cost.md).</summary>
    [Fact]
    public void Parse_WithPanShort_SetsTheShortQuietStep()
    {
        // Act
        Recipe sut = Recipe.Parse("--mapping optimizer rule:pan=short");

        // Assert
        Assert.Equal(PanRules.Today with { Allow = PanSwapAllowance.ShortQuiet, FreeSeconds = 0.1, FreeVolume = 0.5, FreeDistance = 1 }, sut.Pan);
        Assert.True(sut.HasRules);
    }

    /// <summary>The pan rules combine: limits, span, fixes and price each set their own part.</summary>
    [Fact]
    public void Parse_WithEveryPanRule_SetsEachPart()
    {
        // Act
        Recipe sut = Recipe.Parse("--mapping optimizer rule:pan-free=0.25,0.4,2 rule:pan-span=order rule:pan-fix=middle+unmix rule:pan-cost=6");

        // Assert
        Assert.Equal(new PanRules(PanSwapAllowance.ShortQuiet, 0.25, 0.4, 2, SteadyMiddleSpan.Order, PanFixes.SteadyMiddle | PanFixes.SkipPreMix, 6), sut.Pan);
    }

    /// <summary><c>rule:pan=never</c> sets the top of the ladder; a recipe without pan rules leaves the converter's own.</summary>
    [Fact]
    public void Parse_WithPanNever_SetsTheTopOfTheLadder()
    {
        // Act
        Recipe sut = Recipe.Parse("--mapping optimizer rule:pan=never");

        // Assert
        Assert.Equal(PanRules.Never, sut.Pan);
        Assert.Null(Recipe.Parse("--mapping optimizer").Pan);
    }

    /// <summary>A pan rule with a value it can't read is refused, naming what it needs.</summary>
    [Theory]
    [InlineData("rule:pan-free=0.1,2,1")]
    [InlineData("rule:pan-free=0.1,0.5")]
    [InlineData("rule:pan-fix=middle+left")]
    [InlineData("rule:pan=sometimes")]
    [InlineData("rule:pan-cost=-1")]
    public void Parse_WithBadPanRule_Throws(string text)
    {
        // Act
        HarnessException ex = Assert.Throws<HarnessException>(() => Recipe.Parse(text));

        // Assert
        Assert.Contains("rule:pan", ex.Message, StringComparison.Ordinal);
    }

    /// <summary><c>rule:quieter</c> turns samples (1-based) down and <c>rule:song-quieter</c> the whole song, for a retry (docs/adr/0112-retry-list.md).</summary>
    [Fact]
    public void Parse_WithLevelRules_SetsTheLevels()
    {
        // Act
        Recipe sut = Recipe.Parse("--mapping optimizer rule:quieter=3:6,7:3 rule:song-quieter=1.5");

        // Assert
        Assert.Equal(1.5, sut.Levels!.SongDb);
        Assert.Equal([(2, 6.0), (6, 3.0)], sut.Levels.RecordingDb.OrderBy(p => p.Key).Select(p => (p.Key, p.Value)));
        Assert.Equal(-6, sut.Levels.RecordingGainDb(2));
        Assert.True(sut.HasRules);
        Assert.Null(Recipe.Parse("--mapping optimizer").Levels);
    }

    /// <summary>The squeeze rules: no shrinking, every sample at half speed, and who keeps a recording first (docs/adr/0113-squeeze-rounds.md).</summary>
    [Fact]
    public void Parse_WithSqueezeRules_SetsShrinkHalfSpeedAndKeep()
    {
        // Act
        Recipe sut = Recipe.Parse("--mapping optimizer rule:shrink=off rule:half-speed=all rule:keep=drums");

        // Assert
        Assert.False(sut.Shrink);
        Assert.Equal(31, sut.HalfSpeed.Count);
        Assert.Equal(KeepFirst.Drums, sut.Keep);
        Assert.Empty(sut.Rules!.Order);
        Assert.Equal(Enumerable.Range(0, 31), sut.Rules.ForcedHalfSpeed.Order());
        Assert.True(sut.HasRules);
    }

    /// <summary>Without squeeze rules a recipe shrinks as the converter does and keeps by importance, so it has no rules.</summary>
    [Fact]
    public void Parse_WithoutSqueezeRules_KeepsTheConvertersOwn()
    {
        // Act
        Recipe sut = Recipe.Parse("--mapping optimizer rule:keep=importance");

        // Assert
        Assert.True(sut.Shrink);
        Assert.Equal(KeepFirst.Importance, sut.Keep);
        Assert.Null(sut.Rules);
        Assert.False(sut.HasRules);
    }

    /// <summary>The drum-bake rules: bytes by score and which bakes are used (docs/adr/0115-drum-bakes.md).</summary>
    [Theory]
    [InlineData("rule:bytes=score", true, false, false)]
    [InlineData("rule:bake=together", false, true, false)]
    [InlineData("rule:bake=beat", false, false, true)]
    [InlineData("rule:bytes=score rule:bake=both", true, true, true)]
    public void Parse_WithBakeRules_SetsThem(string rules, bool byScore, bool together, bool beat)
    {
        // Act
        Recipe sut = Recipe.Parse("--mapping optimizer " + rules);

        // Assert
        Assert.Equal(new BakeRules(byScore, together, beat), sut.Bake);
        Assert.True(sut.HasRules);
    }

    /// <summary>The converter's own byte order and no bakes are no rules at all.</summary>
    [Fact]
    public void Parse_WithBakeRulesAtTheirDefaults_HasNoRules()
    {
        // Act
        Recipe sut = Recipe.Parse("--mapping optimizer rule:bytes=importance rule:bake=off");

        // Assert
        Assert.Equal(BakeRules.None, sut.Bake);
        Assert.False(sut.HasRules);
    }

    /// <summary>The dial rules set how far pitches and loudness share a copy and how alike merged samples must be (plan T9.4t5).</summary>
    [Fact]
    public void Parse_WithDialRules_SetsTheShrinkDials()
    {
        // Act
        Recipe sut = Recipe.Parse("--mapping optimizer rule:pitch-share=25 rule:loudness-share=4 rule:merge=90");

        // Assert
        Assert.Equal((25.0, 4, 0.9), (sut.PitchShareCents.GetValueOrDefault(), sut.LoudnessShareSteps.GetValueOrDefault(), sut.MergeLikeness.GetValueOrDefault()));
        Assert.Equal(ShrinkRules.Default.Order, sut.Rules!.Order);
        Assert.Equal((25.0, 4, 0.9), (sut.Rules.PitchShareCents, sut.Rules.LoudnessShareSteps, sut.Rules.MergeCorrelation));
        Assert.True(sut.HasRules);
    }

    /// <summary>One dial changes only itself; the others keep the converter's own values.</summary>
    [Fact]
    public void Parse_WithOneDial_KeepsTheOthersAtDefault()
    {
        // Act
        Recipe sut = Recipe.Parse("--mapping optimizer rule:pitch-share=50");

        // Assert
        Assert.Equal(
            (50.0, ShrinkRules.Default.LoudnessShareSteps, ShrinkRules.Default.MergeCorrelation),
            (sut.Rules!.PitchShareCents, sut.Rules.LoudnessShareSteps, sut.Rules.MergeCorrelation));
    }

    /// <summary>A dial outside its range, not a number, or not whole where it must be is refused, naming the rule.</summary>
    [Theory]
    [InlineData("rule:pitch-share=0")]
    [InlineData("rule:pitch-share=101")]
    [InlineData("rule:pitch-share=wide")]
    [InlineData("rule:loudness-share=1")]
    [InlineData("rule:loudness-share=9")]
    [InlineData("rule:loudness-share=2.5")]
    [InlineData("rule:merge=49")]
    [InlineData("rule:merge=101")]
    [InlineData("rule:merge=90.5")]
    public void Parse_WithBadDial_Throws(string rule)
    {
        // Act
        HarnessException ex = Assert.Throws<HarnessException>(() => Recipe.Parse("--mapping optimizer " + rule));

        // Assert
        Assert.Contains(rule, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A keep or shrink rule with a value it doesn't know is refused, naming the rule.</summary>
    [Theory]
    [InlineData("rule:keep=bass")]
    [InlineData("rule:shrink=on")]
    [InlineData("rule:bytes=size")]
    [InlineData("rule:bake=all")]
    public void Parse_WithBadSqueezeRule_Throws(string rule)
    {
        // Act
        HarnessException ex = Assert.Throws<HarnessException>(() => Recipe.Parse("--mapping optimizer " + rule));

        // Assert
        Assert.Contains(rule, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A level rule with a sample or dB it can't read is refused, naming the rule.</summary>
    [Theory]
    [InlineData("rule:quieter=0:3")]
    [InlineData("rule:quieter=3")]
    [InlineData("rule:quieter=3:0")]
    [InlineData("rule:quieter=x:3")]
    [InlineData("rule:song-quieter=30")]
    [InlineData("rule:song-quieter=loud")]
    public void Parse_WithBadLevelRule_Throws(string text)
    {
        // Act
        HarnessException ex = Assert.Throws<HarnessException>(() => Recipe.Parse(text));

        // Assert
        Assert.Contains("quieter", ex.Message, StringComparison.Ordinal);
    }

    /// <summary><c>rule:dac-loser</c> sets the simple mapper's choice and isn't an optimizer rule.</summary>
    [Theory]
    [InlineData("noise", "Noise")]
    [InlineData("fm", "Fm")]
    [InlineData("noise-all-song", "NoiseAllSong")]
    public void Parse_WithDacLoser_SetsTheSimpleMappersChoice(string value, string expected)
    {
        // Act
        Recipe sut = Recipe.Parse("--mapping simple rule:dac-loser=" + value);

        // Assert
        Assert.Equal((Enum.Parse<Mapping.DacLoser>(expected), false), (sut.Loser!.Value, sut.HasRules));
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
