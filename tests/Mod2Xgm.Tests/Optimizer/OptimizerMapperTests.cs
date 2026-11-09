using System.Text.RegularExpressions;
using Mod2Xgm.Audio;
using Mod2Xgm.Cli;
using Mod2Xgm.Dmf;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;
using Mod2Xgm.Optimizer;
using Mod2Xgm.Render;
using Serilog.Core;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="OptimizerMapper"/>: the greedy allocator's choices, limits and hard constraints (plan T9.3, docs/adr/0087-greedy-allocator.md).</summary>
public sealed class OptimizerMapperTests
{
    /// <summary>The playback rate of every test note.</summary>
    private const double Rate = 8000;

    /// <summary>A tick's length, in seconds.</summary>
    private const double TickSeconds = 0.02;

    /// <summary>A song that fits keeps every note, each with its closest sound.</summary>
    [Fact]
    public void Map_WithSongThatFits_KeepsEveryNoteOnClosestSound()
    {
        // Arrange
        Song song = SongOf(2, [(0, 0), (1, 1)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Xgm), [Sounds(0), Sounds(1)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        Assert.Equal(0, sut.NotesMoved);
        Assert.Equal(2, plan.Patterns[0][0].Cells.Count(c => c.Voice?.Kind == VoiceKind.Dac));
    }

    /// <summary>A DMF plays one recording at a time, so the more important of two takes the DAC and the other moves on. Pre-mixes are off: this checks who keeps the voice.</summary>
    [Fact]
    public void Map_WithTwoRecordingsOnDmf_LoudestKeepsDacOtherMoves()
    {
        // Arrange
        Song song = SongOf(2, [(0, 0, 0.25), (1, 1, 1.0)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Sounds(0), Sounds(1)], null, null, PreMixRules.None);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        PlanCell dac = plan.Patterns[0][0].Cells[(int)GenesisChannel.Fm6];
        Assert.Equal(VoiceKind.Dac, dac.Voice?.Kind);
        Assert.Equal(1, dac.Sample);
        Assert.Contains(plan.Patterns[0][0].Cells, c => c is { Sample: 0, Voice.Kind: VoiceKind.Fm });
        Assert.Equal(1, sut.NotesMoved);
    }

    /// <summary>XGM's extra recordings get the extra PCM columns, and a DMF's rows stay as wide as its channels.</summary>
    [Fact]
    public void Map_WithFourRecordingsOnXgm_UsesEveryPcmColumn()
    {
        // Arrange
        Song song = SongOf(4, [(0, 0), (1, 1), (2, 2), (3, 3)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Xgm), [Sounds(0), Sounds(1), Sounds(2), Sounds(3)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        IReadOnlyList<PlanCell> cells = plan.Patterns[0][0].Cells;
        Assert.Equal(GenesisPlan.ColumnCount, cells.Count);
        GenesisChannel[] pcm = [GenesisChannel.Fm6, GenesisChannel.Pcm2, GenesisChannel.Pcm3, GenesisChannel.Pcm4];
        Assert.All(pcm, c => Assert.Equal(VoiceKind.Dac, cells[(int)c].Voice?.Kind));
    }

    /// <summary>Each pitch a recording plays at gets a DAC sample of its own, so it is never played at another's pitch.</summary>
    [Fact]
    public void Map_WithSampleAtTwoPitches_MakesTwoDacSamples()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)], [(0, 0)]);
        Playback playback = PlaybackOf(song, rates: [Rate, Rate * 2]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Sounds(0, rates: [Rate, Rate * 2])]);

        // Act
        GenesisPlan plan = sut.Map(song, playback, MappingOptions.Default, Notes());

        // Assert
        Assert.Equal([Rate, Rate * 2], plan.DacSamples.Select(d => d.PlaybackRateHz));
    }

    /// <summary>A note that bends costs a recording more than FM, so FM takes it though the recording is closer on its own.</summary>
    [Fact]
    public void Map_WithBentNote_PrefersFm()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)], [(-1, 0)]);
        Playback playback = PlaybackOf(song, rates: [Rate, Rate * 1.1], newNotes: [true, false]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Sounds(0, recordingDb: 2, fmDb: 8)]);

        // Act
        GenesisPlan plan = sut.Map(song, playback, MappingOptions.Default, Notes());

        // Assert
        Assert.Contains(plan.Patterns[0][0].Cells, c => c is { Sample: 0, Voice.Kind: VoiceKind.Fm });
    }

    /// <summary>A required sample only ever plays as its kind, even when another sound is closer.</summary>
    [Theory]
    [InlineData("fm", "Fm")]
    [InlineData("dac", "Dac")]
    [InlineData("psg", "PsgTone")]
    public void Map_WithRequiredSample_NeverChangesKind(string list, string expected)
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Sounds(0)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), Required(list, 0), Notes());

        // Assert
        Assert.Equal(Enum.Parse<VoiceKind>(expected), Assert.Single(plan.Patterns[0][0].Cells, c => c.Note is not null).Voice?.Kind);
    }

    /// <summary>Two required recordings on one DAC, pre-mixes off: the less important is cut, never moved.</summary>
    [Fact]
    public void Map_WithRequiredDacOverlap_CutsQuieterNote()
    {
        // Arrange
        Song song = SongOf(2, [(0, 0, 0.25), (1, 1, 1.0)]);
        ConversionNotes notes = Notes();
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Sounds(0), Sounds(1)], null, null, PreMixRules.None);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), Required("dac", 0, 1), notes);

        // Assert
        Assert.Equal(1, Assert.Single(plan.Patterns[0][0].Cells, c => c.Note is not null).Sample);
        Assert.Contains(notes.Messages, m => m.Contains("1 are cut", StringComparison.Ordinal));
    }

    /// <summary>A required recording with every PCM voice reserved stops the run with exit 6 and says why, rather than failing on a copy never sized.</summary>
    [Fact]
    public void Map_WithRequiredDacAndNoPcmVoiceLeft_ThrowsExit6()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)]);
        OptimizerMapper sut = new(TargetLimits.For(PcmTarget.Xgm2, 1 << 20, 3), [Sounds(0)]);

        // Act
        ConversionException ex = Assert.Throws<ConversionException>(() => sut.Map(song, PlaybackOf(song), Required("dac", 0), Notes()));

        // Assert
        Assert.Equal(ExitCode.DoesNotFit, ex.Code);
        Assert.Contains("a PCM voice", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Required recordings too big for the budget stop the run with exit 6, naming them.</summary>
    [Fact]
    public void Map_WithRequiredDacOverBudget_ThrowsExit6()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)]);
        OptimizerMapper sut = new(TargetLimits.For(PcmTarget.Xgm, 256, 0), [Sounds(0)]);

        // Act
        ConversionException ex = Assert.Throws<ConversionException>(() => sut.Map(song, PlaybackOf(song), Required("dac", 0), Notes()));

        // Assert
        Assert.Equal(ExitCode.DoesNotFit, ex.Code);
        Assert.Contains("1 \"s0\"", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A recording that doesn't fit the budget, and isn't required, moves to its next best sound.</summary>
    [Fact]
    public void Map_WithRecordingOverBudget_MovesToNextBest()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)]);
        OptimizerMapper sut = new(TargetLimits.For(PcmTarget.Xgm, 256, 0), [Sounds(0)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        Assert.Equal(VoiceKind.Fm, Assert.Single(plan.Patterns[0][0].Cells, c => c is { Note: not null, Level: 1 }).Voice?.Kind);
        Assert.Equal(1, sut.NotesMoved);
    }

    /// <summary>With half speed allowed, a recording a little over budget on XGM2 is stored at half speed and stays a recording, with a conversion note (plan T9.4a).</summary>
    [Fact]
    public void Map_WithHalfSpeedAllowedAndRecordingOverBudgetOnXgm2_StoresItAtHalfSpeed()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)]);
        int full = CapacityCheck.CopyBytes((0, Rate, 0, 0), 1, song, PcmTarget.Xgm2);
        ShrinkRules rules = ShrinkRules.Default with { Order = [ShrinkMove.HalfSpeed] };
        OptimizerMapper sut = new(TargetLimits.For(PcmTarget.Xgm2, full - 1, 0), [Sounds(0)], rules);
        ConversionNotes notes = Notes();

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, notes);

        // Assert
        Assert.Equal(VoiceKind.Dac, Assert.Single(plan.Patterns[0][0].Cells, c => c.Note is not null).Voice?.Kind);
        Assert.True(Assert.Single(plan.DacSamples).HalfSpeed);
        Assert.Equal(0, sut.NotesMoved);
        Assert.Contains(notes.Messages, m => m.Contains("stored at half speed", StringComparison.Ordinal));
    }

    /// <summary>The default rules never store at half speed (David heard it as crunchy, ADR 0091): a recording over budget moves to FM instead.</summary>
    [Fact]
    public void Map_WithDefaultRulesAndRecordingOverBudgetOnXgm2_MovesItInsteadOfHalfSpeed()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)]);
        int full = CapacityCheck.CopyBytes((0, Rate, 0, 0), 1, song, PcmTarget.Xgm2);
        OptimizerMapper sut = new(TargetLimits.For(PcmTarget.Xgm2, full - 1, 0), [Sounds(0)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        Assert.Equal(VoiceKind.Fm, Assert.Single(plan.Patterns[0][0].Cells, c => c is { Note: not null, Level: 1 }).Voice?.Kind);
        Assert.Equal(1, sut.NotesMoved);
    }

    /// <summary>A recording that fits is stored as the song plays it: no shrinking and no note about it (David: a song that fits stays as it is).</summary>
    [Fact]
    public void Map_WithRecordingThatFits_ShrinksNothing()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Xgm2), [Sounds(0)]);
        ConversionNotes notes = Notes();

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, notes);

        // Assert
        Assert.Equal(new DacSample(0, Rate), Assert.Single(plan.DacSamples));
        Assert.Empty(notes.Messages);
    }

    /// <summary>With shrinking turned off, a recording over budget moves to FM as before (the swap-first rule the listening rounds compare).</summary>
    [Fact]
    public void Map_WithShrinkingOff_MovesTheRecordingInstead()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)]);
        int full = CapacityCheck.CopyBytes((0, Rate, 0, 0), 1, song, PcmTarget.Xgm2);
        OptimizerMapper sut = new(TargetLimits.For(PcmTarget.Xgm2, full - 1, 0), [Sounds(0)], ShrinkRules.None);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        Assert.Equal(VoiceKind.Fm, Assert.Single(plan.Patterns[0][0].Cells, c => c is { Note: not null, Level: 1 }).Voice?.Kind);
        Assert.Equal(1, sut.NotesMoved);
    }

    /// <summary>More notes than the whole Genesis can play: the plan stays valid, the least important are cut, each column holds one note.</summary>
    [Fact]
    public void Map_WithMoreNotesThanChannels_StaysValidAndCuts()
    {
        // Arrange
        (int, int)[] cells = [.. Enumerable.Range(0, 16).Select(i => (i, i))];
        Song song = SongOf(16, cells);
        SampleSounds[] menu = [.. Enumerable.Range(0, 16).Select(i => Sounds(i))];
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), menu);
        ConversionNotes notes = Notes();

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, notes);

        // Assert
        IReadOnlyList<PlanCell> row = plan.Patterns[0][0].Cells;
        Assert.Equal(GenesisPlan.ChannelCount, row.Count);
        Assert.Equal(10, row.Count(c => c.Note is not null));
        Assert.Contains(notes.Messages, m => m.Contains("1 are cut", StringComparison.Ordinal));
    }

    /// <summary>
    /// A track's sound between sections when another is closer in the second: per section, it keeps its sound when the
    /// other is only slightly closer and changes when it is clearly closer; in the song's order (the converter's rule,
    /// ADR 0092), it keeps its sound while that sound has room, however much closer the other is.
    /// </summary>
    [Theory]
    [InlineData("PerSection", 5.0, 4.0, "Fm")]
    [InlineData("PerSection", 5.0, 1.0, "PsgTone")]
    [InlineData("SongRank", 5.0, 1.0, "Fm")]
    public void Map_WithTrackAcrossSections_SwitchesOnlyAsTheRuleSays(string rule, double fmDb, double toneDb, string second)
    {
        // Arrange
        Song song = SongOf(1, 2, [(0, 0)], [(0, 0)]);
        Playback playback = PlaybackOf(song, rates: [Rate, Rate * 4]);
        SampleSounds sounds = new(0, 0, [new RegionSounds(0, Rate * 2, FmPatchLibrary.Sine, fmDb, fmDb + 10, null), new RegionSounds(Rate * 2, Rate * 8, FmPatchLibrary.Sine, fmDb, toneDb, null)], new Dictionary<double, double>());
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [sounds], null, Enum.Parse<VoiceSwitching>(rule));

        // Act
        GenesisPlan plan = sut.Map(song, playback, MappingOptions.Default, Notes());

        // Assert
        Assert.Equal(Enum.Parse<VoiceKind>(second), Assert.Single(plan.Patterns[1][0].Cells, c => c is { Note: not null, Level: 1 }).Voice?.Kind);
    }

    /// <summary>
    /// One PCM voice: sample 0 plays in both sections, a more important sample 1 arrives in the second. Per section, the
    /// newcomer takes the voice and sample 0 switches to FM; keeping the voice, sample 0 stays a recording and the newcomer
    /// plays by FM; choosing for the whole song, the newcomer gets the voice and sample 0 plays by FM from the start, so
    /// only the first rule switches a sound mid-song. In the song's order, the newcomer matters more over the song, so it
    /// takes the voice in the second section as per section (plan T9.4c).
    /// Pre-mixes are off: this checks who keeps the voice.</summary>
    [Theory]
    [InlineData("PerSection", "Dac", "Fm", "Dac")]
    [InlineData("KeepPlaying", "Dac", "Dac", "Fm")]
    [InlineData("WholeSong", "Fm", "Fm", "Dac")]
    [InlineData("SongRank", "Dac", "Fm", "Dac")]
    public void Map_WithMoreImportantNewcomer_KeepsOrGivesUpTheVoiceAsTheRuleSays(string rule, string padFirst, string padThen, string newcomer)
    {
        // Arrange: one PCM voice; sample 0 plays in both sections, sample 1, a hit and so more important, only in the second.
        Song song = SongOf(2, 2, [(0, 0)], [(0, 0), (1, 1)]);
        SampleSounds hit = Sounds(1) with { Percussive = 1 };
        OptimizerMapper sut = new(TargetLimits.For(PcmTarget.Xgm2, 1 << 20, 2), [Sounds(0), hit], null, Enum.Parse<VoiceSwitching>(rule), PreMixRules.None);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        Assert.Equal(
            (Enum.Parse<VoiceKind>(padFirst), Enum.Parse<VoiceKind>(padThen), Enum.Parse<VoiceKind>(newcomer)),
            (KindOf(plan, 0, 0), KindOf(plan, 1, 0), KindOf(plan, 1, 1)));
    }

    /// <summary>
    /// One PCM voice: a pad loud in the first section and quiet in the second, where a newcomer louder than the pad's
    /// second note but quieter than its song-wide mean arrives. Per section, the newcomer matters more there and takes the
    /// voice; in the song's order (the converter's rule, ADR 0092), the pad matters more over the song and keeps it.
    /// Pre-mixes are off: this checks who keeps the voice.</summary>
    [Theory]
    [InlineData("PerSection", "Fm", "Dac")]
    [InlineData("SongRank", "Dac", "Fm")]
    public void Map_WithPadQuietWhereNewcomerArrives_RanksBySongOrSectionAsTheRuleSays(string rule, string padThen, string newcomer)
    {
        // Arrange: the pad is at volume 1 then 0.1 (mean 0.55); the newcomer is at 0.5, beside the pad's quiet note.
        Song song = Assemble(2, 2, [
            new Row([new Cell(new NotePitch(48, 0, Rate), 0, 1.0, []), Cell.Empty]),
            new Row([new Cell(new NotePitch(48, 0, Rate), 0, 0.1, []), new Cell(new NotePitch(48, 0, Rate), 1, 0.5, [])])]);
        OptimizerMapper sut = new(TargetLimits.For(PcmTarget.Xgm2, 1 << 20, 2), [Sounds(0), Sounds(1)], null, Enum.Parse<VoiceSwitching>(rule), PreMixRules.None);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        Assert.Equal(
            (VoiceKind.Dac, Enum.Parse<VoiceKind>(padThen), Enum.Parse<VoiceKind>(newcomer)),
            (KindOf(plan, 0, 0), KindOf(plan, 1, 0), KindOf(plan, 1, 1)));
    }

    /// <summary>The voice kind of MOD channel <paramref name="channel"/>'s note on the first row of order <paramref name="order"/>.</summary>
    private static VoiceKind? KindOf(GenesisPlan plan, int order, int channel) =>
        plan.Patterns[order][0].Cells.Single(c => c is { Note: not null, Level: 1 } && c.Source == channel).Voice?.Kind;

    /// <summary>A note of an ignored sample stops the track, as the simple mapper does, and the note is logged.</summary>
    [Fact]
    public void Map_WithIgnoredSample_StopsTrack()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)], [(0, 1)]);
        Playback playback = PlaybackOf(song, sampleOf: [0, 1]);
        ConversionNotes notes = Notes();
        OptimizerMapper sut = new(Limits(PcmTarget.Xgm), [Sounds(0)]);
        MappingOptions options = MappingOptions.Default with { Ignore = new HashSet<int> { 1 } };

        // Act
        GenesisPlan plan = sut.Map(song, playback, options, notes);

        // Assert
        Assert.True(plan.Patterns[0][1].Cells[(int)GenesisChannel.Fm6].NoteOff);
        Assert.Contains(notes.Messages, m => m.Contains("ignored", StringComparison.Ordinal));
    }

    /// <summary>A sample with no steady pitch that loses the DAC takes the PSG's noise, though FM and a PSG tone are closer: a tuned voice plays it as a wrong note (David). Pre-mixes are off: this checks who keeps the voice.</summary>
    [Fact]
    public void Map_WithUnsteadySampleLosingDac_TakesNoise()
    {
        // Arrange
        Song song = SongOf(2, [(0, 0, 1.0), (1, 1, 0.25)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Sounds(0), Sounds(1) with { SteadyPitch = false }], null, null, PreMixRules.None);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        Assert.Equal(VoiceKind.PsgNoise, Assert.Single(plan.Patterns[0][0].Cells, c => c.Sample == 1).Voice?.Kind);
    }

    /// <summary>With the DAC and the noise channel both busy and pre-mixes off, a sample with no steady pitch is cut, and tuned voices stay free.</summary>
    [Fact]
    public void Map_WithUnsteadySamplesOverDacAndNoise_CutsLeastImportant()
    {
        // Arrange
        Song song = SongOf(3, [(0, 0, 1.0), (1, 1, 0.75), (2, 2, 0.25)]);
        SampleSounds[] menu = [.. Enumerable.Range(0, 3).Select(i => Sounds(i) with { SteadyPitch = false })];
        ConversionNotes notes = Notes();
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), menu, null, null, PreMixRules.None);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, notes);

        // Assert
        Assert.DoesNotContain(plan.Patterns[0][0].Cells, c => c.Sample == 2);
        Assert.Contains(notes.Messages, m => m.Contains("1 are cut", StringComparison.Ordinal));
    }

    /// <summary>The user's list wins over the steady-pitch rule: a required sample takes the tuned voice it asks for.</summary>
    [Theory]
    [InlineData("fm", "Fm")]
    [InlineData("psg", "PsgTone")]
    public void Map_WithRequiredUnsteadySample_TakesTunedVoice(string list, string expected)
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Sounds(0) with { SteadyPitch = false }]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), Required(list, 0), Notes());

        // Assert
        Assert.Equal(Enum.Parse<VoiceKind>(expected), Assert.Single(plan.Patterns[0][0].Cells, c => c.Note is not null).Voice?.Kind);
    }

    /// <summary>FM keeps off FM6 while another column is free, even for a song channel of that number: it is the DAC's only column, and FM settled there silently cost a melody its recordings.</summary>
    [Fact]
    public void Map_WithFmOnSixthChannel_LeavesFm6Free()
    {
        // Arrange
        Song song = SongOf(6, [(5, 0)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Sounds(0)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), Required("fm", 0), Notes());

        // Assert
        Assert.Null(plan.Patterns[0][0].Cells[(int)GenesisChannel.Fm6].Note);
        Assert.Equal(VoiceKind.Fm, Assert.Single(plan.Patterns[0][0].Cells, c => c.Note is not null).Voice?.Kind);
    }

    /// <summary>Of two notes alike but for how much they hit, the sample that hits takes the DAC: a replaced attack is easy to hear. Pre-mixes are off: this checks who keeps the voice.</summary>
    [Fact]
    public void Map_WithHitAndHeldNoteOnDmf_HitTakesDac()
    {
        // Arrange
        Song song = SongOf(2, [(0, 0, 1.0), (1, 1, 1.0)]);
        SampleSounds held = Sounds(0) with { Percussive = 0 };
        SampleSounds hit = Sounds(1) with { Percussive = 1 };
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [held, hit], null, null, PreMixRules.None);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        Assert.Equal(1, plan.Patterns[0][0].Cells[(int)GenesisChannel.Fm6].Sample);
    }

    /// <summary>The optimizer's plan asks for the levels and pans David chose by ear.</summary>
    [Fact]
    public void Map_Always_MarksPlanTunedByEar()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Sounds(0)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        Assert.True(plan.TunedByEar);
    }

    /// <summary>The same input makes the same plan.</summary>
    [Fact]
    public void Map_Twice_GivesSamePlan()
    {
        // Arrange
        Song song = SongOf(4, [(0, 0), (1, 1), (2, 2), (3, 3)], [(0, 1), (1, 0)]);
        SampleSounds[] menu = [Sounds(0), Sounds(1), Sounds(2), Sounds(3)];

        // Act
        GenesisPlan first = new OptimizerMapper(Limits(PcmTarget.Dmf), menu).Map(song, PlaybackOf(song), MappingOptions.Default, Notes());
        GenesisPlan second = new OptimizerMapper(Limits(PcmTarget.Dmf), menu).Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        Assert.Equal(Kinds(first), Kinds(second));
    }

    /// <summary>An FM note with a tone channel free also sounds a quiet PSG tone of the same note, from the same song channel.</summary>
    [Fact]
    public void Map_WithFmNoteAndFreeTone_AddsQuietTone()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [FmFirst(0)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        IReadOnlyList<PlanCell> cells = plan.Patterns[0][0].Cells;
        Assert.Equal(VoiceKind.Fm, cells[(int)GenesisChannel.Fm1].Voice?.Kind);
        Assert.Equal(1, cells[(int)GenesisChannel.Fm1].Level);
        PlanCell tone = cells[(int)GenesisChannel.Psg1];
        Assert.Equal(VoiceKind.PsgTone, tone.Voice?.Kind);
        Assert.Equal(OptimizerMapper.ToneLayerLevel, tone.Level);
        Assert.Equal(cells[(int)GenesisChannel.Fm1].Note, tone.Note);
        Assert.Equal(0, tone.Source);
        Assert.Equal(0, sut.NotesMoved);
    }

    /// <summary>A recording stays alone: only FM notes take a tone.</summary>
    [Fact]
    public void Map_WithRecording_AddsNoLayer()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Sounds(0)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        Assert.Single(plan.Patterns[0][0].Cells, c => c.Note is not null);
    }

    /// <summary>A sample required as FM keeps to that one kind.</summary>
    [Fact]
    public void Map_WithRequiredFm_AddsNoLayer()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [FmFirst(0)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), Required("fm", 0), Notes());

        // Assert
        Assert.Single(plan.Patterns[0][0].Cells, c => c.Note is not null);
    }

    /// <summary>A PSG tone holds its pitch, so a note that slides takes none.</summary>
    [Fact]
    public void Map_WithSlidingNote_AddsNoLayer()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)], [(-1, 0)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [FmFirst(0)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song, [Rate, Rate * 1.5], [true, false]), MappingOptions.Default, Notes());

        // Assert
        Assert.Single(plan.Patterns[0][0].Cells, c => c.Note is not null);
    }

    /// <summary>A layer only takes what nothing needs: with the tones busy on one note of a track's section, none of its notes take one, and no note loses its voice.</summary>
    [Fact]
    public void Map_WithTonesBusyForOneNote_LeavesWholeTrackUnlayered()
    {
        // Arrange
        Song song = SongOf(4, [(0, 0)], [(0, 0), (1, 1), (2, 2), (3, 3)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [FmFirst(0), FmFirst(1), FmFirst(2), FmFirst(3)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), Required("psg", 1, 2, 3), Notes());

        // Assert
        Assert.DoesNotContain(plan.Patterns[0].SelectMany(r => r.Cells), c => c is { Sample: 0, Voice.Kind: VoiceKind.PsgTone });
        Assert.Equal(3, plan.Patterns[0][1].Cells.Count(c => c is { Voice.Kind: VoiceKind.PsgTone }));
        Assert.Equal(0, sut.NotesMoved);
    }

    /// <summary>When one note of a track's section can't take a tone, none of them do, so the tone doesn't come and go note by note.</summary>
    [Fact]
    public void Map_WithOneNoteUnableToLayer_LeavesWholeTrackUnlayered()
    {
        // Arrange: the second note slides, and a tone can't follow it.
        Song song = SongOf(1, [(0, 0)], [(0, 0)], [(-1, 0)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [FmFirst(0)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song, [Rate, Rate, Rate * 1.5], [true, true, false]), MappingOptions.Default, Notes());

        // Assert
        Assert.DoesNotContain(plan.Patterns[0].SelectMany(r => r.Cells), c => c.Voice?.Kind == VoiceKind.PsgTone);
    }

    /// <summary>A layer says so in the conversion notes.</summary>
    [Fact]
    public void Map_WithLayer_AddsConversionNote()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [FmFirst(0)]);
        ConversionNotes notes = Notes();

        // Act
        sut.Map(song, PlaybackOf(song), MappingOptions.Default, notes);

        // Assert
        Assert.Contains(notes.Messages, m => m.Contains("1 FM notes also sound a quiet PSG tone", StringComparison.Ordinal));
    }

    /// <summary>A chord's note sounds a second FM voice, the two voices either side of the note and each at its share of the pair.</summary>
    [Fact]
    public void Map_WithChord_PlaysSecondFmVoice()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Chord(0)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        PlanCell[] voices = [.. plan.Patterns[0][0].Cells.Where(c => c.Note is not null)];
        Assert.Equal(2, voices.Length);
        Assert.All(voices, c => Assert.Equal(VoiceKind.Fm, c.Voice?.Kind));
        Assert.Equal([0.5, 1], voices.Select(c => c.Level));
        Assert.Equal([-11, 11], voices.Select(c => c.Cents));
        Assert.Equal("second", plan.FmPatches[voices[1].Voice?.Index ?? -1].Name);
    }

    /// <summary>A chord's second voice is told about in the conversion notes.</summary>
    [Fact]
    public void Map_WithChord_AddsConversionNote()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Chord(0)]);
        ConversionNotes notes = Notes();

        // Act
        sut.Map(song, PlaybackOf(song), MappingOptions.Default, notes);

        // Assert
        Assert.Contains(notes.Messages, m => m.Contains("a chord, so its 1 FM notes also sound a second FM voice", StringComparison.Ordinal));
    }

    /// <summary>With every FM column but FM6 taken by a note, a chord stays one voice: the second never takes FM6, the first DAC voice's only column.</summary>
    [Fact]
    public void Map_WithOnlyFm6Free_LeavesChordOneVoice()
    {
        // Arrange
        Song song = SongOf(5, [(0, 0), (1, 1), (2, 1), (3, 1), (4, 1)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Chord(0), FmFirst(1)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        IReadOnlyList<PlanCell> cells = plan.Patterns[0][0].Cells;
        Assert.Null(cells[(int)GenesisChannel.Fm6].Note);
        Assert.Single(cells, c => c.Sample == 0);
    }

    /// <summary>A sliding chord stays one voice: a slide's cell has one column, so a second voice wouldn't follow.</summary>
    [Fact]
    public void Map_WithSlidingChord_LeavesItOneVoice()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)], [(-1, 0)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Chord(0)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song, [Rate, Rate * 1.5], [true, false]), MappingOptions.Default, Notes());

        // Assert
        Assert.Single(plan.Patterns[0][0].Cells, c => c.Voice?.Kind == VoiceKind.Fm);
    }

    /// <summary>A chord takes the second voice instead of a PSG tone, not as well as one.</summary>
    [Fact]
    public void Map_WithChord_AddsNoTone()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Chord(0)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        Assert.DoesNotContain(plan.Patterns[0][0].Cells, c => c.Voice?.Kind == VoiceKind.PsgTone);
    }

    /// <summary>A required-FM chord still takes its second voice, as that is FM too.</summary>
    [Fact]
    public void Map_WithRequiredFmChord_PlaysSecondFmVoice()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Chord(0)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), Required("fm", 0), Notes());

        // Assert
        Assert.Equal(2, plan.Patterns[0][0].Cells.Count(c => c.Voice?.Kind == VoiceKind.Fm));
    }

    /// <summary>A volume change on a layered note reaches both of its voices.</summary>
    [Fact]
    public void Map_WithVolumeChange_ReachesTheLayer()
    {
        // Arrange
        Song song = Assemble(1, 1, [new Row([new Cell(new NotePitch(48, 0, Rate), 0, 1.0, [])]), new Row([new Cell(null, null, 0.5, [])])]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [FmFirst(0)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        Assert.Equal(0.5, plan.Patterns[0][1].Cells[(int)GenesisChannel.Fm1].Volume);
        Assert.Equal(0.5, plan.Patterns[0][1].Cells[(int)GenesisChannel.Psg1].Volume);
    }

    /// <summary>The channel's next note, played without a layer, stops the tone under the last one.</summary>
    [Fact]
    public void Map_WithNextNoteUnlayered_StopsTheLayer()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)], [(0, 1)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [FmFirst(0), Sounds(1)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        Assert.Equal(VoiceKind.PsgTone, plan.Patterns[0][0].Cells[(int)GenesisChannel.Psg1].Voice?.Kind);
        Assert.True(plan.Patterns[0][1].Cells[(int)GenesisChannel.Psg1].NoteOff);
    }

    /// <summary>
    /// An FM note whose one-shot sample plays out before the channel's next note stops on the row the MOD falls silent,
    /// not before; the key would otherwise stay on (docs/adr/0095-voices-stop-with-the-mod-note.md).
    /// </summary>
    [Fact]
    public void Map_WithFmNoteWhoseSampleEnds_StopsTheVoiceWhenItEnds()
    {
        // Arrange: 4000 frames at 160 frames a tick play out after 25 one-tick rows.
        (int Channel, int Sample)[][] rows = [[(0, 0)], .. Enumerable.Repeat<(int, int)[]>([(-1, 0)], 39)];
        Song song = SongOf(1, rows);
        OptimizerMapper sut = new(Limits(PcmTarget.Xgm), [FmFirst(0)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        IReadOnlyList<PlanRow> pattern = plan.Patterns[0];
        Assert.Equal(VoiceKind.Fm, pattern[0].Cells[(int)GenesisChannel.Fm1].Voice?.Kind);
        Assert.Equal([25], Enumerable.Range(0, pattern.Count).Where(r => pattern[r].Cells[(int)GenesisChannel.Fm1].NoteOff));
    }

    /// <summary>
    /// A recording the MOD fades to silence (a volume slide, <c>C00</c>) stops on the tick its volume reaches 0 for good, as
    /// the DAC keeps the volume a note starts with (docs/adr/0095-voices-stop-with-the-mod-note.md).
    /// </summary>
    [Fact]
    public void Map_WithRecordingFadedToSilence_StopsTheDacWhenSilent()
    {
        // Arrange: full volume on rows 0–2, then 0 to the song's end; the fade bends the note, so the recording is required.
        (int Channel, int Sample)[][] rows = [[(0, 0)], .. Enumerable.Repeat<(int, int)[]>([(-1, 0)], 9)];
        Song song = SongOf(1, rows);
        Playback full = PlaybackOf(song);
        Playback faded = full with { Ticks = [.. full.Ticks.Select((tick, i) => i < 3 ? tick : tick with { Channels = [tick.Channels[0] with { Volume = 0 }] })] };
        OptimizerMapper sut = new(Limits(PcmTarget.Xgm), [Sounds(0)]);

        // Act
        GenesisPlan plan = sut.Map(song, faded, Required("dac", 0), Notes());

        // Assert
        IReadOnlyList<PlanRow> pattern = plan.Patterns[0];
        Assert.Equal(VoiceKind.Dac, pattern[0].Cells[(int)GenesisChannel.Fm6].Voice?.Kind);
        Assert.Equal([3], Enumerable.Range(0, pattern.Count).Where(r => pattern[r].Cells[(int)GenesisChannel.Fm6].NoteOff));
    }

    /// <summary>A sample that ends partway through a row is cut on that tick of the row: the cell keeps its place and takes a note cut.</summary>
    [Fact]
    public void Map_WithSampleEndingMidRow_CutsOnThatTick()
    {
        // Arrange: the song's ticks played three to a row, so the sample ends on tick 25, tick 1 of row 8.
        (int Channel, int Sample)[][] rows = [[(0, 0)], .. Enumerable.Repeat<(int, int)[]>([(-1, 0)], 35)];
        Song song = SongOf(1, rows);
        Playback oneTickRows = PlaybackOf(song);
        Playback threeTickRows = oneTickRows with { Ticks = [.. oneTickRows.Ticks.Select((tick, i) => tick with { Row = i / 3, Tick = i % 3 })] };
        OptimizerMapper sut = new(Limits(PcmTarget.Xgm), [FmFirst(0)]);

        // Act
        GenesisPlan plan = sut.Map(song, threeTickRows, MappingOptions.Default, Notes());

        // Assert
        PlanCell cut = plan.Patterns[0][8].Cells[(int)GenesisChannel.Fm1];
        Assert.False(cut.NoteOff);
        Assert.Equal(new NoteCut(1, Ends: true), Assert.Single(cut.Effects.OfType<NoteCut>()));
    }

    /// <summary>The MOD's own ECx in the cell and the plan's cut merge into one cut, on the earlier tick, so the render and the DMF agree.</summary>
    [Fact]
    public void Map_WithModNoteCutInTheCutCell_KeepsOneCutOnTheEarlierTick()
    {
        // Arrange: as the mid-row test, with EC2 in row 8 of the song.
        (int Channel, int Sample)[][] rows = [[(0, 0)], .. Enumerable.Repeat<(int, int)[]>([(-1, 0)], 35)];
        Song plain = SongOf(1, rows);
        Row[] withCut = [.. plain.Patterns[0].Rows.Select((row, r) => r == 8 ? new Row([Cell.Empty with { Effects = [new NoteCut(2)] }]) : row)];
        Song song = Assemble(1, 1, withCut);
        Playback oneTickRows = PlaybackOf(song);
        Playback threeTickRows = oneTickRows with { Ticks = [.. oneTickRows.Ticks.Select((tick, i) => tick with { Row = i / 3, Tick = i % 3 })] };
        OptimizerMapper sut = new(Limits(PcmTarget.Xgm), [FmFirst(0)]);

        // Act
        GenesisPlan plan = sut.Map(song, threeTickRows, MappingOptions.Default, Notes());

        // Assert
        Assert.Equal(new NoteCut(1, Ends: true), Assert.Single(plan.Patterns[0][8].Cells[(int)GenesisChannel.Fm1].Effects.OfType<NoteCut>()));
    }

    /// <summary>
    /// A row the renderer reads twice (held by EEx: its ticks start over) gets no cut when only one read wants it, as the cut
    /// would fire on the same tick of the other read too, where the note still sounds.
    /// </summary>
    [Fact]
    public void Map_WithSampleEndingInARepeatedRow_LeavesTheVoiceUncut()
    {
        // Arrange: rows of six ticks counted 0, 1, 2, 0, 1, 2; the sample ends on tick 25, tick 1 of row 4's first read only.
        (int Channel, int Sample)[][] rows = [[(0, 0)], .. Enumerable.Repeat<(int, int)[]>([(-1, 0)], 35)];
        Song song = SongOf(1, rows);
        Playback oneTickRows = PlaybackOf(song);
        Playback repeatedRows = oneTickRows with { Ticks = [.. oneTickRows.Ticks.Select((tick, i) => tick with { Row = i / 6, Tick = i % 3 })] };
        OptimizerMapper sut = new(Limits(PcmTarget.Xgm), [FmFirst(0)]);

        // Act
        GenesisPlan plan = sut.Map(song, repeatedRows, MappingOptions.Default, Notes());

        // Assert
        Assert.DoesNotContain(plan.Patterns[0].Select(row => row.Cells[(int)GenesisChannel.Fm1]), cell => cell.NoteOff || cell.Effects.OfType<NoteCut>().Any());
    }

    /// <summary>
    /// A stop followed by another start of the channel later in the same row (E9x, EDx) is left out: the start takes the voice
    /// over, and a cut there would stop the voice before it.
    /// </summary>
    [Fact]
    public void Map_WithNoteStartingLaterInTheRow_LeavesTheEarlierStopOut()
    {
        // Arrange: three-tick rows; the first note plays out on tick 25 (row 8, tick 1) and the next starts on tick 26 (row 8, tick 2).
        (int Channel, int Sample)[][] rows = [[(0, 0)], .. Enumerable.Repeat<(int, int)[]>([(-1, 0)], 35)];
        rows[26] = [(0, 0)];
        Song song = SongOf(1, rows);
        Playback oneTickRows = PlaybackOf(song);
        Playback threeTickRows = oneTickRows with { Ticks = [.. oneTickRows.Ticks.Select((tick, i) => tick with { Row = i / 3, Tick = i % 3 })] };
        OptimizerMapper sut = new(Limits(PcmTarget.Xgm), [FmFirst(0)]);

        // Act
        GenesisPlan plan = sut.Map(song, threeTickRows, MappingOptions.Default, Notes());

        // Assert
        PlanCell cell = plan.Patterns[0][8].Cells[(int)GenesisChannel.Fm1];
        Assert.False(cell.NoteOff);
        Assert.Empty(cell.Effects.OfType<NoteCut>());
    }

    /// <summary>A note that ends because the channel swaps to another sample without a new note keeps sounding: no cut.</summary>
    [Fact]
    public void Map_WithSampleSwappedWithoutANote_LeavesTheVoiceUncut()
    {
        // Arrange: from tick 10, channel 0 goes on to sample 1's loop (an instrument-only cell); channel 1 plays sample 1.
        (int Channel, int Sample)[][] rows = [[(0, 0), (1, 1)], .. Enumerable.Repeat<(int, int)[]>([(-1, 0)], 39)];
        Song song = SongOf(2, rows);
        Playback playback = PlaybackOf(song);
        Playback swapped = playback with
        {
            Ticks = [.. playback.Ticks.Select((tick, i) => i < 10 ? tick : tick with { Channels = [tick.Channels[0] with { Next = new SampleBlock(1, 0, 4000) }, tick.Channels[1]] })],
        };
        OptimizerMapper sut = new(Limits(PcmTarget.Xgm), [FmFirst(0), FmFirst(1)]);

        // Act
        GenesisPlan plan = sut.Map(song, swapped, MappingOptions.Default, Notes());

        // Assert
        int[] columns = [.. Enumerable.Range(0, plan.Patterns[0][0].Cells.Count).Where(c => plan.Patterns[0][0].Cells[c] is { Source: 0, Note: not null })];
        Assert.NotEmpty(columns);
        Assert.DoesNotContain(plan.Patterns[0].Skip(1).SelectMany(row => columns.Select(c => row.Cells[c])), cell => cell.NoteOff || cell.Effects.OfType<NoteCut>().Any());
    }

    /// <summary>Four recordings at once on XGM2's three voices: the fourth is mixed with one playing instead of moving (plan T9.4b).</summary>
    [Fact]
    public void Map_WithMoreRecordingsThanVoices_PreMixesInsteadOfMoving()
    {
        // Arrange
        Song song = SongOf(4, [(0, 0), (1, 1), (2, 2), (3, 3)], [(-1, 0)], [(-1, 0)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Xgm2), [Sounds(0), Sounds(1), Sounds(2), Sounds(3)]);
        ConversionNotes notes = Notes();

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, notes);

        // Assert
        Assert.Equal(0, sut.NotesMoved);
        Assert.Equal(3, plan.Patterns[0][0].Cells.Count(c => c.Voice?.Kind == VoiceKind.Dac));
        DacSample mix = Assert.Single(plan.DacSamples, d => d.Mix is not null);
        Assert.StartsWith("mix ", mix.Mix!.Name, StringComparison.Ordinal);
        Assert.NotEmpty(mix.Mix.Points);
        Assert.Contains(notes.Messages, m => m.Contains("mixed into one recording 1 times", StringComparison.Ordinal));
    }

    /// <summary>
    /// Short recordings on a DMF's one DAC: five at once on the first row, two on the second. The last track to be given
    /// back its recording (channel 4) could be mixed on the second row but not the first, where four already share a mix,
    /// the most one holds. Having no recording anywhere, it stays whole on FM rather than playing one note mixed and the
    /// other by FM (docs/adr/0094-pre-mixed-voice-steals.md).
    /// </summary>
    [Fact]
    public void Map_WithTrackThatCanNotBeMixedEverywhere_MovesItWhole()
    {
        // Arrange
        Sample[] shortSounds = [.. Enumerable.Range(0, 5).Select(i => new Sample($"s{i}", Sound(100, 0.1f), null))];
        Song song = WithSamples(SongOf(5, [(0, 4), (1, 1), (2, 2), (3, 3), (4, 0)], [(0, 4), (4, 0)]), shortSounds);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Sounds(0), Sounds(1), Sounds(2), Sounds(3), Sounds(4)]);
        ConversionNotes notes = Notes();

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, notes);

        // Assert
        Assert.Contains(plan.DacSamples, d => d.Mix is not null);
        Assert.Contains(notes.Messages, m => m.StartsWith("Sample 1 \"s0\": of its 2 notes, 0 play as recordings, 2 by FM", StringComparison.Ordinal));
        Assert.DoesNotContain(notes.Messages, m => Regex.IsMatch(m, @"[1-9]\d* play as recordings, [1-9]\d* by FM"));
    }

    /// <summary>With pre-mixes off, the note that finds no PCM voice moves, as before T9.4b.</summary>
    [Fact]
    public void Map_WithPreMixesOff_MovesTheNote()
    {
        // Arrange
        Song song = SongOf(4, [(0, 0), (1, 1), (2, 2), (3, 3)], [(-1, 0)], [(-1, 0)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Xgm2), [Sounds(0), Sounds(1), Sounds(2), Sounds(3)], null, null, PreMixRules.None);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        Assert.Equal(1, sut.NotesMoved);
        Assert.DoesNotContain(plan.DacSamples, d => d.Mix is not null);
    }

    /// <summary>Two loud notes mix 5.1 dB over full scale: made under a 6 dB limit, refused under a 3 dB one, so the note moves.</summary>
    [Theory]
    [InlineData(6, 0)]
    [InlineData(3, 1)]
    public void Map_WithLoudMix_KeepsToTheClipLimit(double maxClipDb, int moved)
    {
        // Arrange
        Song song = WithSamples(SongOf(2, [(0, 0), (1, 1)], [(-1, 0)]), new Sample("a", Sound(4000, 0.9f), null), new Sample("b", Sound(4000, 0.9f), null));
        PreMixRules rules = PreMixRules.Default with { MaxClipDb = maxClipDb };
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Sounds(0), Sounds(1)], null, null, rules);

        // Act
        sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        Assert.Equal(moved, sut.NotesMoved);
    }

    /// <summary>
    /// A looped note outlasting a one-shot on a DMF's one DAC: with tails, the mix plays to the row the one-shot ends on and the
    /// looped note goes on as a tail there; without, the one-shot moves.
    /// </summary>
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void Map_WithLoopedNoteOutlastingTheMix_GoesOnAsATailWhenAllowed(bool tails, int moved)
    {
        // Arrange
        Song song = WithSamples(
            SongOf(2, [(0, 0), (1, 1)], [(-1, 0)], [(1, 2)], [(-1, 0)], [(-1, 0)]),
            new Sample("pad", Sound(4000, 0.2f), new SampleLoop(0, 4000)),
            new Sample("hit", Sound(4000, 0.3f), null),
            new Sample("lead", Sound(4000, 0.1f), null));
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Sounds(0), Sounds(1), FmFirst(2)], null, null, PreMixRules.Default with { Tails = tails });

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        Assert.Equal(moved, sut.NotesMoved);
        PlanCell fm6 = plan.Patterns[0][2].Cells[(int)GenesisChannel.Fm6];
        Assert.Equal(tails, fm6.Voice is { Kind: VoiceKind.Dac } voice && plan.DacSamples[voice.Index].Mix?.Name == "01 tail");
    }

    /// <summary>A pre-mix reaches the DMF as one DAC sample named for its parts, and the renderer plays it on every driver.</summary>
    [Theory]
    [InlineData(nameof(PcmDriver.Dmf))]
    [InlineData(nameof(PcmDriver.Xgm))]
    [InlineData(nameof(PcmDriver.Xgm2))]
    public void Map_WithPreMix_IsWrittenAndRendered(string driver)
    {
        // Arrange
        Song song = SongOf(2, [(0, 0), (1, 1)], [(-1, 0)], [(-1, 0)]);
        Playback playback = PlaybackOf(song);
        ConversionNotes notes = Notes();
        GenesisPlan plan = new OptimizerMapper(Limits(PcmTarget.Dmf), [Sounds(0), Sounds(1)]).Map(song, playback, MappingOptions.Default, notes);

        // Act
        DmfSong built = DmfSongBuilder.Build(plan, song, DmfTimingPlanner.Plan(plan, 1.0, Region.Pal, notes), new DmfBuildSettings(AmigaPitch.PalClockHz, ResamplingMode.Smooth), notes);
        StereoAudio audio = GenesisRenderer.Render(plan, song, playback, new GenesisRenderOptions(Enum.Parse<PcmDriver>(driver)), notes);

        // Assert
        Assert.Contains(built.File.Samples, s => s.Name.StartsWith("mix 01+02", StringComparison.Ordinal));
        Assert.Contains(audio.Left, x => Math.Abs(x) > 0.01f);
    }

    /// <summary>Each sample gets an FM confidence from its patch's features, with its share of the song's importance, and the plan doesn't change (docs/adr/0096-fm-confidence.md).</summary>
    [Fact]
    public void Map_WithMeasuredPatches_ReportsConfidenceWithoutChangingPlan()
    {
        // Arrange
        Song song = SongOf(2, [(0, 0, 1.0), (1, 1, 1.0)]);
        RegionSounds measured = new(0, 1e6, FmPatchLibrary.Sine, 8, 15, 25, Features: new FmRegionFeatures(new NoteScore(FmConfidenceModel.ProvisionalMidpointDb, 0, null), 0, 0, 0));
        SampleSounds scored = Sounds(0) with { Regions = [measured] };
        OptimizerMapper sut = new(Limits(PcmTarget.Xgm), [scored, Sounds(1)]);
        OptimizerMapper plain = new(Limits(PcmTarget.Xgm), [Sounds(0), Sounds(1)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());
        GenesisPlan before = plain.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        Assert.Equal(Kinds(before), Kinds(plan));
        Assert.Equal([0, 1], sut.Confidence.Select(c => c.Sample));
        Assert.Equal(0.5, sut.Confidence[0].Probability!.Value, 6);
        Assert.Equal(ConfidenceReason.Scored, sut.Confidence[0].Reason);
        Assert.Null(sut.Confidence[1].Probability);
        Assert.Equal(ConfidenceReason.NoFm, sut.Confidence[1].Reason);
        Assert.Equal(0.5, sut.Confidence[0].Exposure, 6);
        Assert.Equal(1.0, sut.Confidence.Sum(c => c.Exposure), 6);
    }

    /// <summary>A sample whose only note slides has all its importance in bent notes, which its FM confidence reports (docs/adr/0096-fm-confidence.md).</summary>
    [Fact]
    public void Map_WithBentNote_ReportsBentShare()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)], [(-1, 0)]);
        Playback playback = PlaybackOf(song, rates: [Rate, Rate * 1.1], newNotes: [true, false]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Sounds(0)]);

        // Act
        sut.Map(song, playback, MappingOptions.Default, Notes());

        // Assert
        Assert.Equal(1.0, sut.Confidence[0].BentShare, 6);
    }

    /// <summary>When the first pass that makes room for pre-mixes finds the song can't fit, the confidence is still there for the report (docs/adr/0096-fm-confidence.md).</summary>
    [Fact]
    public void Map_WithProbeThatCantFit_KeepsConfidence()
    {
        // Arrange
        Song song = SongOf(1, [(0, 0)]);
        OptimizerMapper sut = new(TargetLimits.For(PcmTarget.Xgm, 256, 0), [Sounds(0)], null, null, PreMixRules.Default with { MakeRoom = true });

        // Act
        Assert.Throws<ConversionException>(() => sut.Map(song, PlaybackOf(song), Required("dac", 0), Notes()));

        // Assert
        Assert.Equal(ConfidenceReason.RequiredDac, Assert.Single(sut.Confidence).Reason);
    }

    private static IEnumerable<string> Kinds(GenesisPlan plan) =>
        plan.Patterns.SelectMany(p => p).SelectMany(r => r.Cells).Select(c => $"{c.Voice}/{c.Sample}");

    private static ConversionNotes Notes() => new(Logger.None);

    private static TargetLimits Limits(PcmTarget target) => TargetLimits.For(target, 128 * 1024, 0);

    private static MappingOptions Required(string list, params int[] samples)
    {
        HashSet<int> set = [.. samples];
        return list switch
        {
            "fm" => MappingOptions.Default with { RequireFm = set },
            "dac" => MappingOptions.Default with { RequireDac = set },
            _ => MappingOptions.Default with { RequirePsg = set },
        };
    }

    /// <summary>A sample's menu: a recording closest, then FM, a PSG tone and hiss.</summary>
    private static SampleSounds Sounds(int sample, double recordingDb = 2, double fmDb = 8, double[]? rates = null) => new(
        sample,
        0.5,
        [new RegionSounds(0, 1e6, FmPatchLibrary.Sine, fmDb, 15, 25)],
        (rates ?? [Rate]).ToDictionary(r => r, _ => recordingDb));

    /// <summary>A sample with no recording, so FM is its closest sound.</summary>
    private static SampleSounds FmFirst(int sample) => Sounds(sample, rates: []);

    /// <summary>A sample with no recording and a second FM voice, the two 22 cents apart, the first at half its level.</summary>
    private static SampleSounds Chord(int sample) => new(
        sample,
        0.5,
        [new RegionSounds(0, 1e6, FmPatchLibrary.Sine, 8, 15, 25, FmPatchLibrary.Sine with { Name = "second" }, 0.5, 22)],
        new Dictionary<double, double>());

    /// <summary>A song of one order per row list after the first (<see cref="SongOf(int, int, ValueTuple{int, int}[][])"/> with one order).</summary>
    private static Song SongOf(int channels, params (int Channel, int Sample)[][] rows) => SongOf(channels, 1, rows);

    /// <summary>Notes with volumes on one row.</summary>
    private static Song SongOf(int channels, (int Channel, int Sample, double Volume)[] row)
    {
        Cell[] cells = [.. Enumerable.Repeat(Cell.Empty, channels)];
        foreach ((int channel, int sample, double volume) in row)
        {
            cells[channel] = new Cell(new NotePitch(48, 0, Rate), sample, volume, []);
        }

        return Assemble(channels, 1, [new Row(cells)]);
    }

    /// <summary>
    /// A song whose rows each hold the given (channel, sample) notes; a channel below 0 means a row with no new note.
    /// With several orders, the rows are shared out among them.
    /// </summary>
    private static Song SongOf(int channels, int orders, params (int Channel, int Sample)[][] rows)
    {
        Row[] made = [.. rows.Select(row =>
        {
            Cell[] cells = [.. Enumerable.Repeat(Cell.Empty, channels)];
            foreach ((int channel, int sample) in row.Where(n => n.Channel >= 0))
            {
                cells[channel] = new Cell(new NotePitch(48, 0, Rate), sample, 1.0, []);
            }

            return new Row(cells);
        })];
        return Assemble(channels, orders, made);
    }

    private static Song Assemble(int channels, int orders, Row[] rows)
    {
        int sampleCount = rows.SelectMany(r => r.Cells).Select(c => c.Instrument ?? 0).DefaultIfEmpty(0).Max() + 1;
        Sample[] samples = [.. Enumerable.Range(0, sampleCount).Select(i => new Sample($"s{i}", Sound(4000, 0.1f + (i * 0.01f)), null))];
        Instrument[] instruments = [.. Enumerable.Range(0, sampleCount).Select(i => new Instrument(i, 1, 0))];
        int perOrder = rows.Length / orders;
        Pattern[] patterns = [.. Enumerable.Range(0, orders).Select(o => new Pattern([.. rows.Skip(o * perOrder).Take(perOrder)]))];
        return new Song("song", [.. Enumerable.Repeat(new Channel(0), channels)], [.. Enumerable.Range(0, orders)], null, patterns, instruments, samples, new Timing(1, 125));
    }

    /// <summary><paramref name="song"/> with <paramref name="samples"/> in place of its own.</summary>
    private static Song WithSamples(Song song, params Sample[] samples) =>
        new(song.Title, song.Channels, song.Orders, song.LoopOrder, song.Patterns, [.. Enumerable.Range(0, samples.Length).Select(i => new Instrument(i, 1, 0))], samples, song.Timing);

    private static float[] Sound(int frames, float level)
    {
        float[] data = new float[frames];
        Array.Fill(data, level);
        return data;
    }

    /// <summary>
    /// The song played one tick per row: <paramref name="rates"/> sets each row's rate, <paramref name="newNotes"/> whether its
    /// notes start (a bend otherwise), <paramref name="sampleOf"/> the sample a row plays when it isn't the cell's.
    /// </summary>
    private static Playback PlaybackOf(Song song, double[]? rates = null, bool[]? newNotes = null, int[]? sampleOf = null)
    {
        List<PlaybackTick> ticks = [];
        ChannelTick[] previous = [.. song.Channels.Select(_ => new ChannelTick(false, new NotePitch(48, 0, Rate), 0, 0, null, new SampleBlock(null, 0, 0)))];
        int at = 0;
        for (int o = 0; o < song.Orders.Count; o++)
        {
            IReadOnlyList<Row> rows = song.Patterns[song.Orders[o]].Rows;
            for (int r = 0; r < rows.Count; r++, at++)
            {
                double rate = rates?[at] ?? Rate;
                bool starts = newNotes?[at] ?? true;
                ChannelTick[] channels = new ChannelTick[song.Channels.Count];
                for (int c = 0; c < channels.Length; c++)
                {
                    Cell cell = rows[r].Cells[c];
                    int? sample = sampleOf is null ? cell.Instrument : cell.Note is null ? null : sampleOf[at];
                    channels[c] = sample is { } s && starts
                        ? new ChannelTick(true, new NotePitch(48, 0, rate), cell.Volume ?? 1, 0, new SampleBlock(s, 0, 4000), new SampleBlock(s, 0, 2))
                        : previous[c] with { Started = null, Pitch = new NotePitch(48, 0, previous[c].Playing ? rate : Rate) };
                }

                ticks.Add(new PlaybackTick(at * TickSeconds, TickSeconds, o, r, 0, false, channels));
                previous = channels;
            }
        }

        return new Playback(ticks, null, PlaybackEnd.Stops);
    }
}
