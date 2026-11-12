using System.Buffers.Binary;
using Mod2Xgm.Analysis;
using Mod2Xgm.Scoring;

namespace Mod2Xgm.Harness.Tests;

/// <summary>Tests for <see cref="PairPicker"/>, the listening round's pair picker (plan T9.4k3, docs/adr/0102-pair-picker.md).</summary>
public sealed class PairPickerTests
{
    private static readonly IReadOnlyList<double> NoWeights = [];

    /// <summary>When every measure finds the default closer, the vote is unanimous: fully sure.</summary>
    [Fact]
    public void Sureness_WhenEveryMeasureAgrees_IsOne()
    {
        // Arrange
        PairCandidate pair = Pair("a.mod", PairKind.NoDac, Features(1), Features(2));

        // Act
        double sut = PairPicker.Sureness(pair, NoWeights);

        // Assert
        Assert.Equal(1, sut);
    }

    /// <summary>Four measures for the default and three for the change leave the vote split: 1/7 sure.</summary>
    [Fact]
    public void Sureness_WhenMeasuresSplitFourToThree_IsOneSeventh()
    {
        // Arrange
        PairCandidate pair = Pair("a.mod", PairKind.NoDac, Features(1), new ListeningFeatures(2, 2, 2, 2, 0, 0, 0));

        // Act
        double sut = PairPicker.Sureness(pair, NoWeights);

        // Assert
        Assert.Equal(1 / 7.0, sut, 12);
    }

    /// <summary>When no measure tells the versions apart, no vote is cast, and the pair counts as sure (nothing to learn).</summary>
    [Fact]
    public void Sureness_WhenNoMeasureVotes_IsOne()
    {
        // Arrange
        PairCandidate pair = Pair("a.mod", PairKind.NoDac, Features(1), Features(1));

        // Act
        double sut = PairPicker.Sureness(pair, NoWeights);

        // Assert
        Assert.Equal(1, sut);
    }

    /// <summary>With weights, a pair whose scores are equal is a coin toss: 0 sure.</summary>
    [Fact]
    public void Sureness_WithWeightsAndEqualScores_IsZero()
    {
        // Arrange
        IReadOnlyList<double> weights = [1, 0, 0, 0, 0, 0, 0];
        PairCandidate pair = Pair("a.mod", PairKind.NoDac, new ListeningFeatures(1, 0, 0, 0, 0, 0, 0), new ListeningFeatures(1, 5, 5, 5, 5, 5, 5));

        // Act
        double sut = PairPicker.Sureness(pair, weights);

        // Assert
        Assert.Equal(0, sut, 12);
    }

    /// <summary>With weights, a large score difference is nearly certain.</summary>
    [Fact]
    public void Sureness_WithWeightsAndLargeDifference_IsNearOne()
    {
        // Arrange
        IReadOnlyList<double> weights = [1, 0, 0, 0, 0, 0, 0];
        PairCandidate pair = Pair("a.mod", PairKind.NoDac, new ListeningFeatures(0, 0, 0, 0, 0, 0, 0), new ListeningFeatures(20, 0, 0, 0, 0, 0, 0));

        // Act
        double sut = PairPicker.Sureness(pair, weights);

        // Assert
        Assert.True(sut > 0.999, $"sureness {sut}");
    }

    /// <summary>The picker takes the least sure pairs, at most one per song.</summary>
    [Fact]
    public void Pick_WithTwoPairsOfOneSong_TakesTheLessSureOnly()
    {
        // Arrange
        PairCandidate split = Pair("a.mod", PairKind.NoDac, Features(1), new ListeningFeatures(2, 2, 2, 2, 0, 0, 0));
        PairCandidate sure = Pair("a.mod", PairKind.Simple, Features(1), Features(2));
        PairCandidate other = Pair("b.mod", PairKind.Simple, Features(1), Features(2));

        // Act
        IReadOnlyList<PairCandidate> sut = PairPicker.Pick([sure, split, other], 3, new HashSet<string>(), NoWeights);

        // Assert
        Assert.Equal([split, other], sut);
    }

    /// <summary>No kind gets more than <see cref="PairPicker.MaxPerKind"/> pairs, however unsure they are.</summary>
    [Fact]
    public void Pick_WithManyUnsurePairsOfOneKind_CapsTheKind()
    {
        // Arrange
        ListeningFeatures splitB = new(2, 2, 2, 2, 0, 0, 0);
        PairCandidate[] noDac = [.. Enumerable.Range(0, 5).Select(i => Pair($"s{i}.mod", PairKind.NoDac, Features(1), splitB))];
        PairCandidate simple = Pair("t.mod", PairKind.Simple, Features(1), Features(2));

        // Act
        IReadOnlyList<PairCandidate> sut = PairPicker.Pick([.. noDac, simple], 10, new HashSet<string>(), NoWeights);

        // Assert
        Assert.Equal(PairPicker.MaxPerKind, sut.Count(p => p.Kind == PairKind.NoDac));
        Assert.Contains(simple, sut);
    }

    /// <summary>Songs already asked about are used only when the other songs run out.</summary>
    [Fact]
    public void Pick_WithAskedSong_TakesItLast()
    {
        // Arrange
        PairCandidate asked = Pair("old.mod", PairKind.NoDac, Features(1), new ListeningFeatures(2, 2, 2, 2, 0, 0, 0));
        PairCandidate fresh = Pair("new.mod", PairKind.Simple, Features(1), Features(2));
        HashSet<string> songs = [with(StringComparer.OrdinalIgnoreCase), "OLD.mod"];

        // Act
        IReadOnlyList<PairCandidate> one = PairPicker.Pick([asked, fresh], 1, songs, NoWeights);
        IReadOnlyList<PairCandidate> two = PairPicker.Pick([asked, fresh], 2, songs, NoWeights);

        // Assert
        Assert.Equal([fresh], one);
        Assert.Equal([fresh, asked], two);
    }

    /// <summary>The rows are a new round's questions, unanswered and unhashed, with the default on one side and the change on the other.</summary>
    [Fact]
    public void Rows_WithPicks_MakesUnansweredQuestions()
    {
        // Arrange
        PairCandidate pair = Pair("a.mod", PairKind.NoDac, Features(1), Features(2)) with { StartSeconds = 30 };

        // Act
        IReadOnlyList<ListeningRow> sut = PairPicker.Rows([pair], "r21", new DateOnly(2026, 11, 11));

        // Assert
        ListeningRow row = Assert.Single(sut);
        Assert.Equal(("2026-11-11", "r21", "q1", "a.mod", 30.0, 20.0, "xgm2"), (row.Date, row.Round, row.Question, row.Song, row.StartSeconds, row.Seconds, row.Format));
        Assert.Equal(new[] { PairPicker.DefaultRecipe, pair.Recipe }.Order(), new[] { row.RecipeA, row.RecipeB }.Order());
        Assert.Equal(string.Empty, row.Sha256A + row.Sha256B + row.Pick + row.Commit);
    }

    /// <summary>The A/B order comes from the song and start alone, so it is the same every run and not always the default first.</summary>
    [Fact]
    public void Rows_OverManyStarts_PutsTheChangeFirstSometimes()
    {
        // Arrange
        PairCandidate[] pairs = [.. Enumerable.Range(0, 20).Select(i => Pair($"s{i}.mod", PairKind.Simple, Features(1), Features(2)))];

        // Act
        IReadOnlyList<ListeningRow> sut = PairPicker.Rows(pairs, "r21", new DateOnly(2026, 11, 11));
        IReadOnlyList<ListeningRow> again = PairPicker.Rows(pairs, "r21", new DateOnly(2026, 11, 11));

        // Assert
        Assert.Contains(sut, r => r.RecipeA == PairPicker.DefaultRecipe);
        Assert.Contains(sut, r => r.RecipeB == PairPicker.DefaultRecipe);
        Assert.Equal(sut, again);
    }

    /// <summary>The next round follows the highest round number in the file.</summary>
    [Fact]
    public void NextRound_AfterR20_IsR21()
    {
        // Arrange
        ListeningRow row = PairPicker.Rows([Pair("a.mod", PairKind.Simple, Features(1), Features(2))], "r20", new DateOnly(2026, 11, 10))[0];

        // Act
        string sut = PairPicker.NextRound([row, row with { Round = "r3" }]);

        // Assert
        Assert.Equal("r21", sut);
    }

    /// <summary>An empty file starts at round 1.</summary>
    [Fact]
    public void NextRound_WithNoRows_IsR1()
    {
        // Act
        string sut = PairPicker.NextRound([]);

        // Assert
        Assert.Equal("r1", sut);
    }

    /// <summary>Starts are 10 s apart and every clip fits in the song.</summary>
    [Fact]
    public void Starts_ForA45SecondSong_AreZeroTenAndTwenty()
    {
        // Act
        IReadOnlyList<double> sut = PairPicker.Starts(45);

        // Assert
        Assert.Equal([0.0, 10.0, 20.0], sut);
    }

    /// <summary>The budget is half the stored PCM bytes from the XGM2 header's SLEN, in whole 256-byte blocks.</summary>
    [Fact]
    public void HalfBudget_WithThreeBlocks_IsOneBlock()
    {
        // Arrange
        byte[] header = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 3);

        // Act
        long sut = PairPicker.HalfBudget(header);

        // Assert
        Assert.Equal(256, sut);
    }

    /// <summary>A file too short for its header is refused.</summary>
    [Fact]
    public void HalfBudget_WithShortFile_Throws()
    {
        // Act & Assert
        Assert.Throws<HarnessException>(() => PairPicker.HalfBudget(new byte[4]));
    }

    /// <summary>The recipes change one thing from the default each.</summary>
    [Fact]
    public void RecipeOf_EachKind_ChangesOneThing()
    {
        // Act & Assert
        Assert.Equal("--mapping simple", PairPicker.RecipeOf(PairKind.Simple));
        Assert.Equal("--mapping optimizer --no-dac", PairPicker.RecipeOf(PairKind.NoDac));
        Assert.Equal("--mapping optimizer --max-pcm-bytes 4096", PairPicker.RecipeOf(PairKind.Budget, 4096));
        Assert.Equal("--mapping optimizer rule:half-speed=3", PairPicker.RecipeOf(PairKind.HalfSpeed, 3));
        Assert.Equal("--mapping optimizer rule:layers=off", PairPicker.RecipeOf(PairKind.NoLayers));
        Assert.Equal("--mapping optimizer rule:pre-mix=off", PairPicker.RecipeOf(PairKind.NoPreMix));
    }

    /// <summary>Every kind's name reads back as that kind, so <c>--kinds</c> takes what <c>candidates.tsv</c> shows; other names are none.</summary>
    [Fact]
    public void Parse_EveryKindsName_GivesTheKind()
    {
        // Act & Assert
        Assert.All(Enum.GetValues<PairKind>(), k => Assert.Equal(k, PairPicker.Parse(PairPicker.Name(k))));
        Assert.Equal("no-layers", PairPicker.Name(PairKind.NoLayers));
        Assert.Equal("no-pre-mix", PairPicker.Name(PairKind.NoPreMix));
        Assert.Null(PairPicker.Parse("layers"));
    }

    /// <summary>A raised cap (<c>--max-per-kind</c>) lets more pairs of one kind in.</summary>
    [Fact]
    public void Pick_WithRaisedCap_TakesMoreOfOneKind()
    {
        // Arrange
        ListeningFeatures splitB = new(2, 2, 2, 2, 0, 0, 0);
        PairCandidate[] noLayers = [.. Enumerable.Range(0, 6).Select(i => Pair($"s{i}.mod", PairKind.NoLayers, Features(1), splitB))];

        // Act
        IReadOnlyList<PairCandidate> sut = PairPicker.Pick(noLayers, 10, new HashSet<string>(), NoWeights, maxPerKind: 5);

        // Assert
        Assert.Equal(5, sut.Count);
    }

    /// <summary>A spectrum only in the low bands has nothing above the half-speed top; one only in the top band has everything.</summary>
    [Fact]
    public void ShareAbove_LowAndHighSpectra_AreZeroAndOne()
    {
        // Arrange
        float[] low = [.. Enumerable.Range(0, 64).Select(b => b < 5 ? -10f : -80f)];
        float[] high = [.. Enumerable.Range(0, 64).Select(b => b == 63 ? -10f : -80f)];

        // Act
        double lowShare = PairPicker.ShareAbove(new RegionSpectrum(0.01, [low], null));
        double highShare = PairPicker.ShareAbove(new RegionSpectrum(0.01, [high], null));

        // Assert
        Assert.Equal(0, lowShare);
        Assert.Equal(1, highShare);
    }

    /// <summary>Only played, sounding samples with little above the half-speed top are dull, the dullest first, at most two.</summary>
    [Fact]
    public void DullSamples_WithMixedSamples_ReturnsTheDullestPlayedOnes()
    {
        // Arrange
        float[] dull = [.. Enumerable.Range(0, 64).Select(b => b < 5 ? -10f : -80f)];
        float[] slightlyBright = [.. Enumerable.Range(0, 64).Select(b => b < 5 ? -10f : b == 50 ? -40f : -80f)];
        float[] bright = [.. Enumerable.Range(0, 64).Select(_ => -10f)];
        SampleAnalysis[] samples =
        [
            Analysis(0, SampleKind.Tone, bright, notes: 5),
            Analysis(1, SampleKind.Tone, slightlyBright, notes: 5),
            Analysis(2, SampleKind.Tone, dull, notes: 5),
            Analysis(3, SampleKind.Tone, dull, notes: 0),
            Analysis(4, SampleKind.Silent, dull, notes: 5),
            Analysis(5, SampleKind.Tone, dull, notes: 1),
        ];

        // Act
        IReadOnlyList<int> sut = PairPicker.DullSamples(samples);

        // Assert
        Assert.Equal([2, 5], sut);
    }

    /// <summary>The candidates table has a header and one line per candidate, least sure first.</summary>
    [Fact]
    public void CandidatesTable_WithTwoPairs_ListsLeastSureFirst()
    {
        // Arrange
        PairCandidate sure = Pair("a.mod", PairKind.Simple, Features(1), Features(2));
        PairCandidate split = Pair("b.mod", PairKind.NoDac, Features(1), new ListeningFeatures(2, 2, 2, 2, 0, 0, 0));

        // Act
        string[] sut = PairPicker.CandidatesTable([sure, split], NoWeights).TrimEnd('\n').Split('\n');

        // Assert
        Assert.Equal(3, sut.Length);
        Assert.StartsWith("song\tkind\trecipe\tstart_s\tsureness\tdefault_mel_db", sut[0], StringComparison.Ordinal);
        Assert.StartsWith("b.mod\tno-dac\t", sut[1], StringComparison.Ordinal);
        Assert.Equal(5 + (2 * ListeningFeatures.Names.Count), sut[1].Split('\t').Length);
    }

    private static ListeningFeatures Features(double value) => new(value, value, value, value, value, value, value);

    private static PairCandidate Pair(string song, PairKind kind, ListeningFeatures defaults, ListeningFeatures changed) =>
        new(song, kind, PairPicker.RecipeOf(kind, 1), 0, defaults, changed);

    private static SampleAnalysis Analysis(int sample, SampleKind kind, float[] bands, int notes) =>
        new(
            sample,
            "s" + sample,
            kind,
            0,
            0,
            0,
            null,
            new AmplitudeEnvelope(8287, 0.01, [], 0, 0, null, null),
            null,
            [new PitchRegion(8287, 8287, 8287, notes, null, new RegionSpectrum(0.01, [bands], null))]);
}
