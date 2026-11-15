using System.Buffers.Binary;

namespace Mod2Xgm.Harness.Tests;

/// <summary>Tests for <see cref="Squeeze"/>, the squeeze rounds' budgets, recipes and pairs (plan T9.4t1, docs/adr/0113-squeeze-rounds.md).</summary>
public sealed class SqueezeTests
{
    /// <summary>A budget is the share of what today's version stores, rounded down to whole 256-byte blocks.</summary>
    [Theory]
    [InlineData(102_400, 50, 51_200)]
    [InlineData(102_400, 25, 25_600)]
    [InlineData(1_000, 25, 0)]
    [InlineData(10_000, 33, 3_072)]
    public void Budget_WithShare_RoundsDownToBlocks(long stored, int percent, long expected)
    {
        // Act
        long sut = Squeeze.Budget(stored, percent);

        // Assert
        Assert.Equal(expected, sut);
    }

    /// <summary>A share of 0 % or 100 % is refused: no recordings is <c>--no-dac</c>, and all of them is today's version.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Budget_WithShareOutOfRange_Throws(int percent)
    {
        // Act and Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => Squeeze.Budget(1024, percent));
    }

    /// <summary>Each way's recipe is the optimizer at the budget, with the rule that makes it that way.</summary>
    [Theory]
    [InlineData("Today", "--mapping optimizer --max-pcm-bytes 4096")]
    [InlineData("NoShrink", "--mapping optimizer --max-pcm-bytes 4096 rule:shrink=off")]
    [InlineData("HalfSpeedAll", "--mapping optimizer --max-pcm-bytes 4096 rule:half-speed=all")]
    [InlineData("DrumsFirst", "--mapping optimizer --max-pcm-bytes 4096 rule:keep=drums")]
    [InlineData("TonalFirst", "--mapping optimizer --max-pcm-bytes 4096 rule:keep=tonal")]
    [InlineData("PitchShare25", "--mapping optimizer --max-pcm-bytes 4096 rule:pitch-share=25")]
    [InlineData("PitchShare50", "--mapping optimizer --max-pcm-bytes 4096 rule:pitch-share=50")]
    [InlineData("LoudnessShare12", "--mapping optimizer --max-pcm-bytes 4096 rule:loudness-share=4")]
    [InlineData("Merge90", "--mapping optimizer --max-pcm-bytes 4096 rule:merge=90")]
    public void RecipeOf_EveryWay_IsTheBudgetAndItsRule(string way, string expected)
    {
        // Act
        string sut = Squeeze.RecipeOf(Enum.Parse<SqueezeWay>(way), 4096);

        // Assert
        Assert.Equal(expected, sut);
        Assert.NotNull(Recipe.Parse(sut));
    }

    /// <summary>Half speed for dull samples names them 1-based and in order, so the recipe stores just those at half speed.</summary>
    [Fact]
    public void RecipeOf_HalfSpeedDull_ListsTheDullSamples()
    {
        // Act
        string sut = Squeeze.RecipeOf(SqueezeWay.HalfSpeedDull, 4096, [6, 2]);

        // Assert
        Assert.Equal("--mapping optimizer --max-pcm-bytes 4096 rule:half-speed=3,7", sut);
        Assert.Equal([2, 6], Recipe.Parse(sut).Rules!.ForcedHalfSpeed.Order());
    }

    /// <summary>A <c>--ways</c> list gives those ways in the usual order, with today's always first.</summary>
    [Fact]
    public void ParseWays_WithList_AddsTodayAndKeepsTheOrder()
    {
        // Act
        IReadOnlyList<SqueezeWay> sut = Squeeze.ParseWays("merge-90, pitch-25,half-speed-dull");

        // Assert
        Assert.Equal([SqueezeWay.Today, SqueezeWay.PitchShare25, SqueezeWay.Merge90, SqueezeWay.HalfSpeedDull], sut);
    }

    /// <summary>A way <c>--ways</c> doesn't know is a usage error that names it.</summary>
    [Fact]
    public void ParseWays_WithUnknownWay_Throws()
    {
        // Act
        HarnessException ex = Assert.Throws<HarnessException>(() => Squeeze.ParseWays("pitch-25,louder"));

        // Assert
        Assert.Contains("louder", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Every way's <c>--ways</c> key reads back as that way.</summary>
    [Fact]
    public void Key_EveryWay_ParsesBack()
    {
        // Act
        IReadOnlyList<SqueezeWay> sut = Squeeze.ParseWays(string.Join(',', Squeeze.Ways.Select(Squeeze.Key)));

        // Assert
        Assert.Equal(Squeeze.Ways, sut);
    }

    /// <summary>Of one song's pairs, those whose ways sound the same are left out and the most different few are chosen.</summary>
    [Fact]
    public void Chosen_WithFourPairs_KeepsTheThreeMostDifferentThatDiffer()
    {
        // Arrange
        SqueezePair[] pairs =
        [
            Pair(SqueezeWay.NoShrink, 1.5),
            Pair(SqueezeWay.HalfSpeedAll, 0),
            Pair(SqueezeWay.DrumsFirst, 3),
            Pair(SqueezeWay.TonalFirst, 0.5),
        ];

        // Act
        IReadOnlyList<SqueezePair> sut = Squeeze.Chosen(pairs);

        // Assert
        Assert.Equal([SqueezeWay.DrumsFirst, SqueezeWay.NoShrink, SqueezeWay.TonalFirst], sut.Select(p => p.Second));
    }

    /// <summary>Each pair becomes one row of the next round: both recipes at the pair's budget, its start and 20 seconds, unanswered.</summary>
    [Fact]
    public void Rows_WithOnePair_WritesBothWaysAtItsBudget()
    {
        // Arrange
        SqueezePair pair = Pair(SqueezeWay.NoShrink, 2);

        // Act
        ListeningRow sut = Assert.Single(Squeeze.Rows([pair], "r27", new DateOnly(2026, 11, 15)));

        // Assert
        Assert.Equal(("2026-11-15", "r27", "q1", "hit.mod", 40.0, 20.0, "xgm2"), (sut.Date, sut.Round, sut.Question, sut.Song, sut.StartSeconds, sut.Seconds, sut.Format));
        Assert.Equal(
            new[] { Squeeze.RecipeOf(SqueezeWay.Today, 8192), Squeeze.RecipeOf(SqueezeWay.NoShrink, 8192) }.Order(),
            new[] { sut.RecipeA, sut.RecipeB }.Order());
        Assert.Equal(string.Empty, sut.Pick);
    }

    /// <summary><c>squeeze.tsv</c> lists every pair tried, with the way names and whether it went to the round.</summary>
    [Fact]
    public void Table_WithTriedAndChosen_MarksTheChosen()
    {
        // Arrange
        SqueezePair chosen = Pair(SqueezeWay.DrumsFirst, 3);
        SqueezePair left = Pair(SqueezeWay.HalfSpeedAll, 0);

        // Act
        string[] sut = Squeeze.Table([left, chosen], new HashSet<SqueezePair> { chosen }).TrimEnd('\n').Split('\n');

        // Assert
        Assert.Equal("song\tbudget\tfirst\tsecond\tstart_s\tdifference_db\tto_round", sut[0]);
        Assert.Equal("hit.mod\t8192\ttoday\tdrums keep theirs first\t40\t3\tyes", sut[1]);
        Assert.Equal("hit.mod\t8192\ttoday\thalf speed for all\t40\t0\tno", sut[2]);
    }

    /// <summary>Only the six songs that store the most are squeezed, biggest first; a song that stores nothing never is.</summary>
    [Fact]
    public void Biggest_WithEightSongs_TakesTheSixThatStoreMost()
    {
        // Arrange
        Dictionary<string, long> stored = new()
        {
            ["a.mod"] = 100, ["b.mod"] = 800, ["c.mod"] = 0, ["d.mod"] = 300,
            ["e.mod"] = 500, ["f.mod"] = 200, ["g.mod"] = 700, ["h.mod"] = 400,
        };

        // Act
        IReadOnlyList<string> sut = Squeeze.Biggest(stored);

        // Assert
        Assert.Equal(["b.mod", "g.mod", "e.mod", "h.mod", "d.mod", "f.mod"], sut);
    }

    /// <summary>Every way has its own name in the tables.</summary>
    [Fact]
    public void Name_EveryWay_IsDistinct()
    {
        // Act
        string[] sut = [.. Squeeze.Ways.Select(Squeeze.Name)];

        // Assert
        Assert.Equal(sut.Length, sut.Distinct().Count());
    }

    /// <summary>The PCM bytes an XGM2 file stores are its SLEN blocks of 256 bytes; half of them make the old half budget.</summary>
    [Fact]
    public void StoredBytes_WithSlen_IsBlocksOf256()
    {
        // Arrange
        byte[] header = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 3);

        // Act
        long sut = PairPicker.StoredBytes(header);

        // Assert
        Assert.Equal(768, sut);
        Assert.Equal(256, PairPicker.HalfBudget(header));
    }

    private static SqueezePair Pair(SqueezeWay second, double db) => new("hit.mod", SqueezeWay.Today, second, 8192, 40, db);
}
