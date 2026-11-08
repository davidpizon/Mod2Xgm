using Mod2Xgm.Model;
using Mod2Xgm.Optimizer;
using Serilog.Events;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="RecordingShrinker"/>: shrinking recordings to fit the target, cheapest to hear first (plan T9.4a).</summary>
public sealed class RecordingShrinkerTests
{
    /// <summary>The rate most test notes start at.</summary>
    private const double Rate = 8000;

    /// <summary>A rate 5 cents above <see cref="Rate"/>.</summary>
    private static readonly double FiveCentsUp = Rate * Math.Pow(2, 5 / 1200.0);

    /// <summary>A rate 50 cents above <see cref="Rate"/>.</summary>
    private static readonly double FiftyCentsUp = Rate * Math.Pow(2, 50 / 1200.0);

    /// <summary>A song whose recordings already fit gets no move, and every copy stays as asked (David: a song that fits keeps every note).</summary>
    [Fact]
    public void Shrink_WhenCopiesFit_MakesNoMove()
    {
        // Arrange
        Song song = SongOf(Tone(1000, 50));
        (int, double, int, int)[] keys = [(0, Rate, 0, 0), (0, FiveCentsUp, 0, 0)];

        // Act
        ShrinkResult result = RecordingShrinker.Shrink(Wanted(keys), Importance(0), song, Limits(PcmTarget.Xgm2, 1 << 20), ShrinkRules.Default, Required());

        // Assert
        Assert.Empty(result.Steps);
        Assert.Equal(2, result.Slots);
    }

    /// <summary>Pitches within the share play the most played one's copy; the note further off keeps its own.</summary>
    [Fact]
    public void Shrink_WithClosePitches_SharesTheMostPlayedCopy()
    {
        // Arrange
        Song song = SongOf(Tone(1000, 50));
        (int, double, int, int)[] keys = [(0, Rate, 0, 0), (0, Rate, 0, 0), (0, FiveCentsUp, 0, 0), (0, FiftyCentsUp, 0, 0)];
        ShrinkRules rules = ShrinkRules.Default with { Order = [ShrinkMove.SharePitch] };

        // Act
        ShrinkResult result = RecordingShrinker.Shrink(Wanted(keys), Importance(0), song, Limits(PcmTarget.Xgm, 2 * CopyBytes(song, Rate)), rules, Required());

        // Assert
        Assert.Equal(Rate, result.Stored[(0, FiveCentsUp, 0, 0)].RateHz);
        Assert.Equal(FiftyCentsUp, result.Stored[(0, FiftyCentsUp, 0, 0)].RateHz);
        Assert.Equal([new ShrinkStep(ShrinkMove.SharePitch, 0, 0, 1, result.Steps[0].BytesSaved)], result.Steps);
    }

    /// <summary>Loudness copies two steps apart: 3 dB and 6 dB down share the 6 dB copy, so 3 copies become 2.</summary>
    [Fact]
    public void Shrink_WithLoudnessShare_KeepsOneCopyPerTwoSteps()
    {
        // Arrange
        Song song = SongOf(Tone(1000, 50));
        (int, double, int, int)[] keys = [(0, Rate, 0, 0), (0, Rate, 0, 1), (0, Rate, 0, 2)];
        ShrinkRules rules = ShrinkRules.Default with { Order = [ShrinkMove.ShareLoudness] };

        // Act
        ShrinkResult result = RecordingShrinker.Shrink(Wanted(keys), Importance(0), song, Limits(PcmTarget.Xgm, 2 * CopyBytes(song, Rate)), rules, Required());

        // Assert
        Assert.Equal(2, result.Slots);
        Assert.Equal(2, result.Stored[(0, Rate, 0, 1)].VolumeSteps);
    }

    /// <summary>A DMF's writer makes its own loudness copies 3 dB apart, so loudness is never shared there.</summary>
    [Fact]
    public void Shrink_OnDmf_NeverSharesLoudness()
    {
        // Arrange
        Song song = SongOf(Tone(1000, 50));
        (int, double, int, int)[] keys = [.. Enumerable.Range(0, 145).Select(i => (0, Rate * (1 + (i * 0.1)), 0, 0)), (0, Rate, 0, 1)];
        ShrinkRules rules = ShrinkRules.Default with { Order = [ShrinkMove.ShareLoudness] };

        // Act
        ShrinkResult result = RecordingShrinker.Shrink(Wanted(keys), Importance(0), song, Limits(PcmTarget.Dmf, 1 << 20), rules, Required());

        // Assert
        Assert.Empty(result.Steps);
    }

    /// <summary>XGM2 at half speed stores half the points, so a copy over budget comes back within it.</summary>
    [Fact]
    public void Shrink_OnXgm2_StoresAtHalfSpeed()
    {
        // Arrange
        Song song = SongOf(Tone(20000, 50));
        int full = CapacityCheck.CopyBytes((0, Rate, 0, 0), 1, song, PcmTarget.Xgm2);
        ShrinkRules rules = ShrinkRules.Default with { Order = [ShrinkMove.HalfSpeed] };

        // Act
        ShrinkResult result = RecordingShrinker.Shrink(Wanted((0, Rate, 0, 0)), Importance(0), song, Limits(PcmTarget.Xgm2, full - 1), rules, Required());

        // Assert
        Assert.True(result.Stored[(0, Rate, 0, 0)].HalfSpeed);
        Assert.InRange(result.TotalBytes, (full / 2) - 256, (full / 2) + 256);
    }

    /// <summary>XGM has no half speed: the move isn't made, and the copy stays over budget for the allocator to deal with.</summary>
    [Fact]
    public void Shrink_OnXgm_NeverStoresAtHalfSpeed()
    {
        // Arrange
        Song song = SongOf(Tone(20000, 50));
        ShrinkRules rules = ShrinkRules.Default with { Order = [ShrinkMove.HalfSpeed] };

        // Act
        ShrinkResult result = RecordingShrinker.Shrink(Wanted((0, Rate, 0, 0)), Importance(0), song, Limits(PcmTarget.Xgm, 256), rules, Required());

        // Assert
        Assert.Empty(result.Steps);
        Assert.False(result.Stored[(0, Rate, 0, 0)].HalfSpeed);
    }

    /// <summary>A sample 6 dB quieter than a look-alike plays the look-alike's copy two loudness steps down, which the song already stores.</summary>
    [Fact]
    public void Shrink_WithLookAlike_MergesTheLessImportantIntoTheOther()
    {
        // Arrange
        Song song = SongOf(Tone(1000, 50), Tone(1000, 50, 0.5f));
        (int, double, int, int)[] keys = [(0, Rate, 0, 2), (1, Rate, 0, 0)];
        ShrinkRules rules = ShrinkRules.Default with { Order = [ShrinkMove.Merge] };
        Dictionary<int, double> importance = new() { [0] = 2, [1] = 1 };

        // Act
        ShrinkResult result = RecordingShrinker.Shrink(Wanted(keys), importance, song, Limits(PcmTarget.Xgm, CopyBytes(song, Rate)), rules, Required());

        // Assert
        Assert.Equal(new StoredCopy(0, Rate, 0, 2, false), result.Stored[(1, Rate, 0, 0)]);
        Assert.Equal(-6.0, result.MergeGainDb[1], 1);
        Assert.Equal(1, result.Slots);
    }

    /// <summary>A sample is never merged into a quieter look-alike, which would need more than full volume: the quieter one merges into it instead.</summary>
    [Fact]
    public void Shrink_WithLouderSampleLeastImportant_MergesTheQuieterIntoIt()
    {
        // Arrange
        Song song = SongOf(Tone(1000, 50), Tone(1000, 50, 0.5f));
        (int, double, int, int)[] keys = [(0, Rate, 0, 2), (1, Rate, 0, 0)];
        ShrinkRules rules = ShrinkRules.Default with { Order = [ShrinkMove.Merge] };
        Dictionary<int, double> importance = new() { [0] = 1, [1] = 2 };

        // Act
        ShrinkResult result = RecordingShrinker.Shrink(Wanted(keys), importance, song, Limits(PcmTarget.Xgm, CopyBytes(song, Rate)), rules, Required());

        // Assert
        ShrinkStep step = Assert.Single(result.Steps);
        Assert.Equal((1, 0), (step.Sample, step.Into));
    }

    /// <summary>A <c>--require-dac</c> sample keeps its own sound: it is never merged into another's.</summary>
    [Fact]
    public void Shrink_WithRequiredLookAlike_DoesNotMergeIt()
    {
        // Arrange
        Song song = SongOf(Tone(1000, 50), Tone(1000, 50));
        (int, double, int, int)[] keys = [(0, Rate, 0, 0), (1, Rate, 0, 0)];
        ShrinkRules rules = ShrinkRules.Default with { Order = [ShrinkMove.Merge] };
        Dictionary<int, double> importance = new() { [0] = 2, [1] = 1 };

        // Act
        ShrinkResult result = RecordingShrinker.Shrink(Wanted(keys), importance, song, Limits(PcmTarget.Xgm, CopyBytes(song, Rate)), rules, Required(1));

        // Assert
        Assert.Equal([ShrinkMove.Merge], result.Steps.Select(s => s.Move));
        Assert.Equal((0, 1), (result.Steps[0].Sample, result.Steps[0].Into));
        Assert.Equal(1, result.Stored[(0, Rate, 0, 0)].Sample);
        Assert.Equal(1, result.Stored[(1, Rate, 0, 0)].Sample);
    }

    /// <summary>The least important sample shrinks first, and the shrinking stops as soon as the copies fit.</summary>
    [Fact]
    public void Shrink_WithTwoSamples_ShrinksOnlyTheLeastImportant()
    {
        // Arrange
        Song song = SongOf(Tone(1000, 50), Tone(1000, 70));
        (int, double, int, int)[] keys = [(0, Rate, 0, 0), (0, FiveCentsUp, 0, 0), (1, Rate, 0, 0), (1, FiveCentsUp, 0, 0)];
        ShrinkRules rules = ShrinkRules.Default with { Order = [ShrinkMove.SharePitch] };
        Dictionary<int, double> importance = new() { [0] = 5, [1] = 1 };

        // Act
        ShrinkResult result = RecordingShrinker.Shrink(Wanted(keys), importance, song, Limits(PcmTarget.Xgm, 3 * CopyBytes(song, Rate)), rules, Required());

        // Assert
        Assert.Equal([1], result.Steps.Select(s => s.Sample));
        Assert.Equal(3, result.Slots);
    }

    /// <summary>With no moves allowed nothing shrinks, however far over the target the copies are.</summary>
    [Fact]
    public void Shrink_WithNoMoves_LeavesEveryCopy()
    {
        // Arrange
        Song song = SongOf(Tone(1000, 50));
        (int, double, int, int)[] keys = [(0, Rate, 0, 0), (0, FiveCentsUp, 0, 0)];

        // Act
        ShrinkResult result = RecordingShrinker.Shrink(Wanted(keys), Importance(0), song, Limits(PcmTarget.Xgm2, 256), ShrinkRules.None, Required());

        // Assert
        Assert.Empty(result.Steps);
        Assert.Equal(2, result.Slots);
    }

    /// <summary>Each move is a conversion note naming the sample and what it saved.</summary>
    [Fact]
    public void Report_WithEveryMove_WritesOneNoteEach()
    {
        // Arrange
        CollectingSink sink = new();
        ConversionNotes notes = new(sink.CreateLogger());
        Song song = SongOf(Tone(1000, 50), Tone(1000, 50));
        ShrinkResult result = new(
            new Dictionary<(int, double, int, int), StoredCopy>(),
            new Dictionary<StoredCopy, int>(),
            [
                new(ShrinkMove.Merge, 1, 0, 1, 1024),
                new(ShrinkMove.SharePitch, 0, 0, 2, 2048),
                new(ShrinkMove.ShareLoudness, 0, 0, 1, 512),
                new(ShrinkMove.HalfSpeed, 0, 0, 0, 4096),
            ],
            new Dictionary<int, double>());

        // Act
        RecordingShrinker.Report(result, song, ShrinkRules.Default, notes);

        // Assert
        Assert.Equal(
            [
                "Sample 2 \"tone\" plays the copies of look-alike sample 1 \"tone\" to fit: 1 slots, 1 KB saved",
                "Sample 1 \"tone\": pitches within 10 cents share one copy to fit, so some notes play up to that far off: 2 slots, 2 KB saved",
                "Sample 1 \"tone\": one copy per 6 dB of loudness to fit, so some notes play up to 3 dB off: 1 slots, 0.5 KB saved",
                "Sample 1 \"tone\": stored at half speed (6.65 kHz) to fit, losing its highs: 4 KB saved",
            ],
            sink.Messages(LogEventLevel.Warning));
    }

    /// <summary>A sample and a quieter copy of it are fully alike, at 6 dB apart.</summary>
    [Fact]
    public void Likeness_WithScaledCopy_IsOneAndLevelIsItsGain()
    {
        // Arrange
        Sample loud = Tone(1000, 50);
        Sample quiet = Tone(1000, 50, 0.5f);

        // Act
        double likeness = RecordingShrinker.Likeness(quiet, loud);
        double level = RecordingShrinker.LevelDb(quiet, loud);

        // Assert
        Assert.Equal(1, likeness, 6);
        Assert.Equal(-6.02, level, 2);
    }

    /// <summary>A different tone, a loop against a one-shot, or a much shorter sample isn't a look-alike.</summary>
    [Fact]
    public void Likeness_WithDifferentSounds_IsLow()
    {
        // Arrange
        Sample tone = Tone(1000, 50);
        Sample looped = new("tone", tone.Data, new SampleLoop(0, 1000));

        // Act
        double other = RecordingShrinker.Likeness(tone, Tone(1000, 37));
        double loop = RecordingShrinker.Likeness(tone, looped);
        double shorter = RecordingShrinker.Likeness(tone, Tone(500, 50));

        // Assert
        Assert.InRange(other, 0, 0.5);
        Assert.Equal(0, loop);
        Assert.Equal(0, shorter);
    }

    /// <summary>Samples with the same data but different loops aren't look-alikes: a merge would swap one loop for the other.</summary>
    [Fact]
    public void Likeness_WithSameDataAndDifferentLoops_IsZero()
    {
        // Arrange
        Sample tone = Tone(1000, 50);
        Sample attack = new("tone", tone.Data, new SampleLoop(0, 200));
        Sample sustain = new("tone", tone.Data, new SampleLoop(600, 400));

        // Act
        double different = RecordingShrinker.Likeness(attack, sustain);
        double same = RecordingShrinker.Likeness(attack, new Sample("tone", tone.Data, new SampleLoop(0, 200)));

        // Assert
        Assert.Equal(0, different);
        Assert.Equal(1, same, 6);
    }

    /// <summary>
    /// A copy's loudness steps: the note's own, less a merge's gain, rounded to the share, never above full or below the
    /// quietest; a note 2.5 dB down (one step) shares the 6 dB copy, as the player plays it.
    /// </summary>
    [Theory]
    [InlineData(1, 0, 2, 2)]
    [InlineData(1, -3, 2, 2)]
    [InlineData(3, 0, 2, 4)]
    [InlineData(2, 6, 1, 0)]
    [InlineData(0, 6, 1, 0)]
    [InlineData(0, -6, 1, 2)]
    public void StoredSteps_WithStepsGainAndShare_RoundsAsTheShrinkerCounts(int steps, double gainDb, int share, int expected)
    {
        // Act
        int stored = RecordingShrinker.StoredSteps(steps, gainDb, share);

        // Assert
        Assert.Equal(expected, stored);
    }

    /// <summary>The bytes one copy of the song's first sample takes on XGM at <paramref name="rate"/>.</summary>
    private static int CopyBytes(Song song, double rate) => CapacityCheck.CopyBytes((0, rate, 0, 0), 1, song, PcmTarget.Xgm);

    /// <summary>One wanted play per key, each held a second.</summary>
    private static List<((int Sample, double RateHz, int StartFrame, int VolumeSteps) Key, double Seconds)> Wanted(params (int, double, int, int)[] keys) =>
        [.. keys.Select(k => (k, 1.0))];

    /// <summary>Equal importance for <paramref name="samples"/>.</summary>
    private static Dictionary<int, double> Importance(params int[] samples) => samples.ToDictionary(s => s, _ => 1.0);

    /// <summary>The <c>--require-dac</c> samples.</summary>
    private static HashSet<int> Required(params int[] samples) => [.. samples];

    /// <summary>The limits of <paramref name="target"/> with <paramref name="budget"/> PCM bytes.</summary>
    private static TargetLimits Limits(PcmTarget target, long budget) => TargetLimits.For(target, budget, 0);

    /// <summary>A song with these samples.</summary>
    private static Song SongOf(params Sample[] samples) =>
        new("song", [new Channel(-1), new Channel(1)], [0], null, [new Pattern([new Row([Cell.Empty, Cell.Empty])])], [], samples, new Timing(6, 125));

    /// <summary>A one-shot sine of <paramref name="frames"/> frames, <paramref name="period"/> frames per cycle.</summary>
    private static Sample Tone(int frames, int period, float level = 1f) =>
        new("tone", [.. Enumerable.Range(0, frames).Select(i => level * (float)Math.Sin(2 * Math.PI * i / period))], null);
}
