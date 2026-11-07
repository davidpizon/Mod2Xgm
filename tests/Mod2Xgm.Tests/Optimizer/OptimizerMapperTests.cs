using Mod2Xgm.Mapping;
using Mod2Xgm.Model;
using Mod2Xgm.Optimizer;
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

    /// <summary>A DMF plays one recording at a time, so the more important of two takes the DAC and the other moves on.</summary>
    [Fact]
    public void Map_WithTwoRecordingsOnDmf_LoudestKeepsDacOtherMoves()
    {
        // Arrange
        Song song = SongOf(2, [(0, 0, 0.25), (1, 1, 1.0)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Sounds(0), Sounds(1)]);

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

    /// <summary>Two required recordings on one DAC: the less important is cut, never moved.</summary>
    [Fact]
    public void Map_WithRequiredDacOverlap_CutsQuieterNote()
    {
        // Arrange
        Song song = SongOf(2, [(0, 0, 0.25), (1, 1, 1.0)]);
        ConversionNotes notes = Notes();
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Sounds(0), Sounds(1)]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), Required("dac", 0, 1), notes);

        // Assert
        Assert.Equal(1, Assert.Single(plan.Patterns[0][0].Cells, c => c.Note is not null).Sample);
        Assert.Contains(notes.Messages, m => m.Contains("1 are cut", StringComparison.Ordinal));
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

    /// <summary>A track keeps its sound between sections when another is only slightly closer, and changes when another is clearly closer.</summary>
    [Theory]
    [InlineData(5.0, 4.0, "Fm")]
    [InlineData(5.0, 1.0, "PsgTone")]
    public void Map_WithTrackAcrossSections_SticksUnlessClearlyCloser(double fmDb, double toneDb, string second)
    {
        // Arrange
        Song song = SongOf(1, 2, [(0, 0)], [(0, 0)]);
        Playback playback = PlaybackOf(song, rates: [Rate, Rate * 4]);
        SampleSounds sounds = new(0, 0, [new RegionSounds(0, Rate * 2, FmPatchLibrary.Sine, fmDb, fmDb + 10, null), new RegionSounds(Rate * 2, Rate * 8, FmPatchLibrary.Sine, fmDb, toneDb, null)], new Dictionary<double, double>());
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [sounds]);

        // Act
        GenesisPlan plan = sut.Map(song, playback, MappingOptions.Default, Notes());

        // Assert
        Assert.Equal(Enum.Parse<VoiceKind>(second), Assert.Single(plan.Patterns[1][0].Cells, c => c is { Note: not null, Level: 1 }).Voice?.Kind);
    }

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

    /// <summary>A sample with no steady pitch that loses the DAC takes the PSG's noise, though FM and a PSG tone are closer: a tuned voice plays it as a wrong note (David).</summary>
    [Fact]
    public void Map_WithUnsteadySampleLosingDac_TakesNoise()
    {
        // Arrange
        Song song = SongOf(2, [(0, 0, 1.0), (1, 1, 0.25)]);
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [Sounds(0), Sounds(1) with { SteadyPitch = false }]);

        // Act
        GenesisPlan plan = sut.Map(song, PlaybackOf(song), MappingOptions.Default, Notes());

        // Assert
        Assert.Equal(VoiceKind.PsgNoise, Assert.Single(plan.Patterns[0][0].Cells, c => c.Sample == 1).Voice?.Kind);
    }

    /// <summary>With the DAC and the noise channel both busy, a sample with no steady pitch is cut, and tuned voices stay free.</summary>
    [Fact]
    public void Map_WithUnsteadySamplesOverDacAndNoise_CutsLeastImportant()
    {
        // Arrange
        Song song = SongOf(3, [(0, 0, 1.0), (1, 1, 0.75), (2, 2, 0.25)]);
        SampleSounds[] menu = [.. Enumerable.Range(0, 3).Select(i => Sounds(i) with { SteadyPitch = false })];
        ConversionNotes notes = Notes();
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), menu);

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

    /// <summary>Of two notes alike but for how much they hit, the sample that hits takes the DAC: a replaced attack is easy to hear.</summary>
    [Fact]
    public void Map_WithHitAndHeldNoteOnDmf_HitTakesDac()
    {
        // Arrange
        Song song = SongOf(2, [(0, 0, 1.0), (1, 1, 1.0)]);
        SampleSounds held = Sounds(0) with { Percussive = 0 };
        SampleSounds hit = Sounds(1) with { Percussive = 1 };
        OptimizerMapper sut = new(Limits(PcmTarget.Dmf), [held, hit]);

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
