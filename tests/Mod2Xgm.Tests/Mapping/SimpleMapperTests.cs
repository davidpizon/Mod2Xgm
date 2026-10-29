using Mod2Xgm.Cli;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;

namespace Mod2Xgm.Tests.Mapping;

/// <summary>
/// Tests for <see cref="SimpleMapper"/> (plan T5.3): where each kind of sample is played, how shared channels are
/// settled, what <c>--require-fm</c>, <c>--require-dac</c>, <c>--require-psg</c> and <c>--no-dac</c> change, and the conversion notes.
/// </summary>
public sealed class SimpleMapperTests
{
    /// <summary>Instrument numbers in <see cref="Build"/>: a looped sine, two tonal drums, a noise hit and an empty slot.</summary>
    private const int Organ = 0;

    private const int Kick = 1;
    private const int Tom = 2;
    private const int Hat = 3;
    private const int Empty = 4;

    private static NotePitch Note(int semitone = 48, double rate = 8287) => new(semitone, 0, rate);

    private static Cell Play(int instrument, int semitone = 48, double? volume = null, double rate = 8287, params Effect[] effects) =>
        new(Note(semitone, rate), instrument, volume, effects);

    private static Cell NoteOnly(int semitone = 48) => new(Note(semitone), null, null, []);

    private static Row RowOf(params Cell[] cells) => new([.. cells, .. Enumerable.Repeat(Cell.Empty, 4 - cells.Length)]);

    /// <summary>A song with the five instruments above, 4 channels, one order per given pattern.</summary>
    private static Song Build(Row[][] patterns, int channels = 4)
    {
        float[] tone = [.. Enumerable.Range(0, 1024).Select(i => (float)Math.Sin(2 * Math.PI * i / 64))];
        float[] thump = [.. Enumerable.Range(0, 2000).Select(i => (float)(Math.Sin(2 * Math.PI * i / 128) * Math.Exp(-i / 500.0)))];
        float[] white = [.. new Random(7).GetItems([-1f, 1f], 2000)];
        Sample[] samples =
        [
            new("organ", tone, new SampleLoop(0, 1024)),
            new("kick", thump, null),
            new("tom", thump, null),
            new("hat", white, null),
            new("empty", [], null),
        ];
        Instrument[] instruments = [.. samples.Select((s, i) => new Instrument(s.Name, i, 0.5, 0))];
        Pattern[] built = [.. patterns.Select(rows => new Pattern([.. rows.Select(r => r.Cells.Count == channels ? r : new Row([.. r.Cells.Take(channels), .. Enumerable.Repeat(Cell.Empty, channels - r.Cells.Count)]))]))];
        return new Song("test", [.. Enumerable.Repeat(new Channel(0), channels)], [.. Enumerable.Range(0, built.Length)], null, built, instruments, samples, new Timing(6, 125));
    }

    private static (GenesisPlan Plan, List<string> Notes) Map(Song song, MappingOptions? options = null)
    {
        CollectingSink sink = new();
        ConversionNotes notes = new(sink.CreateLogger());
        IChannelMapper mapper = new SimpleMapper();
        GenesisPlan plan = mapper.Map(song, options ?? MappingOptions.Default, notes);
        return (plan, [.. notes.Messages]);
    }

    private static PlanCell CellAt(GenesisPlan plan, GenesisChannel channel, int order = 0, int row = 0) =>
        plan.Patterns[order][row].Cells[(int)channel];

    private static MappingOptions Options(int[]? requireFm = null, int[]? requireDac = null, int[]? requirePsg = null, bool useDac = true) =>
        new(new HashSet<int>(requireFm ?? []), new HashSet<int>(requireDac ?? []), new HashSet<int>(requirePsg ?? []), useDac);

    /// <summary>The plan's columns follow DMF channel order, which the mapper relies on when it turns a MOD channel number into a column.</summary>
    [Fact]
    public void PlanChannelsFollowDmfOrder() =>
        Assert.Equal(
            [
                GenesisChannel.Fm1, GenesisChannel.Fm2, GenesisChannel.Fm3, GenesisChannel.Fm4, GenesisChannel.Fm5, GenesisChannel.Fm6,
                GenesisChannel.Psg1, GenesisChannel.Psg2, GenesisChannel.Psg3, GenesisChannel.Psg4,
            ],
            Enum.GetValues<GenesisChannel>());

    /// <summary>A sample that loops is a sustained tone: it goes to the MOD channel's own FM channel with a library patch.</summary>
    [Fact]
    public void LoopedSamplesPlayOnTheirOwnFmChannel()
    {
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Cell.Empty, Play(Organ, 57))]]));

        PlanCell cell = CellAt(plan, GenesisChannel.Fm2);
        Assert.Equal(new PlanVoice(VoiceKind.Fm, 0), cell.Voice);
        Assert.Equal(57, cell.Note?.Semitone);
        Assert.Equal(0.5, cell.Volume);
        Assert.Equal(FmPatchLibrary.Sine, plan.FmPatches[0]);
        Assert.Empty(plan.DacSamples);
        Assert.Contains(notes, n => n.Contains("Sample 1 \"organ\": it loops", StringComparison.Ordinal) && n.Contains("Sine", StringComparison.Ordinal));
        Assert.All(Enumerable.Range(0, GenesisPlan.ChannelCount).Where(c => c != (int)GenesisChannel.Fm2), c => Assert.Equal(PlanCell.Empty, plan.Patterns[0][0].Cells[c]));
    }

    /// <summary>
    /// Every cell says which song channel it came from (writers pan by it) and a note's cell which sample it plays (writers
    /// measure its pitch); effects-only cells name their channel too, also on the shared DAC.
    /// </summary>
    [Fact]
    public void CellsNameTheirSourceChannelAndSample()
    {
        (GenesisPlan plan, _) = Map(Build([[
            RowOf(Cell.Empty, Play(Organ), Cell.Empty, Play(Kick)),
            RowOf(Cell.Empty, new Cell(null, null, 0.25, []), Cell.Empty, new Cell(null, null, null, [new Retrigger(3)])),
        ]]));

        Assert.Equal((1, Organ), (CellAt(plan, GenesisChannel.Fm2).Source, CellAt(plan, GenesisChannel.Fm2).Sample));
        Assert.Equal((3, Kick), (CellAt(plan, GenesisChannel.Fm6).Source, CellAt(plan, GenesisChannel.Fm6).Sample));
        Assert.Equal(1, CellAt(plan, GenesisChannel.Fm2, row: 1).Source);
        Assert.Null(CellAt(plan, GenesisChannel.Fm2, row: 1).Sample);
        Assert.Equal(3, CellAt(plan, GenesisChannel.Fm6, row: 1).Source);
        Assert.Null(CellAt(plan, GenesisChannel.Fm1).Source);
    }

    /// <summary>The note explaining why a sample is played by FM is said once, when it is first played, and not for unplayed samples.</summary>
    [Fact]
    public void RoutingNotesAreSaidOncePerPlayedInstrument()
    {
        (_, List<string> notes) = Map(Build([[RowOf(Play(Organ)), RowOf(Play(Organ, 50)), RowOf(Play(Organ, 52))]]));

        Assert.Single(notes);
    }

    /// <summary>A sample that plays once is a recording: it goes to the DAC (FM6), whichever MOD channel plays it.</summary>
    [Fact]
    public void OneShotSamplesPlayOnTheDac()
    {
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Cell.Empty, Cell.Empty, Play(Kick))]]));

        PlanCell cell = CellAt(plan, GenesisChannel.Fm6);
        Assert.Equal(new PlanVoice(VoiceKind.Dac, 0), cell.Voice);
        DacSample dac = Assert.Single(plan.DacSamples);
        Assert.Equal((Kick, 8287.0), (dac.Sample, dac.PlaybackRateHz));
        Assert.Empty(plan.FmPatches);
        Assert.Empty(notes);
        Assert.Equal(PlanCell.Empty, CellAt(plan, GenesisChannel.Fm3));
    }

    /// <summary>DAC samples are numbered by first use, and each plays at the rate of the pitch used most often.</summary>
    [Fact]
    public void DacSamplesPlayAtTheirMostUsedPitch()
    {
        Cell again = new(new NotePitch(48, 0, 9000), null, null, []);
        Row[] rows = [RowOf(Play(Kick, 36, rate: 4000), Play(Tom, 40, rate: 5000)), RowOf(again), RowOf(again)];

        (GenesisPlan plan, _) = Map(Build([rows]));

        // Row 0: kick and tom collide on the DAC (same volume, so channel 1 wins); the tom goes to FM.
        // The kick is then played twice at semitone 48 (rate 9000) and once at 36 (rate 4000): 9000 wins.
        Assert.Equal(new PlanVoice(VoiceKind.Dac, 0), CellAt(plan, GenesisChannel.Fm6).Voice);
        DacSample dac = Assert.Single(plan.DacSamples);
        Assert.Equal((Kick, 9000.0), (dac.Sample, dac.PlaybackRateHz));
    }

    /// <summary>Instrument numbers on the DAC are assigned in order of first use across the whole song.</summary>
    [Fact]
    public void DacSampleIndexFollowsFirstUse()
    {
        (GenesisPlan plan, _) = Map(Build([[RowOf(Play(Tom)), RowOf(Play(Kick)), RowOf(Play(Tom))]]));

        Assert.Equal([Tom, Kick], plan.DacSamples.Select(d => d.Sample));
        Assert.Equal(1, CellAt(plan, GenesisChannel.Fm6, row: 1).Voice?.Index);
        Assert.Equal(0, CellAt(plan, GenesisChannel.Fm6, row: 2).Voice?.Index);
    }

    /// <summary>The louder of two drum notes on the same row keeps the DAC; a tie goes to the lower channel.</summary>
    [Theory]
    [InlineData(0.9, 0.3, 0)]
    [InlineData(0.3, 0.9, 1)]
    [InlineData(0.5, 0.5, 0)]
    public void TheLouderDrumKeepsTheDac(double first, double second, int winnerChannel)
    {
        (GenesisPlan plan, _) = Map(Build([[RowOf(Play(Kick, volume: first), Play(Tom, 40, volume: second))]]));

        int winnerSample = winnerChannel == 0 ? Kick : Tom;
        Assert.Equal(winnerSample, plan.DacSamples[CellAt(plan, GenesisChannel.Fm6).Voice!.Value.Index].Sample);
    }

    /// <summary>A tonal drum that loses the DAC plays on its own FM channel instead, with a note saying so.</summary>
    [Fact]
    public void ALosingTonalDrumMovesToFm()
    {
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Play(Kick, volume: 0.9), Play(Tom, 40, volume: 0.3))]]));

        PlanCell moved = CellAt(plan, GenesisChannel.Fm2);
        Assert.Equal(VoiceKind.Fm, moved.Voice?.Kind);
        Assert.Equal(FmPatchLibrary.Pluck, plan.FmPatches[moved.Voice!.Value.Index]);
        Assert.Contains(notes, n => n.StartsWith("1 drum notes started on the same row", StringComparison.Ordinal) && n.EndsWith("played by FM instead", StringComparison.Ordinal));
    }

    /// <summary>A noise drum that loses the DAC plays on the PSG noise channel.</summary>
    [Fact]
    public void ALosingNoiseDrumMovesToThePsgNoiseChannel()
    {
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Play(Kick, volume: 0.9), Play(Hat, volume: 0.3))]]));

        Assert.Equal(new PlanVoice(VoiceKind.PsgNoise, 0), CellAt(plan, GenesisChannel.Psg4).Voice);
        Assert.Equal(PlanCell.Empty, CellAt(plan, GenesisChannel.Fm2));
        Assert.Contains(notes, n => n.Contains("played by the PSG noise channel instead", StringComparison.Ordinal));
    }

    /// <summary>A <c>--require-dac</c> note that loses the DAC is dropped, never moved to FM or PSG.</summary>
    [Fact]
    public void ALosingRequiredDacNoteIsDropped()
    {
        (GenesisPlan plan, List<string> notes) = Map(
            Build([[RowOf(Play(Kick, volume: 0.9), Play(Tom, 40, volume: 0.3))]]),
            Options(requireDac: [Tom]));

        Assert.Equal(PlanCell.Empty, CellAt(plan, GenesisChannel.Fm2));
        Assert.Empty(plan.FmPatches);
        Assert.Contains(notes, n => n.StartsWith("1 --require-dac notes", StringComparison.Ordinal));
    }

    /// <summary><c>--no-dac</c>: a tonal one-shot is an FM Pluck, a noise one-shot is the PSG noise channel.</summary>
    [Fact]
    public void WithoutTheDacOneShotsGoToFmAndNoise()
    {
        (GenesisPlan plan, List<string> notes) = Map(
            Build([[RowOf(Play(Kick), Play(Hat))]]),
            Options(useDac: false));

        Assert.Equal(VoiceKind.Fm, CellAt(plan, GenesisChannel.Fm1).Voice?.Kind);
        Assert.Equal(FmPatchLibrary.Pluck, plan.FmPatches[0]);
        Assert.Equal(VoiceKind.PsgNoise, CellAt(plan, GenesisChannel.Psg4).Voice?.Kind);
        Assert.Equal(PlanCell.Empty, CellAt(plan, GenesisChannel.Fm6));
        Assert.Empty(plan.DacSamples);
        Assert.Contains(notes, n => n.Contains("Sample 2 \"kick\": the DAC is off", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Contains("Sample 4 \"hat\": it is noise and the DAC is off", StringComparison.Ordinal));
    }

    /// <summary>Two noise notes on the same row with no DAC: the louder keeps the noise channel, the other plays on FM.</summary>
    [Fact]
    public void ALosingNoiseNoteMovesToFm()
    {
        (GenesisPlan plan, List<string> notes) = Map(
            Build([[RowOf(Play(Hat, volume: 0.2), Play(Hat, volume: 0.8))]]),
            Options(useDac: false));

        Assert.Equal(VoiceKind.PsgNoise, CellAt(plan, GenesisChannel.Psg4).Voice?.Kind);
        Assert.Equal(VoiceKind.Fm, CellAt(plan, GenesisChannel.Fm1).Voice?.Kind);
        Assert.Equal(PlanCell.Empty, CellAt(plan, GenesisChannel.Fm2));
        Assert.Contains(notes, n => n.StartsWith("1 noise notes started on the same row", StringComparison.Ordinal));
    }

    /// <summary><c>--require-fm</c> sends a one-shot to FM even though the DAC is free.</summary>
    [Fact]
    public void RequiredFmBeatsTheDac()
    {
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Play(Kick))]]), Options(requireFm: [Kick]));

        Assert.Equal(VoiceKind.Fm, CellAt(plan, GenesisChannel.Fm1).Voice?.Kind);
        Assert.Empty(plan.DacSamples);
        Assert.Contains(notes, n => n.Contains("required as FM (--require-fm)", StringComparison.Ordinal));
    }

    /// <summary><c>--require-dac</c> sends even a looped sample to the DAC.</summary>
    [Fact]
    public void RequiredDacBeatsTheLoopRule()
    {
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Play(Organ))]]), Options(requireDac: [Organ]));

        Assert.Equal(VoiceKind.Dac, CellAt(plan, GenesisChannel.Fm6).Voice?.Kind);
        Assert.Empty(plan.FmPatches);
        Assert.Empty(notes);
    }

    /// <summary><c>--require-psg</c> sends a tonal one-shot and a looped sample to the PSG noise channel, with the DAC and FM both free.</summary>
    [Fact]
    public void RequiredPsgBeatsTheDacAndTheLoopRule()
    {
        (GenesisPlan plan, List<string> notes) = Map(
            Build([[RowOf(Play(Kick), Play(Organ))]]),
            Options(requirePsg: [Kick, Organ]));

        Assert.Equal(VoiceKind.PsgNoise, CellAt(plan, GenesisChannel.Psg4).Voice?.Kind);
        Assert.Equal(PlanCell.Empty, CellAt(plan, GenesisChannel.Fm6));
        Assert.Empty(plan.DacSamples);
        Assert.Empty(plan.FmPatches);
        Assert.Contains(notes, n => n.Contains("required as PSG (--require-psg)", StringComparison.Ordinal));
    }

    /// <summary>A <c>--require-psg</c> note that loses the noise channel is dropped, never moved to FM; an ordinary noise note would move.</summary>
    [Fact]
    public void ALosingRequiredPsgNoteIsDropped()
    {
        (GenesisPlan plan, List<string> notes) = Map(
            Build([[RowOf(Play(Hat, volume: 0.9), Play(Kick, volume: 0.3))]]),
            Options(requirePsg: [Kick], useDac: false));

        Assert.Equal(PlanCell.Empty, CellAt(plan, GenesisChannel.Fm2));
        Assert.Empty(plan.FmPatches);
        Assert.Contains(notes, n => n.StartsWith("1 --require-psg notes", StringComparison.Ordinal));
    }

    /// <summary><c>--no-dac</c> and <c>--require-dac</c> contradict each other.</summary>
    [Fact]
    public void NoDacWithRequiredDacIsRefused() =>
        Assert.Throws<ArgumentException>(() => Map(Build([[RowOf()]]), Options(requireDac: [Kick], useDac: false)));

    /// <summary>A required sample that is empty or never played gets a note saying the requirement has no effect.</summary>
    [Fact]
    public void RequirementsOnUnplayedSamplesAreNoted()
    {
        (_, List<string> notes) = Map(Build([[RowOf(Play(Organ))]]), Options(requireFm: [Empty], requireDac: [Tom], requirePsg: [Hat]));

        Assert.Contains(notes, n => n.Contains("Sample 5 \"empty\": required as FM, but it is empty or never played", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Contains("Sample 3 \"tom\": required as DAC", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Contains("Sample 4 \"hat\": required as PSG, but it is empty or never played", StringComparison.Ordinal));
    }

    /// <summary>When a MOD channel moves from an FM note to a drum, its FM channel is told to stop.</summary>
    [Fact]
    public void LeavingFmStopsTheFmChannel()
    {
        (GenesisPlan plan, _) = Map(Build([[RowOf(Play(Organ)), RowOf(Play(Kick)), RowOf(NoteOnly())]]));

        Assert.True(CellAt(plan, GenesisChannel.Fm1, row: 1).NoteOff);
        Assert.Equal(VoiceKind.Dac, CellAt(plan, GenesisChannel.Fm6, row: 1).Voice?.Kind);
        Assert.False(CellAt(plan, GenesisChannel.Fm1, row: 2).NoteOff);
    }

    /// <summary>A volume change is passed on to the FM channel only while the MOD channel sounds on FM.</summary>
    [Fact]
    public void VolumeChangesFollowTheChannel()
    {
        Cell volumeOnly = new(null, null, 0.25, []);
        (GenesisPlan plan, _) = Map(Build([[RowOf(Play(Organ)), RowOf(volumeOnly), RowOf(Play(Kick)), RowOf(volumeOnly)]]));

        Assert.Equal(0.25, CellAt(plan, GenesisChannel.Fm1, row: 1).Volume);
        Assert.Null(CellAt(plan, GenesisChannel.Fm1, row: 3).Volume);
        Assert.Null(CellAt(plan, GenesisChannel.Fm6, row: 3).Volume);
    }

    /// <summary>A note takes the volume of its cell, else the instrument's default, else what the channel carried.</summary>
    [Fact]
    public void NotesCarryTheirStartingVolume()
    {
        Cell set = new(null, null, 0.75, []);
        (GenesisPlan plan, _) = Map(Build([[RowOf(Play(Organ)), RowOf(set), RowOf(NoteOnly(50)), RowOf(Play(Organ, 52, volume: 0.1))]]));

        Assert.Equal(0.5, CellAt(plan, GenesisChannel.Fm1, row: 0).Volume);
        Assert.Equal(0.75, CellAt(plan, GenesisChannel.Fm1, row: 2).Volume);
        Assert.Equal(0.1, CellAt(plan, GenesisChannel.Fm1, row: 3).Volume);
    }

    /// <summary>A note with no instrument chosen yet is skipped, with a note; so is one on an empty sample slot.</summary>
    [Fact]
    public void NotesWithoutSoundAreSkipped()
    {
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(NoteOnly()), RowOf(Play(Empty))]]));

        Assert.All(plan.Patterns[0], row => Assert.All(row.Cells, cell => Assert.Null(cell.Note)));
        Assert.Contains("1 notes were played before their channel had an instrument; left out", notes);
        Assert.Contains("1 notes use an empty sample slot; left out", notes);
    }

    /// <summary>A song with more channels than FM1-5 can carry leaves the extra ones out, with a note.</summary>
    [Fact]
    public void ChannelsBeyondTheFifthAreLeftOut()
    {
        Row row = new([Cell.Empty, Cell.Empty, Cell.Empty, Cell.Empty, Cell.Empty, Play(Organ)]);
        (GenesisPlan plan, List<string> notes) = Map(Build([[row]], channels: 6));

        Assert.All(plan.Patterns[0][0].Cells, cell => Assert.Null(cell.Note));
        Assert.Contains("1 notes are on MOD channels above 5, which have no FM channel; left out", notes);
    }

    /// <summary>Song-wide effects go to the row's flow list; channel effects go with the channel's sound.</summary>
    [Fact]
    public void EffectsAreSplitIntoFlowAndChannelEffects()
    {
        Effect speed = new SetSpeed(3);
        Effect arpeggio = new Arpeggio(4, 7);
        Effect jump = new PositionJump(0);
        (GenesisPlan plan, _) = Map(Build([[
            RowOf(Play(Organ, effects: [speed, arpeggio])),
            RowOf(Cell.Empty, Play(Kick, effects: [arpeggio, jump])),
            RowOf(Cell.Empty, new Cell(null, null, null, [arpeggio])),
            RowOf(new Cell(null, null, null, [arpeggio])),
        ]]));

        Assert.Equal([speed], plan.Patterns[0][0].Flow);
        Assert.Equal([arpeggio], CellAt(plan, GenesisChannel.Fm1, row: 0).Effects);
        Assert.Equal([jump], plan.Patterns[0][1].Flow);
        Assert.Equal([arpeggio], CellAt(plan, GenesisChannel.Fm6, row: 1).Effects);

        // Channel 2 now sounds on the DAC, so an effect on a later row follows it there.
        Assert.Equal([arpeggio], CellAt(plan, GenesisChannel.Fm6, row: 2).Effects);
        Assert.Empty(CellAt(plan, GenesisChannel.Fm2, row: 2).Effects);

        // Channel 1 still sounds on FM1.
        Assert.Equal([arpeggio], CellAt(plan, GenesisChannel.Fm1, row: 3).Effects);
    }

    /// <summary>The plan has one pattern per order, and the instrument and volume a channel carries flow from order to order.</summary>
    [Fact]
    public void StateFlowsAcrossOrders()
    {
        Song song = Build([[RowOf(Play(Organ))], [RowOf(NoteOnly(60))]]);

        (GenesisPlan plan, _) = Map(song);

        Assert.Equal(2, plan.Patterns.Count);
        Assert.Equal(60, CellAt(plan, GenesisChannel.Fm1, order: 1).Note?.Semitone);
        Assert.Equal(song.Timing, plan.Timing);
        Assert.Equal(song.LoopOrder, plan.LoopOrder);
        Assert.Equal("test", plan.Title);
        Assert.Equal(GenesisPlan.ChannelCount, plan.Patterns[0][0].Cells.Count);
    }

    /// <summary>The same song and options always give the same plan.</summary>
    [Fact]
    public void MappingIsDeterministic()
    {
        Song song = Build([[RowOf(Play(Organ), Play(Kick), Play(Hat), Play(Tom))]]);

        (GenesisPlan first, List<string> firstNotes) = Map(song);
        (GenesisPlan second, List<string> secondNotes) = Map(song);

        Assert.Equal(firstNotes, secondNotes);
        Assert.Equal(first.FmPatches, second.FmPatches);
        Assert.Equal(first.DacSamples, second.DacSamples);
        Assert.Equal(first.Patterns[0][0].Cells, second.Patterns[0][0].Cells);
    }

    /// <summary>The command line's 1-based numbers become the mapper's 0-based ones, and <c>--no-dac</c> turns the DAC off.</summary>
    [Fact]
    public void OptionsComeFromTheCommandLine()
    {
        ConversionOptions cli = new()
        {
            InputPath = "x.mod",
            RequireFm = new HashSet<int> { 1, 3 },
            RequireDac = new HashSet<int> { 2 },
            RequirePsg = new HashSet<int> { 4 },
            IgnoreSamples = new HashSet<int> { 5, 7 },
            NoDac = false,
        };

        MappingOptions options = MappingOptions.From(cli);

        Assert.Equal([0, 2], options.RequireFm.Order());
        Assert.Equal([1], options.RequireDac);
        Assert.Equal([3], options.RequirePsg);
        Assert.Equal([4, 6], options.Ignore.Order());
        Assert.True(options.UseDac);
        Assert.False(MappingOptions.From(cli with { RequireDac = new HashSet<int>(), NoDac = true }).UseDac);
        Assert.Empty(MappingOptions.Default.RequireFm);
        Assert.Empty(MappingOptions.Default.Ignore);
    }

    /// <summary>
    /// A note of an ignored sample is left out and silences its track: the FM note the track was holding gets a note off,
    /// and the next note plays again. One conversion note per ignored sample says how many notes were dropped.
    /// </summary>
    [Fact]
    public void IgnoredSamplesSilenceTheirTrack()
    {
        Song song = Build([[RowOf(Play(Organ)), RowOf(Play(Kick)), RowOf(Play(Kick)), RowOf(Play(Organ, 50))]]);

        (GenesisPlan plan, List<string> notes) = Map(song, MappingOptions.Default with { Ignore = new HashSet<int> { Kick } });

        Assert.True(CellAt(plan, GenesisChannel.Fm1, row: 1).NoteOff);
        Assert.Null(CellAt(plan, GenesisChannel.Fm1, row: 1).Note);
        Assert.Equal(PlanCell.Empty, CellAt(plan, GenesisChannel.Fm1, row: 2));
        Assert.All(plan.Patterns[0], row => Assert.Null(row.Cells[(int)GenesisChannel.Fm6].Note));
        Assert.Empty(plan.DacSamples);
        Assert.Equal(50, CellAt(plan, GenesisChannel.Fm1, row: 3).Note?.Semitone);
        Assert.Contains("Sample 2 \"kick\": ignored (--ignore-samples), so its 2 notes are left out and their tracks are silent until the next note", notes);
    }

    /// <summary>An ignored note stops the DAC only when its own track started the DAC's sound, never another track's drum.</summary>
    [Fact]
    public void IgnoredSamplesStopTheDacOnlyForTheirOwnTrack()
    {
        Song song = Build([[
            RowOf(Play(Kick), Cell.Empty),
            RowOf(Cell.Empty, Play(Tom)),
            RowOf(Play(Hat), Cell.Empty),
            RowOf(Cell.Empty, Play(Hat)),
        ]]);

        (GenesisPlan plan, _) = Map(song, MappingOptions.Default with { Ignore = new HashSet<int> { Hat } });

        Assert.False(CellAt(plan, GenesisChannel.Fm6, row: 2).NoteOff);
        Assert.True(CellAt(plan, GenesisChannel.Fm6, row: 3).NoteOff);
    }

    /// <summary>An ignored note of a track on the PSG noise channel stops the noise.</summary>
    [Fact]
    public void IgnoredSamplesStopTheirTracksNoise()
    {
        Song song = Build([[RowOf(Cell.Empty, Play(Hat)), RowOf(Cell.Empty, Play(Kick))]]);

        (GenesisPlan plan, _) = Map(song, Options(requirePsg: [Hat]) with { Ignore = new HashSet<int> { Kick } });

        Assert.Equal(VoiceKind.PsgNoise, CellAt(plan, GenesisChannel.Psg4).Voice?.Kind);
        Assert.True(CellAt(plan, GenesisChannel.Psg4, row: 1).NoteOff);
    }
}
