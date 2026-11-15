using Mod2Xgm.Analysis;
using Mod2Xgm.Mod;
using Mod2Xgm.Model;
using Serilog.Core;

namespace Mod2Xgm.Harness.Tests;

/// <summary>Tests for <see cref="RetryList"/>, the places a <c>neither</c> sent back and the fixes tried there (plan T9.4s2, docs/adr/0112-retry-list.md).</summary>
public sealed class RetryListTests
{
    /// <summary>Rows of one place group together, whatever the case of the song's name, and its <c>neither</c> rows are kept.</summary>
    [Fact]
    public void Places_WithTwoNeitherRowsAtOnePlace_GroupsThem()
    {
        // Arrange
        ListeningRow[] rows = [Row("r23", "q6", "lazycows.mod", 100, "neither"), Row("r24", "q1", "other.mod", 60, "a"), Row("r25", "q9", "LAZYCOWS.MOD", 100, "neither")];

        // Act
        IReadOnlyList<RetryPlace> sut = RetryList.Places(rows);

        // Assert
        RetryPlace place = Assert.Single(sut);
        Assert.Equal(("lazycows.mod", 100.0, 2), (place.Song, place.StartSeconds, place.Rejected.Count));
        Assert.Equal("lazycows@100", place.Name);
    }

    /// <summary>A later answer at a place other than <c>neither</c> means a retry passed: the place leaves the list.</summary>
    [Fact]
    public void Places_WhenALaterAnswerPassed_LeavesThePlaceOut()
    {
        // Arrange
        ListeningRow[] rows = [Row("r23", "q5", "burp.mod", 200, "neither"), Row("r26", "q1", "burp.mod", 200, "b")];

        // Act
        IReadOnlyList<RetryPlace> sut = RetryList.Places(rows);

        // Assert
        Assert.Empty(sut);
    }

    /// <summary>A question not answered yet doesn't decide anything: the place stays on the list.</summary>
    [Fact]
    public void Places_WithAnUnansweredLaterRow_KeepsThePlace()
    {
        // Arrange
        ListeningRow[] rows = [Row("r23", "q5", "burp.mod", 200, "neither"), Row("r26", "q1", "burp.mod", 200, string.Empty)];

        // Act
        IReadOnlyList<RetryPlace> sut = RetryList.Places(rows);

        // Assert
        Assert.Single(sut);
    }

    /// <summary>The rejected clips are both versions' hashes of every <c>neither</c> row there.</summary>
    [Fact]
    public void RejectedClips_HasBothVersionsOfEveryRow()
    {
        // Arrange
        RetryPlace sut = new("a.mod", 0, 20, "xgm2", [Row("r1", "q1", "a.mod", 0, "neither") with { Sha256A = "aa", Sha256B = "bb" }, Row("r2", "q1", "a.mod", 0, "neither") with { Sha256A = "cc" }]);

        // Act
        IReadOnlySet<string> clips = sut.RejectedClips;

        // Assert
        Assert.Equal(["aa", "bb", "cc"], clips.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Every kind of fix David chose is tried from today's default: the three loudest sounds kept as recordings, pre-mixes and
    /// layers off, the loudest sound and the whole song 3 and 6 dB quieter, with 1-based sample numbers.
    /// </summary>
    [Fact]
    public void Tries_WithFourSounds_TriesEveryKind()
    {
        // Arrange
        (int, double)[] sounding = [(4, 9), (0, 5), (2, 3), (7, 1)];

        // Act
        IReadOnlyList<RetryTry> sut = RetryList.Tries(sounding);

        // Assert
        Assert.Equal(
            [
                "--mapping optimizer --require-dac 5",
                "--mapping optimizer --require-dac 1",
                "--mapping optimizer --require-dac 3",
                "--mapping optimizer rule:pre-mix=off",
                "--mapping optimizer rule:layers=off",
                "--mapping optimizer rule:pre-mix=off rule:layers=off",
                "--mapping optimizer rule:quieter=5:3",
                "--mapping optimizer rule:quieter=5:6",
                "--mapping optimizer rule:song-quieter=3",
                "--mapping optimizer rule:song-quieter=6",
            ],
            sut.Select(t => t.Recipe));
    }

    /// <summary>Every try's recipe is one the harness reads.</summary>
    [Fact]
    public void Tries_EveryRecipe_Parses()
    {
        // Arrange
        IReadOnlyList<RetryTry> sut = RetryList.Tries([(4, 9), (0, 5)]);

        // Act
        Recipe[] recipes = [.. sut.Select(t => Recipe.Parse(t.Recipe))];

        // Assert
        Assert.All(recipes, r => Assert.Equal("--mapping", r.Arguments[0]));
    }

    /// <summary>With nothing sounding there, no sound can be kept or turned down alone: only the rule toggles and the whole song quieter.</summary>
    [Fact]
    public void Tries_WithNothingSounding_LeavesOutThePerSoundKinds()
    {
        // Act
        IReadOnlyList<RetryTry> sut = RetryList.Tries([]);

        // Assert
        Assert.DoesNotContain(sut, t => t.Kind is RetryKind.KeepRecording or RetryKind.RecordingQuieter);
        Assert.Equal(5, sut.Count);
    }

    /// <summary>A kept recording that found no room tries again without the sound that plays least there, never the kept one.</summary>
    [Fact]
    public void WithRoom_WithKeptRecording_LeavesOutTheQuietestOther()
    {
        // Arrange
        RetryTry keep = new(RetryKind.KeepRecording, "--mapping optimizer --require-dac 8", "sample 8 kept a recording");
        (int, double)[] sounding = [(4, 9), (0, 5), (7, 1)];

        // Act
        RetryTry? sut = RetryList.WithRoom(keep, sounding);

        // Assert
        Assert.Equal("--mapping optimizer --require-dac 8 --ignore-samples 1", sut?.Recipe);
    }

    /// <summary>Only a kept recording, not already given room, gets a second try; nor one with no other sound there.</summary>
    [Fact]
    public void WithRoom_WithOtherTries_GivesNone()
    {
        // Arrange
        RetryTry quieter = new(RetryKind.SongQuieter, "--mapping optimizer rule:song-quieter=3", "x");
        RetryTry roomy = new(RetryKind.KeepRecording, "--mapping optimizer --require-dac 2 --ignore-samples 1", "x");
        RetryTry alone = new(RetryKind.KeepRecording, "--mapping optimizer --require-dac 2", "x");

        // Act
        RetryTry?[] sut = [RetryList.WithRoom(quieter, [(1, 1)]), RetryList.WithRoom(roomy, [(1, 1), (0, 1)]), RetryList.WithRoom(alone, [(1, 1)])];

        // Assert
        Assert.All(sut, Assert.Null);
    }

    /// <summary>Tries as far from the MOD as the rejected clips are screened out, a try that changes nothing is dropped, and the closest left are chosen.</summary>
    [Fact]
    public void Choose_WithSomeTriesPastTheRejected_ChoosesTheClosestThatPass()
    {
        // Arrange
        RetryOutcome far = Outcome("far", 0.5);
        RetryOutcome close = Outcome("close", 0.1);
        RetryOutcome same = Outcome("same", 0.05) with { SameAsDefault = true };
        RetryOutcome middle = Outcome("middle", 0.2);

        // Act
        (IReadOnlyList<RetryOutcome> chosen, bool doubted) = RetryList.Choose([far, close, same, middle], 0.3, 2);

        // Assert
        Assert.Equal([close, middle], chosen);
        Assert.False(doubted);
    }

    /// <summary>When every try is as far as the rejected clips, the closest are chosen anyway and marked doubted (David, 2026-11-14).</summary>
    [Fact]
    public void Choose_WhenNoTryPasses_ChoosesTheClosestDoubted()
    {
        // Arrange
        RetryOutcome a = Outcome("a", 0.5);
        RetryOutcome b = Outcome("b", 0.4);

        // Act
        (IReadOnlyList<RetryOutcome> chosen, bool doubted) = RetryList.Choose([a, b], 0.3, 1);

        // Assert
        Assert.Equal([b], chosen);
        Assert.True(doubted);
    }

    /// <summary>With no try that changes anything, nothing is chosen and nothing is doubted.</summary>
    [Fact]
    public void Choose_WithOnlyUnchangedTries_ChoosesNothing()
    {
        // Act
        (IReadOnlyList<RetryOutcome> chosen, bool doubted) = RetryList.Choose([Outcome("a", 0.1) with { SameAsDefault = true }], 0.3, 2);

        // Assert
        Assert.Equal((0, false), (chosen.Count, doubted));
    }

    /// <summary>Two tries go to the round where today's default is still a rejected clip, one against the default where it changed.</summary>
    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public void Wanted_ByDefaultState_IsTwoOrOne(bool defaultRejected, int expected)
    {
        // Act
        int sut = RetryList.Wanted(defaultRejected);

        // Assert
        Assert.Equal(expected, sut);
    }

    /// <summary>
    /// A place still as bad puts its two tries against each other; a changed one puts today's default against its try; a place
    /// with nothing chosen asks nothing. Questions are numbered from 1 and unanswered.
    /// </summary>
    [Fact]
    public void Rows_WithEachKindOfPlace_AsksTheRightPairs()
    {
        // Arrange
        RetryPlace bad = new("bad.mod", 10, 20, "xgm2", []);
        RetryPlace changed = new("changed.mod", 30, 20, "xgm2", []);
        RetryPlace empty = new("empty.mod", 0, 20, "xgm2", []);
        RetryChoice[] choices =
        [
            new(bad, true, 0.3, 0.3, [], [Outcome("one", 0.1), Outcome("two", 0.2)], false),
            new(empty, true, 0.3, 0.3, [], [], false),
            new(changed, false, 0.3, 0.2, [], [Outcome("three", 0.1)], false),
        ];

        // Act
        IReadOnlyList<ListeningRow> sut = RetryList.Rows(choices, "r26", new DateOnly(2026, 11, 15));

        // Assert
        Assert.Equal(2, sut.Count);
        Assert.Equal(["one", "two"], new[] { sut[0].RecipeA, sut[0].RecipeB }.Order(StringComparer.Ordinal));
        Assert.Equal(["--mapping optimizer", "three"], new[] { sut[1].RecipeA, sut[1].RecipeB }.Order(StringComparer.Ordinal));
        Assert.Equal(("r26", "q1", "q2", "2026-11-15", string.Empty), (sut[0].Round, sut[0].Question, sut[1].Question, sut[0].Date, sut[0].Pick));
    }

    /// <summary>The table has a line for today's default and one per try, closest first, marking what passed and what went to the round.</summary>
    [Fact]
    public void Table_WithOnePlace_ListsTheDefaultAndEveryTry()
    {
        // Arrange
        RetryOutcome passed = Outcome("passed", 0.1);
        RetryOutcome screened = Outcome("screened", 0.4);
        RetryChoice choice = new(new RetryPlace("a.mod", 0, 20, "xgm2", []), true, 0.3, 0.35, [screened, passed], [passed], false);

        // Act
        string[] sut = RetryList.Table([choice]).TrimEnd('\n').Split('\n');

        // Assert
        Assert.Equal(4, sut.Length);
        Assert.StartsWith("place\tsong", sut[0], StringComparison.Ordinal);
        Assert.Contains("\tdefault\ttoday's default\t--mapping optimizer\t0.35\t0.3\t", sut[1], StringComparison.Ordinal);
        Assert.EndsWith("passed\t0.1\t0.3\tyes\tyes\tyes", sut[2], StringComparison.Ordinal);
        Assert.EndsWith("screened\t0.4\t0.3\tyes\tno\tno", sut[3], StringComparison.Ordinal);
    }

    /// <summary>Each kind has a name in the table.</summary>
    [Fact]
    public void Name_EveryKind_IsDistinct()
    {
        // Act
        string[] sut = [.. Enum.GetValues<RetryKind>().Select(RetryList.Name)];

        // Assert
        Assert.Equal(sut.Length, sut.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// On a real song, the sounds of a clip come loudest first, each counted once, and a clip past the song's end has none.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void Sounding_OnAShortSong_ListsItsSoundsLoudestFirst()
    {
        // Arrange
        ConversionNotes notes = new(Logger.None);
        ModModule module = ModReader.Read(File.ReadAllBytes(TestFiles.ShortSong), notes);
        Song song = ModToSong.Convert(module, AmigaPitch.PalClockHz, notes);
        Playback playback = ModTickPlayer.Play(module, AmigaPitch.PalClockHz, 1.0, notes);
        SongAnalysis analysis = new(song, playback, [], SongVoices.Count(song, playback));

        // Act
        IReadOnlyList<(int Sample, double Amount)> sut = RetryList.Sounding(analysis, 0, 20);
        IReadOnlyList<(int Sample, double Amount)> after = RetryList.Sounding(analysis, playback.DurationSeconds + 1, 20);

        // Assert
        Assert.NotEmpty(sut);
        Assert.Equal(sut.Count, sut.Select(s => s.Sample).Distinct().Count());
        Assert.Equal(sut.Select(s => s.Amount).OrderDescending(), sut.Select(s => s.Amount));
        Assert.Empty(after);
    }

    private static RetryOutcome Outcome(string recipe, double distance) => new(new RetryTry(RetryKind.NoLayers, recipe, recipe), distance, false);

    private static ListeningRow Row(string round, string question, string song, double start, string pick) =>
        new("2026-11-14", round, question, song, start, 20, "xgm2", "--mapping optimizer", "--mapping simple", "c", string.Empty, string.Empty, pick, string.Empty, string.Empty, string.Empty);
}
