using Mod2Xgm.Analysis;
using Mod2Xgm.Cli;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;
using Mod2Xgm.Optimizer;

namespace Mod2Xgm.Tests.Mapping;

/// <summary>
/// Tests for <see cref="SimpleMapper"/> (plan T5.3): where each kind of sample is played, how shared channels are
/// settled, what <c>--require-fm</c>, <c>--require-dac</c>, <c>--require-psg</c> and <c>--no-dac</c> change, and the conversion notes.
/// </summary>
public sealed class SimpleMapperTests
{
    /// <summary>
    /// Instrument numbers in <see cref="Build"/>: a looped sine, two tonal drums, a noise hit, an empty slot, a hit of
    /// noise with a short looped tail, a looped hiss, a decaying tone with a short looped tail, a looped tone whose
    /// second harmonic is louder than its fundamental, a looped sawtooth, a looped six-note chord (harmonics 4, 5, 6, 8, 10
    /// and 12 of its cycle), a looped chorused chord with no cycle at all and a looped A major triad whose common cycle
    /// is too long to search.
    /// </summary>
    private const int Organ = 0;

    private const int Kick = 1;
    private const int Tom = 2;
    private const int Hat = 3;
    private const int Empty = 4;
    private const int Bells = 5;
    private const int Hiss = 6;
    private const int Pluck = 7;
    private const int Octave = 8;
    private const int Saw = 9;
    private const int Chord = 10;
    private const int Pad = 11;
    private const int Triad = 12;

    /// <summary>
    /// A note is placed at the pitch the tick player plays it at (ADR 0061: the instrument's finetune is in it), on every
    /// order entry of its own; a note the playback doesn't have keeps its written pitch.
    /// </summary>
    [Fact]
    public void Map_WithPlaybackPitches_TakesTickPlayersPitch()
    {
        // Arrange
        NotePitch tuned = new(47, 0.2, 7821.2);
        Playback playback = new([], null, PlaybackEnd.Stops)
        {
            NotePitches = new Dictionary<NotePosition, NotePitch> { [new NotePosition(1, 0, 3)] = tuned, [new NotePosition(0, 0, 1)] = tuned },
        };

        // Act
        (GenesisPlan plan, _) = Map(Build([[RowOf(Cell.Empty, Play(Organ), Cell.Empty, Play(Kick))], [RowOf(Cell.Empty, Cell.Empty, Cell.Empty, Play(Kick))]]), playback: playback);

        // Assert
        Assert.Equal(tuned, CellAt(plan, GenesisChannel.Fm2).Note);
        Assert.Equal(Note(), CellAt(plan, GenesisChannel.Fm6).Note);
        Assert.Equal(tuned, CellAt(plan, GenesisChannel.Fm6, order: 1).Note);
    }

    /// <summary>
    /// A note on a row the song never plays (the playback has no pitch for it) is moved by its instrument's finetune, so
    /// it agrees with the notes that play: −100 cents is a semitone down at a rate 2^(−1/12) lower.
    /// </summary>
    [Fact]
    public void Map_WithUnplayedNote_TakesInstrumentsFinetune()
    {
        // Arrange
        Song song = Build([[RowOf(Cell.Empty, Play(Organ))]]);
        song = new Song(song.Title, song.Channels, song.Orders, song.LoopOrder, song.Patterns, [song.Instruments[0] with { FineTuneCents = -100 }, .. song.Instruments.Skip(1)], song.Samples, song.Timing);

        // Act
        (GenesisPlan plan, _) = Map(song);

        // Assert
        NotePitch note = Assert.NotNull(CellAt(plan, GenesisChannel.Fm2).Note);
        Assert.Equal(47, note.Semitone);
        Assert.Equal(0, note.Cents, 1e-9);
        Assert.Equal(8287 * Math.Pow(2, -1 / 12.0), note.PlaybackRateHz, 1e-9);
    }

    /// <summary>The plan's columns follow DMF channel order, which the mapper relies on when it turns a MOD channel number into a column.</summary>
    [Fact]
    public void GenesisChannel_ForEveryValue_FollowsDmfOrder()
    {
        // Act & Assert
        Assert.Equal(
            [
                GenesisChannel.Fm1, GenesisChannel.Fm2, GenesisChannel.Fm3, GenesisChannel.Fm4, GenesisChannel.Fm5, GenesisChannel.Fm6,
                GenesisChannel.Psg1, GenesisChannel.Psg2, GenesisChannel.Psg3, GenesisChannel.Psg4,
                GenesisChannel.Pcm2, GenesisChannel.Pcm3, GenesisChannel.Pcm4,
            ],
            Enum.GetValues<GenesisChannel>());
    }

    /// <summary>A sample that loops is a sustained tone: it goes to the MOD channel's own FM channel with a library patch.</summary>
    [Fact]
    public void Map_WithLoopedSample_PlaysOnOwnFmChannel()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Cell.Empty, Play(Organ, 57))]]));

        // Assert
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
    public void Map_WithNotesAndEffects_NamesSourceChannelAndSample()
    {
        // Act
        (GenesisPlan plan, _) = Map(Build([[
            RowOf(Cell.Empty, Play(Organ), Cell.Empty, Play(Kick)),
            RowOf(Cell.Empty, new Cell(null, null, 0.25, []), Cell.Empty, new Cell(null, null, null, [new Retrigger(3)])),
        ]]));

        // Assert
        Assert.Equal((1, Organ), (CellAt(plan, GenesisChannel.Fm2).Source, CellAt(plan, GenesisChannel.Fm2).Sample));
        Assert.Equal((3, Kick), (CellAt(plan, GenesisChannel.Fm6).Source, CellAt(plan, GenesisChannel.Fm6).Sample));
        Assert.Equal(1, CellAt(plan, GenesisChannel.Fm2, row: 1).Source);
        Assert.Null(CellAt(plan, GenesisChannel.Fm2, row: 1).Sample);
        Assert.Equal(3, CellAt(plan, GenesisChannel.Fm6, row: 1).Source);
        Assert.Null(CellAt(plan, GenesisChannel.Fm1).Source);
    }

    /// <summary>
    /// The notes explaining why a sample is played by FM, and at what pitch, are said once, when it is first played, and
    /// not for unplayed samples.
    /// </summary>
    [Fact]
    public void Map_WithRepeatedInstrument_NotesRoutingOnce()
    {
        // Act
        (_, List<string> notes) = Map(Build([[RowOf(Play(Organ)), RowOf(Play(Organ, 50)), RowOf(Play(Organ, 52))]]));

        // Assert
        Assert.Equal(3, notes.Count);
        Assert.Contains("it loops, so it is a sustained tone", notes[0], StringComparison.Ordinal);
        Assert.Contains("so it is played as a plain sine", notes[1], StringComparison.Ordinal);
        Assert.Contains("so its FM notes play", notes[2], StringComparison.Ordinal);
    }

    /// <summary>
    /// A looped tone whose second harmonic is louder than its fundamental is played by a patch of its own harmonics, so it
    /// keeps sounding at the octave heard, with a note saying so (docs/adr/0118-harmonic-patches.md).
    /// </summary>
    [Fact]
    public void Map_WithLoudSecondHarmonic_PlaysHarmonicPatch()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Play(Octave))]]));

        // Assert
        Assert.Equal("Harmonic 1+2", Assert.Single(plan.FmPatches).Name);
        Assert.Contains("played by the Harmonic 1+2 FM patch", notes[0], StringComparison.Ordinal);
        Assert.Contains("harmonics 1, 2 hold 100 % of its tone", notes[1], StringComparison.Ordinal);
    }

    /// <summary>A looped tone that four harmonics don't hold well keeps the patch chosen by brightness, with a note saying why.</summary>
    [Fact]
    public void Map_WithSawtooth_ChoosesPatchByBrightness()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Play(Saw))]]));

        // Assert
        Assert.Contains(Assert.Single(plan.FmPatches), FmPatchLibrary.All);
        Assert.Contains("its four loudest harmonics (1, 2, 3, 4) hold only", notes[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// A chord whose loop repeats at its common cycle, two octaves below its bottom note, and that four carriers can't hold
    /// plays a library patch lifted two octaves to that bottom note, with a note saying so (docs/adr/0125-chord-samples.md).
    /// </summary>
    [Fact]
    public void Map_WithChordLoop_LiftsLibraryPatchToBottomNote()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Play(Chord))]]));

        // Assert
        Assert.Contains(Assert.Single(plan.FmPatches), FmPatchLibrary.All);
        Assert.Equal(2400, CellAt(plan, GenesisChannel.Fm1).Cents, 3);
        Assert.Contains(notes, n => n.Contains("a cycle below every note in it (a chord), so its FM notes play its lowest strong harmonic, 4", StringComparison.Ordinal));
    }

    /// <summary>
    /// A chord sample's notes (harmonics 4, 5, 6 and 8 of its cycle) each play on an FM channel of their own: its own
    /// channel the lowest, FM5 (no track's) next, then the idle tracks' channels, all following its MOD channel, their
    /// levels together as loud as one voice (docs/adr/0133-simple-fm-chords.md).
    /// </summary>
    [Fact]
    public void Map_WithChordLoop_PlaysEachNoteOnAFreeFmChannel()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Play(Chord))]]));

        // Assert
        GenesisChannel[] columns = [GenesisChannel.Fm1, GenesisChannel.Fm5, GenesisChannel.Fm2, GenesisChannel.Fm3];
        PlanCell[] cells = [.. columns.Select(c => CellAt(plan, c))];
        Assert.Equal([2400, 1200 * Math.Log2(5), 1200 * Math.Log2(6), 3600], cells.Select(c => Math.Round(c.Cents, 6)), (a, b) => Math.Abs(a - b) < 1e-3);
        Assert.All(cells, c => Assert.Equal((0, VoiceKind.Fm), (c.Source ?? -1, c.Voice?.Kind ?? VoiceKind.PsgTone)));
        Assert.Equal(1, cells.Sum(c => c.Level * c.Level), 6);
        Assert.Null(CellAt(plan, GenesisChannel.Fm4).Note);
        Assert.Contains(notes, n => n.Contains("a chord (harmonics 4, 5, 6, 8 of its cycle), so each of its 1 FM notes plays the chord's notes on FM channels of their own; 0 found fewer", StringComparison.Ordinal));
    }

    /// <summary>With the other tracks playing on their own FM channels, only FM5 is free: the chord keeps its two lowest notes and leaves out the top ones.</summary>
    [Fact]
    public void Map_WithOtherTracksSounding_LeavesOutTheChordsTopNotes()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Play(Chord), Play(Organ), Play(Organ), Play(Organ))]]));

        // Assert
        Assert.Equal(1200 * Math.Log2(5), CellAt(plan, GenesisChannel.Fm5).Cents, 3);
        Assert.Equal(1, CellAt(plan, GenesisChannel.Fm2).Source);
        Assert.Contains(notes, n => n.Contains("so each of its 1 FM notes plays the chord's notes on FM channels of their own; 1 found fewer free FM channels than notes", StringComparison.Ordinal));
    }

    /// <summary>A plain note on the chord's MOD channel lets go of the chord's other voices.</summary>
    [Fact]
    public void Map_WithPlainNoteAfterChord_LetsGoOfTheChordVoices()
    {
        // Act
        (GenesisPlan plan, _) = Map(Build([[RowOf(Play(Chord)), RowOf(Play(Organ))]]));

        // Assert
        PlanCell[] released = [.. new[] { GenesisChannel.Fm5, GenesisChannel.Fm2, GenesisChannel.Fm3 }.Select(c => CellAt(plan, c, row: 1))];
        Assert.All(released, c => Assert.Equal((true, 0), (c.NoteOff, c.Source ?? -1)));
        Assert.Equal(0, CellAt(plan, GenesisChannel.Fm1, row: 1).Cents);
    }

    /// <summary>A track's own note takes its FM channel back from a chord voice there, with a note counting the voices cut.</summary>
    [Fact]
    public void Map_WithTrackNoteOnChordChannel_TakesTheChannelBack()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Play(Chord)), RowOf(Cell.Empty, Play(Organ))]]));

        // Assert
        Assert.Equal(1, CellAt(plan, GenesisChannel.Fm2, row: 1).Source);
        Assert.Equal(0, CellAt(plan, GenesisChannel.Fm2, row: 1).Cents);
        Assert.Contains(notes, n => n.Contains("1 chord voices stopped early, where the track that owns their FM channel played a note", StringComparison.Ordinal));
    }

    /// <summary>A volume change on the chord's MOD channel reaches every voice of the chord.</summary>
    [Fact]
    public void Map_WithVolumeChangeDuringChord_SetsItOnEveryVoice()
    {
        // Act
        (GenesisPlan plan, _) = Map(Build([[RowOf(Play(Chord)), RowOf(new Cell(null, null, 0.25, []))]]));

        // Assert
        PlanCell[] voices = [.. new[] { GenesisChannel.Fm1, GenesisChannel.Fm5, GenesisChannel.Fm2, GenesisChannel.Fm3 }.Select(c => CellAt(plan, c, row: 1))];
        Assert.All(voices, c => Assert.Equal((0.25, 0), (c.Volume ?? -1, c.Source ?? -1)));
    }

    /// <summary>
    /// A chorused chord with no cycle at all plays its bottom note, unlifted, with a note saying so
    /// (docs/adr/0125-chord-samples.md).
    /// </summary>
    [Fact]
    public void Map_WithChorusedChordLoop_PlaysBottomNote()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Play(Pad))]]));

        // Assert
        Assert.Equal(0, CellAt(plan, GenesisChannel.Fm1).Cents);
        Assert.Contains(notes, n => n.Contains("no one cycle repeats in its loop (a chord, or a chorused tone), so its FM notes play its lowest strong note", StringComparison.Ordinal));
    }


    /// <summary>
    /// A loop with no cycle short enough to find, whose partials make an A major triad, plays each of its notes on an FM
    /// channel of its own at its interval above the bottom note, with a note naming the notes read from the loop
    /// (docs/adr/0135-loop-chords.md).
    /// </summary>
    [Fact]
    public void Map_WithTriadLoop_PlaysEachNoteOnAFreeFmChannel()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Play(Triad))]]));

        // Assert
        PlanCell[] cells = [.. new[] { GenesisChannel.Fm1, GenesisChannel.Fm5, GenesisChannel.Fm2 }.Select(c => CellAt(plan, c))];
        Assert.Equal([0, 1200 * Math.Log2(69 / 55.0), 1200 * Math.Log2(82 / 55.0)], cells.Select(c => c.Cents), (a, b) => Math.Abs(a - b) < 5);
        Assert.Equal(1, cells.Sum(c => c.Level * c.Level), 6);
        Assert.Null(CellAt(plan, GenesisChannel.Fm3).Note);
        Assert.Contains(notes, n => n.Contains("a chord (A0, C#1, E1 at C-2, read from its loop), so each of its 1 FM notes plays the chord's notes", StringComparison.Ordinal));
    }
    /// <summary>
    /// A sample whose loop is a short tail after a long recording with no clear pitch is played like one that plays once:
    /// on the DAC, with a note saying what the mapper took it to be before the one saying where it plays
    /// (docs/adr/0117-tail-and-noise-loops.md).
    /// </summary>
    [Fact]
    public void Map_WithTailLoop_PlaysOnDacAndSaysWhy()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Play(Bells))]]));

        // Assert
        Assert.Equal(new PlanVoice(VoiceKind.Dac, 0), CellAt(plan, GenesisChannel.Fm6).Voice);
        Assert.Empty(plan.FmPatches);
        Assert.Equal(2, notes.Count);
        Assert.Contains("its 600-frame loop is a short tail after 7400 frames of recording with no clear pitch", notes[0], StringComparison.Ordinal);
        Assert.Contains("it plays once, so it is played as a recording on the DAC", notes[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// A short tail after a recording with a clear pitch stays a sustained tone, played by FM at the recording's pitch,
    /// not the loop's (docs/adr/0117-tail-and-noise-loops.md).
    /// </summary>
    [Fact]
    public void Map_WithTailAfterPitchedRecording_PlaysFmAtRecordingsPitch()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Play(Pluck))]]));

        // Assert
        Assert.Equal(VoiceKind.Fm, CellAt(plan, GenesisChannel.Fm1).Voice?.Kind);
        Assert.Contains("with a clear pitch, so it is a sustained tone", notes[0], StringComparison.Ordinal);
        Assert.Contains("its tone repeats every 74.99 frames", notes[1], StringComparison.Ordinal);
    }

    /// <summary>A sample whose loop is noise is played like one that plays once: on the DAC, with a note saying so.</summary>
    [Fact]
    public void Map_WithNoiseLoop_PlaysOnDacAndSaysWhy()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Play(Hiss))]]));

        // Assert
        Assert.Equal(new PlanVoice(VoiceKind.Dac, 0), CellAt(plan, GenesisChannel.Fm6).Voice);
        Assert.Contains("its 2000-frame loop is noise, not a repeating tone", notes[0], StringComparison.Ordinal);
    }

    /// <summary>A sample with no tone that FM plays (--require-fm) gets a note that its notes are placed by the note's name.</summary>
    [Fact]
    public void Map_WithRequiredFmNoise_SaysNoPitchFound()
    {
        // Act
        (_, List<string> notes) = Map(Build([[RowOf(Play(Hat))]]), Options(requireFm: [Hat]));

        // Assert
        Assert.Contains(notes, n => n.Contains("no repeating tone found in it, so its FM notes play two octaves above", StringComparison.Ordinal));
    }

    /// <summary>A sample that plays once is a recording: it goes to the DAC (FM6), whichever MOD channel plays it.</summary>
    [Fact]
    public void Map_WithOneShotSample_PlaysOnDac()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Cell.Empty, Cell.Empty, Play(Kick))]]));

        // Assert
        PlanCell cell = CellAt(plan, GenesisChannel.Fm6);
        Assert.Equal(new PlanVoice(VoiceKind.Dac, 0), cell.Voice);
        DacSample dac = Assert.Single(plan.DacSamples);
        Assert.Equal((Kick, 8287.0), (dac.Sample, dac.PlaybackRateHz));
        Assert.Empty(plan.FmPatches);
        Assert.Contains("it plays once, so it is played as a recording on the DAC", Assert.Single(notes), StringComparison.Ordinal);
        Assert.Equal(PlanCell.Empty, CellAt(plan, GenesisChannel.Fm3));
    }

    /// <summary>DAC samples are numbered by first use, and each plays at the rate of the pitch used most often.</summary>
    [Fact]
    public void Map_WithDacSample_PlaysAtMostUsedPitch()
    {
        // Arrange
        Cell again = new(new NotePitch(48, 0, 9000), null, null, []);
        Row[] rows = [RowOf(Play(Kick, 36, rate: 4000), Play(Tom, 40, rate: 5000)), RowOf(again), RowOf(again)];

        // Act
        (GenesisPlan plan, _) = Map(Build([rows]));

        // Assert
        // Row 0: kick and tom collide on the DAC (same volume, so channel 1 wins); the tom goes to FM.
        // The kick is then played twice at semitone 48 (rate 9000) and once at 36 (rate 4000): 9000 wins.
        Assert.Equal(new PlanVoice(VoiceKind.Dac, 0), CellAt(plan, GenesisChannel.Fm6).Voice);
        DacSample dac = Assert.Single(plan.DacSamples);
        Assert.Equal((Kick, 9000.0), (dac.Sample, dac.PlaybackRateHz));
    }

    /// <summary>Instrument numbers on the DAC are assigned in order of first use across the whole song.</summary>
    [Fact]
    public void Map_WithDacSamples_NumbersByFirstUse()
    {
        // Act
        (GenesisPlan plan, _) = Map(Build([[RowOf(Play(Tom)), RowOf(Play(Kick)), RowOf(Play(Tom))]]));

        // Assert
        Assert.Equal([Tom, Kick], plan.DacSamples.Select(d => d.Sample));
        Assert.Equal(1, CellAt(plan, GenesisChannel.Fm6, row: 1).Voice?.Index);
        Assert.Equal(0, CellAt(plan, GenesisChannel.Fm6, row: 2).Voice?.Index);
    }

    /// <summary>The louder of two drum notes on the same row keeps the DAC; a tie goes to the lower channel.</summary>
    [Theory]
    [InlineData(0.9, 0.3, 0)]
    [InlineData(0.3, 0.9, 1)]
    [InlineData(0.5, 0.5, 0)]
    public void Map_WithTwoDrumsOnRow_LetsLouderKeepDac(double first, double second, int winnerChannel)
    {
        // Act
        (GenesisPlan plan, _) = Map(Build([[RowOf(Play(Kick, volume: first), Play(Tom, 40, volume: second))]]));

        // Assert
        int winnerSample = winnerChannel == 0 ? Kick : Tom;
        Assert.Equal(winnerSample, plan.DacSamples[CellAt(plan, GenesisChannel.Fm6).Voice!.Value.Index].Sample);
    }

    /// <summary>A drum that loses every clash for the DAC still counts as a recording, so the capacity check sees what it wanted.</summary>
    [Fact]
    public void Map_WithDrumThatAlwaysLosesDac_KeepsItAmongPcmSamples()
    {
        // Act
        (GenesisPlan plan, _) = Map(Build([[RowOf(Play(Kick, volume: 0.9), Play(Tom, 40, volume: 0.3))]]));

        // Assert
        Assert.Equal(Kick, Assert.Single(plan.DacSamples).Sample);
        Assert.Contains(Tom, plan.PcmSamples);
    }

    /// <summary>A tonal drum that loses the DAC plays on its own FM channel instead, with a note saying so.</summary>
    [Fact]
    public void Map_WithTonalDrumLosingDac_MovesItToFm()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Play(Kick, volume: 0.9), Play(Tom, 40, volume: 0.3))]]));

        // Assert
        PlanCell moved = CellAt(plan, GenesisChannel.Fm2);
        Assert.Equal(VoiceKind.Fm, moved.Voice?.Kind);
        Assert.Equal(FmPatchLibrary.Pluck, plan.FmPatches[moved.Voice!.Value.Index]);
        Assert.Contains(notes, n => n.StartsWith("1 drum notes started on the same row", StringComparison.Ordinal) && n.EndsWith("played by FM instead", StringComparison.Ordinal));
    }

    /// <summary>A noise drum that loses the DAC plays on the PSG noise channel.</summary>
    [Fact]
    public void Map_WithNoiseDrumLosingDac_MovesItToPsgNoise()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Play(Kick, volume: 0.9), Play(Hat, volume: 0.3))]]));

        // Assert
        Assert.Equal(new PlanVoice(VoiceKind.PsgNoise, 0), CellAt(plan, GenesisChannel.Psg4).Voice);
        Assert.Equal(PlanCell.Empty, CellAt(plan, GenesisChannel.Fm2));
        Assert.Contains(notes, n => n.Contains("played by the PSG noise channel instead", StringComparison.Ordinal));
    }

    /// <summary>
    /// With <see cref="DacLoser.Fm"/>, a drum on the right that loses the DAC plays FM on its own channel, which keeps its
    /// side, instead of the PSG noise, which would jump it to the middle (docs/adr/0109-pan-swap-cost.md).
    /// </summary>
    [Fact]
    public void Map_WithLoserToFmAndDrumOnASide_PlaysItFmOnItsOwnChannel()
    {
        // Act
        (GenesisPlan plan, _) = Map(Panned(Build([[RowOf(Play(Kick, volume: 0.9), Play(Hat, volume: 0.3))]]), 0, 1, 0, 0), loser: DacLoser.Fm);

        // Assert
        Assert.Equal(VoiceKind.Fm, CellAt(plan, GenesisChannel.Fm2).Voice?.Kind);
        Assert.Equal(PlanCell.Empty, CellAt(plan, GenesisChannel.Psg4));
    }

    /// <summary>With <see cref="DacLoser.Fm"/>, a centred drum that loses the DAC still takes the PSG noise: it can't jump.</summary>
    [Fact]
    public void Map_WithLoserToFmAndCentredDrum_StillTakesTheNoise()
    {
        // Act
        (GenesisPlan plan, _) = Map(Build([[RowOf(Play(Kick, volume: 0.9), Play(Hat, volume: 0.3))]]), loser: DacLoser.Fm);

        // Assert
        Assert.Equal(new PlanVoice(VoiceKind.PsgNoise, 0), CellAt(plan, GenesisChannel.Psg4).Voice);
    }

    /// <summary>
    /// With <see cref="DacLoser.NoiseAllSong"/>, a drum on the right that loses the DAC once plays the PSG noise everywhere,
    /// even where it would have the DAC alone, so it stays steadily in the middle.
    /// </summary>
    [Fact]
    public void Map_WithNoiseAllSong_PlaysTheSideLoserOnNoiseEverywhere()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(
            Panned(Build([[RowOf(Play(Kick, volume: 0.9), Play(Hat, volume: 0.3))], [RowOf(Cell.Empty, Play(Hat, volume: 0.3))]]), 0, 1, 0, 0),
            loser: DacLoser.NoiseAllSong);

        // Assert
        Assert.Equal(new PlanVoice(VoiceKind.PsgNoise, 0), CellAt(plan, GenesisChannel.Psg4, order: 1).Voice);
        Assert.Equal(PlanCell.Empty, CellAt(plan, GenesisChannel.Fm6, order: 1));
        Assert.Contains(notes, n => n.Contains("PSG noise channel all song", StringComparison.Ordinal));
    }

    /// <summary>A <c>--require-dac</c> note that loses the DAC is dropped, never moved to FM or PSG.</summary>
    [Fact]
    public void Map_WithRequiredDacNoteLosingDac_DropsIt()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(
            Build([[RowOf(Play(Kick, volume: 0.9), Play(Tom, 40, volume: 0.3))]]),
            Options(requireDac: [Tom]));

        // Assert
        Assert.Equal(PlanCell.Empty, CellAt(plan, GenesisChannel.Fm2));
        Assert.Empty(plan.FmPatches);
        Assert.Contains(notes, n => n.StartsWith("1 --require-dac notes", StringComparison.Ordinal));
    }

    /// <summary><c>--no-dac</c>: a tonal one-shot is an FM Pluck, a noise one-shot is the PSG noise channel.</summary>
    [Fact]
    public void Map_WithoutDac_SendsOneShotsToFmAndNoise()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(
            Build([[RowOf(Play(Kick), Play(Hat))]]),
            Options(useDac: false));

        // Assert
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
    public void Map_WithNoiseNoteLosingNoiseChannel_MovesItToFm()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(
            Build([[RowOf(Play(Hat, volume: 0.2), Play(Hat, volume: 0.8))]]),
            Options(useDac: false));

        // Assert
        Assert.Equal(VoiceKind.PsgNoise, CellAt(plan, GenesisChannel.Psg4).Voice?.Kind);
        Assert.Equal(VoiceKind.Fm, CellAt(plan, GenesisChannel.Fm1).Voice?.Kind);
        Assert.Equal(PlanCell.Empty, CellAt(plan, GenesisChannel.Fm2));
        Assert.Contains(notes, n => n.StartsWith("1 noise notes started on the same row", StringComparison.Ordinal));
    }

    /// <summary><c>--require-fm</c> sends a one-shot to FM even though the DAC is free.</summary>
    [Fact]
    public void Map_WithRequiredFm_BeatsDac()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Play(Kick))]]), Options(requireFm: [Kick]));

        // Assert
        Assert.Equal(VoiceKind.Fm, CellAt(plan, GenesisChannel.Fm1).Voice?.Kind);
        Assert.Empty(plan.DacSamples);
        Assert.Contains(notes, n => n.Contains("required as FM (--require-fm)", StringComparison.Ordinal));
    }

    /// <summary><c>--require-dac</c> sends even a looped sample to the DAC.</summary>
    [Fact]
    public void Map_WithRequiredDac_BeatsLoopRule()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(Play(Organ))]]), Options(requireDac: [Organ]));

        // Assert
        Assert.Equal(VoiceKind.Dac, CellAt(plan, GenesisChannel.Fm6).Voice?.Kind);
        Assert.Empty(plan.FmPatches);
        Assert.Empty(notes);
    }

    /// <summary><c>--require-psg</c> sends a tonal one-shot and a looped sample to the PSG noise channel, with the DAC and FM both free.</summary>
    [Fact]
    public void Map_WithRequiredPsg_BeatsDacAndLoopRule()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(
            Build([[RowOf(Play(Kick), Play(Organ))]]),
            Options(requirePsg: [Kick, Organ]));

        // Assert
        Assert.Equal(VoiceKind.PsgNoise, CellAt(plan, GenesisChannel.Psg4).Voice?.Kind);
        Assert.Equal(PlanCell.Empty, CellAt(plan, GenesisChannel.Fm6));
        Assert.Empty(plan.DacSamples);
        Assert.Empty(plan.FmPatches);
        Assert.Contains(notes, n => n.Contains("required as PSG (--require-psg)", StringComparison.Ordinal));
    }

    /// <summary>A <c>--require-psg</c> note that loses the noise channel is dropped, never moved to FM; an ordinary noise note would move.</summary>
    [Fact]
    public void Map_WithRequiredPsgNoteLosingNoiseChannel_DropsIt()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(
            Build([[RowOf(Play(Hat, volume: 0.9), Play(Kick, volume: 0.3))]]),
            Options(requirePsg: [Kick], useDac: false));

        // Assert
        Assert.Equal(PlanCell.Empty, CellAt(plan, GenesisChannel.Fm2));
        Assert.Empty(plan.FmPatches);
        Assert.Contains(notes, n => n.StartsWith("1 --require-psg notes", StringComparison.Ordinal));
    }

    /// <summary><c>--no-dac</c> and <c>--require-dac</c> contradict each other.</summary>
    [Fact]
    public void Map_WithNoDacAndRequiredDac_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => Map(Build([[RowOf()]]), Options(requireDac: [Kick], useDac: false)));
    }

    /// <summary>A required sample that is empty or never played gets a note saying the requirement has no effect.</summary>
    [Fact]
    public void Map_WithRequirementsOnUnplayedSamples_Notes()
    {
        // Act
        (_, List<string> notes) = Map(Build([[RowOf(Play(Organ))]]), Options(requireFm: [Empty], requireDac: [Tom], requirePsg: [Hat]));

        // Assert
        Assert.Contains(notes, n => n.Contains("Sample 5 \"empty\": required as FM, but it is empty or never played", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Contains("Sample 3 \"tom\": required as DAC", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Contains("Sample 4 \"hat\": required as PSG, but it is empty or never played", StringComparison.Ordinal));
    }

    /// <summary>When a MOD channel moves from an FM note to a drum, its FM channel is told to stop.</summary>
    [Fact]
    public void Map_WhenChannelLeavesFm_StopsFmChannel()
    {
        // Act
        (GenesisPlan plan, _) = Map(Build([[RowOf(Play(Organ)), RowOf(Play(Kick)), RowOf(NoteOnly())]]));

        // Assert
        Assert.True(CellAt(plan, GenesisChannel.Fm1, row: 1).NoteOff);
        Assert.Equal(VoiceKind.Dac, CellAt(plan, GenesisChannel.Fm6, row: 1).Voice?.Kind);
        Assert.False(CellAt(plan, GenesisChannel.Fm1, row: 2).NoteOff);
    }

    /// <summary>A volume change is passed on to the FM channel only while the MOD channel sounds on FM.</summary>
    [Fact]
    public void Map_WithVolumeChange_FollowsChannel()
    {
        // Arrange
        Cell volumeOnly = new(null, null, 0.25, []);

        // Act
        (GenesisPlan plan, _) = Map(Build([[RowOf(Play(Organ)), RowOf(volumeOnly), RowOf(Play(Kick)), RowOf(volumeOnly)]]));

        // Assert
        Assert.Equal(0.25, CellAt(plan, GenesisChannel.Fm1, row: 1).Volume);
        Assert.Null(CellAt(plan, GenesisChannel.Fm1, row: 3).Volume);
        Assert.Null(CellAt(plan, GenesisChannel.Fm6, row: 3).Volume);
    }

    /// <summary>A note takes the volume of its cell, else the instrument's default, else what the channel carried.</summary>
    [Fact]
    public void Map_WithNote_CarriesStartingVolume()
    {
        // Arrange
        Cell set = new(null, null, 0.75, []);

        // Act
        (GenesisPlan plan, _) = Map(Build([[RowOf(Play(Organ)), RowOf(set), RowOf(NoteOnly(50)), RowOf(Play(Organ, 52, volume: 0.1))]]));

        // Assert
        Assert.Equal(0.5, CellAt(plan, GenesisChannel.Fm1, row: 0).Volume);
        Assert.Equal(0.75, CellAt(plan, GenesisChannel.Fm1, row: 2).Volume);
        Assert.Equal(0.1, CellAt(plan, GenesisChannel.Fm1, row: 3).Volume);
    }

    /// <summary>A note with no instrument chosen yet is skipped, with a note; so is one on an empty sample slot.</summary>
    [Fact]
    public void Map_WithNotesWithoutSound_SkipsThem()
    {
        // Act
        (GenesisPlan plan, List<string> notes) = Map(Build([[RowOf(NoteOnly()), RowOf(Play(Empty))]]));

        // Assert
        Assert.All(plan.Patterns[0], row => Assert.All(row.Cells, cell => Assert.Null(cell.Note)));
        Assert.Contains("1 notes were played before their channel had an instrument; left out", notes);
        Assert.Contains("1 notes use an empty sample slot; left out", notes);
    }

    /// <summary>A song with more channels than FM1-5 can carry leaves the extra ones out, with a note.</summary>
    [Fact]
    public void Map_WithChannelBeyondFifth_LeavesItOut()
    {
        // Arrange
        Row row = new([Cell.Empty, Cell.Empty, Cell.Empty, Cell.Empty, Cell.Empty, Play(Organ)]);

        // Act
        (GenesisPlan plan, List<string> notes) = Map(Build([[row]], channels: 6));

        // Assert
        Assert.All(plan.Patterns[0][0].Cells, cell => Assert.Null(cell.Note));
        Assert.Contains("1 notes are on MOD channels above 5, which have no FM channel; left out", notes);
    }

    /// <summary>Song-wide effects go to the row's flow list; channel effects go with the channel's sound.</summary>
    [Fact]
    public void Map_WithEffects_SplitsFlowAndChannelEffects()
    {
        // Arrange
        Effect speed = new SetSpeed(3);
        Effect arpeggio = new Arpeggio(4, 7);
        Effect jump = new PositionJump(0);

        // Act
        (GenesisPlan plan, _) = Map(Build([[
            RowOf(Play(Organ, effects: [speed, arpeggio])),
            RowOf(Cell.Empty, Play(Kick, effects: [arpeggio, jump])),
            RowOf(Cell.Empty, new Cell(null, null, null, [arpeggio])),
            RowOf(new Cell(null, null, null, [arpeggio])),
        ]]));

        // Assert
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
    public void Map_WithSeveralOrders_CarriesStateAcross()
    {
        // Arrange
        Song song = Build([[RowOf(Play(Organ))], [RowOf(NoteOnly(60))]]);

        // Act
        (GenesisPlan plan, _) = Map(song);

        // Assert
        Assert.Equal(2, plan.Patterns.Count);
        Assert.Equal(60, CellAt(plan, GenesisChannel.Fm1, order: 1).Note?.Semitone);
        Assert.Equal(song.Timing, plan.Timing);
        Assert.Equal(song.LoopOrder, plan.LoopOrder);
        Assert.Equal("test", plan.Title);
        Assert.Equal(GenesisPlan.ChannelCount, plan.Patterns[0][0].Cells.Count);
    }

    /// <summary>The same song and options always give the same plan.</summary>
    [Fact]
    public void Map_WithSameSong_GivesSamePlan()
    {
        // Arrange
        Song song = Build([[RowOf(Play(Organ), Play(Kick), Play(Hat), Play(Tom))]]);
        (GenesisPlan first, List<string> firstNotes) = Map(song);

        // Act
        (GenesisPlan second, List<string> secondNotes) = Map(song);

        // Assert
        Assert.Equal(firstNotes, secondNotes);
        Assert.Equal(first.FmPatches, second.FmPatches);
        Assert.Equal(first.DacSamples, second.DacSamples);
        Assert.Equal(first.Patterns[0][0].Cells, second.Patterns[0][0].Cells);
    }

    /// <summary>The command line's 1-based numbers become the mapper's 0-based ones, and <c>--no-dac</c> turns the DAC off.</summary>
    [Fact]
    public void From_WithCommandLine_ConvertsToZeroBased()
    {
        // Arrange
        ConversionOptions cli = new()
        {
            InputPath = "x.mod",
            RequireFm = new HashSet<int> { 1, 3 },
            RequireDac = new HashSet<int> { 2 },
            RequirePsg = new HashSet<int> { 4 },
            IgnoreSamples = new HashSet<int> { 5, 7 },
            NoDac = false,
        };

        // Act
        MappingOptions options = MappingOptions.From(cli);

        // Assert
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
    public void Map_WithIgnoredSample_SilencesTrack()
    {
        // Arrange
        Song song = Build([[RowOf(Play(Organ)), RowOf(Play(Kick)), RowOf(Play(Kick)), RowOf(Play(Organ, 50))]]);

        // Act
        (GenesisPlan plan, List<string> notes) = Map(song, MappingOptions.Default with { Ignore = new HashSet<int> { Kick } });

        // Assert
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
    public void Map_WithIgnoredSample_StopsDacOnlyForOwnTrack()
    {
        // Arrange
        Song song = Build([[
            RowOf(Play(Kick), Cell.Empty),
            RowOf(Cell.Empty, Play(Tom)),
            RowOf(Play(Hat), Cell.Empty),
            RowOf(Cell.Empty, Play(Hat)),
        ]]);

        // Act
        (GenesisPlan plan, _) = Map(song, MappingOptions.Default with { Ignore = new HashSet<int> { Hat } });

        // Assert
        Assert.False(CellAt(plan, GenesisChannel.Fm6, row: 2).NoteOff);
        Assert.True(CellAt(plan, GenesisChannel.Fm6, row: 3).NoteOff);
    }

    /// <summary>An ignored note of a track on the PSG noise channel stops the noise.</summary>
    [Fact]
    public void Map_WithIgnoredSample_StopsTracksNoise()
    {
        // Arrange
        Song song = Build([[RowOf(Cell.Empty, Play(Hat)), RowOf(Cell.Empty, Play(Kick))]]);

        // Act
        (GenesisPlan plan, _) = Map(song, Options(requirePsg: [Hat]) with { Ignore = new HashSet<int> { Kick } });

        // Assert
        Assert.Equal(VoiceKind.PsgNoise, CellAt(plan, GenesisChannel.Psg4).Voice?.Kind);
        Assert.True(CellAt(plan, GenesisChannel.Psg4, row: 1).NoteOff);
    }

    /// <summary>
    /// A track that moves from a recording to an FM note stops the recording, as the MOD's next note cuts it: a looped
    /// recording would otherwise ring on for seconds (brun_jul's sleigh bells at the song's end).
    /// </summary>
    [Fact]
    public void Map_WithTrackMovingFromDacToFm_StopsRecording()
    {
        // Arrange
        Song song = Build([[RowOf(Play(Kick)), RowOf(Play(Organ))]]);

        // Act
        (GenesisPlan plan, _) = Map(song, MappingOptions.Default);

        // Assert
        Assert.Equal(VoiceKind.Dac, CellAt(plan, GenesisChannel.Fm6).Voice?.Kind);
        Assert.True(CellAt(plan, GenesisChannel.Fm6, row: 1).NoteOff);
        Assert.Equal(0, CellAt(plan, GenesisChannel.Fm6, row: 1).Source);
        Assert.Equal(VoiceKind.Fm, CellAt(plan, GenesisChannel.Fm1, row: 1).Voice?.Kind);
    }

    /// <summary>A track that moves to FM after another track's drum took the DAC leaves that drum playing.</summary>
    [Fact]
    public void Map_WithTrackMovingToFmAfterDacTakenOver_LeavesOtherDrum()
    {
        // Arrange
        Song song = Build([[
            RowOf(Play(Kick), Cell.Empty),
            RowOf(Cell.Empty, Play(Tom)),
            RowOf(Play(Organ), Cell.Empty),
        ]]);

        // Act
        (GenesisPlan plan, _) = Map(song, MappingOptions.Default);

        // Assert
        Assert.False(CellAt(plan, GenesisChannel.Fm6, row: 2).NoteOff);
    }

    /// <summary>
    /// With a fit closer than its built-in patch, a looped sample's FM notes play the fit, at the sample's own pitch, and
    /// a note says so (docs/adr/0137-simple-mapper-fitted-patches.md).
    /// </summary>
    [Fact]
    public void Map_WithCloserFit_PlaysFittedPatch()
    {
        // Arrange
        Song song = Build([[RowOf(Play(0))]]);
        FittedFm fits = FitsFor(song, 0, Fitted("Fit 1.1", -1));

        // Act
        (GenesisPlan plan, List<string> notes) = Map(song, fits: fits);

        // Assert
        PlanCell cell = CellAt(plan, GenesisChannel.Fm1);
        Assert.Equal("Fit 1.1", plan.FmPatches[cell.Voice!.Value.Index].Name);
        Assert.Equal(0, cell.Cents);
        Assert.Contains(notes, n => n.Contains("an FM patch fitted to pitch region 1, for all its notes", StringComparison.Ordinal));
    }

    /// <summary>A fit no closer than the built-in patch leaves it playing, and a note says why.</summary>
    [Fact]
    public void Map_WithFartherFit_KeepsBuiltInPatch()
    {
        // Arrange
        Song song = Build([[RowOf(Play(0))]]);
        (GenesisPlan plain, _) = Map(song);
        FittedFm fits = FitsFor(song, 0, Fitted("Fit 1.1", 1000));

        // Act
        (GenesisPlan plan, List<string> notes) = Map(song, fits: fits);

        // Assert
        Assert.Equal(plain.FmPatches.Single().Name, plan.FmPatches.Single().Name);
        Assert.Contains(notes, n => n.Contains("keeps the built-in", StringComparison.Ordinal));
    }

    /// <summary>A fit that is closest with the LFO sets the plan's LFO rate, and a note says so.</summary>
    [Fact]
    public void Map_WithLfoFit_SetsPlanLfoRate()
    {
        // Arrange
        Song song = Build([[RowOf(Play(0))]]);
        FittedFm fits = FitsFor(song, 0, Fitted("Fit 1.1", -2, lfoRate: 4));

        // Act
        (GenesisPlan plan, List<string> notes) = Map(song, fits: fits);

        // Assert
        Assert.Equal(4, plan.LfoRate);
        Assert.Contains(notes, n => n.Contains("The song's LFO runs at rate 4", StringComparison.Ordinal));
    }

    /// <summary>Without fits the plan leaves the LFO off.</summary>
    [Fact]
    public void Map_WithoutFits_LeavesLfoOff()
    {
        // Arrange
        Song song = Build([[RowOf(Play(0))]]);

        // Act
        (GenesisPlan plan, _) = Map(song);

        // Assert
        Assert.Null(plan.LfoRate);
    }

    /// <summary>A chord's notes play on several voices, so with fits it keeps its built-in patch, and a note says so.</summary>
    [Fact]
    public void Map_WithFitsForChord_KeepsBuiltInPatch()
    {
        // Arrange
        Song song = Build([[RowOf(Play(10))]]);
        FittedFm fits = FitsFor(song, 10, Fitted("Fit 11.1", -1));

        // Act
        (GenesisPlan plan, List<string> notes) = Map(song, fits: fits);

        // Assert
        Assert.DoesNotContain(plan.FmPatches, p => p.Name == "Fit 11.1");
        Assert.Contains(notes, n => n.Contains("keeps its built-in patch instead of a fitted one", StringComparison.Ordinal));
    }

    /// <summary>The samples worth fitting are the looped ones played on FM with one voice: not drums, noise or chords.</summary>
    [Fact]
    public void FmSamples_ForSong_ListsSingleVoiceFmSamples()
    {
        // Arrange
        Song song = Build([[RowOf(Play(0))]]);

        // Act
        IReadOnlySet<int> fm = SimpleMapper.FmSamples(song, MappingOptions.Default);

        // Assert
        Assert.Contains(0, fm);
        Assert.DoesNotContain(1, fm);
        Assert.DoesNotContain(6, fm);
        Assert.DoesNotContain(10, fm);
    }

    /// <summary><paramref name="song"/>'s sample <paramref name="sample"/> analyzed at C-2, with <paramref name="fit"/> for its one region.</summary>
    private static FittedFm FitsFor(Song song, int sample, FmFit fit) =>
        new([SampleAnalyzer.Analyze(sample, song.Samples[sample], [8287])], [new FmSampleFit(sample, song.Samples[sample].Name, [new FmRegionFit(0, fit, fit, null, fit, fit with { LfoRate = null, Patch = fit.Patch with { Fms = 0 }, Score = fit.Score with { MelDb = Math.Max(fit.Score.MelDb, -1) } }, 0, TimeSpan.Zero)])]);

    /// <summary>A made-up fit called <paramref name="name"/> at <paramref name="melDb"/> (below 0 beats any real patch); with <paramref name="lfoRate"/>, it uses the LFO.</summary>
    private static FmFit Fitted(string name, double melDb, int? lfoRate = null) =>
        new(FmPatchLibrary.Sine with { Name = name, Fms = lfoRate is null ? 0 : 3 }, lfoRate, FmFitSource.Exhaustive, new NoteScore(melDb, 0, null));

    /// <summary>A pitch at <paramref name="semitone"/>, played at <paramref name="rate"/>.</summary>
    private static NotePitch Note(int semitone = 48, double rate = 8287) => new(semitone, 0, rate);

    /// <summary>A note of <paramref name="instrument"/>.</summary>
    private static Cell Play(int instrument, int semitone = 48, double? volume = null, double rate = 8287, params Effect[] effects) =>
        new(Note(semitone, rate), instrument, volume, effects);

    /// <summary>A note that names no instrument.</summary>
    private static Cell NoteOnly(int semitone = 48) => new(Note(semitone), null, null, []);

    /// <summary>A row of the given cells, then empty ones up to 4 channels.</summary>
    private static Row RowOf(params Cell[] cells) => new([.. cells, .. Enumerable.Repeat(Cell.Empty, 4 - cells.Length)]);

    /// <summary>A song with the five instruments above, 4 channels, one order per given pattern.</summary>
    private static Song Build(Row[][] patterns, int channels = 4)
    {
        float[] tone = [.. Enumerable.Range(0, 1024).Select(i => (float)Math.Sin(2 * Math.PI * i / 64))];
        float[] thump = [.. Enumerable.Range(0, 2000).Select(i => (float)(Math.Sin(2 * Math.PI * i / 128) * Math.Exp(-i / 500.0)))];
        float[] white = [.. new Random(7).GetItems([-1f, 1f], 2000)];
        float[] bells = [.. new Random(11).GetItems([-1f, 1f], 7400), .. Enumerable.Range(0, 600).Select(i => (float)Math.Sin(2 * Math.PI * i / 60))];
        float[] pluck = [.. Enumerable.Range(0, 8000).Select(i => (float)(Math.Sin(2 * Math.PI * i / 75) * Math.Exp(-i / 3000.0)))];
        float[] octave = [.. Enumerable.Range(0, 128).Select(i => (float)((0.3 * Math.Sin(2 * Math.PI * i / 128)) + Math.Sin(2 * Math.PI * i / 64)))];
        float[] saw = [.. Enumerable.Range(0, 64).Select(i => (float)((i / 32.0) - 1))];
        float[] chord = [.. Enumerable.Range(0, 128).Select(i => (float)new[] { 4, 5, 6, 8, 10, 12 }.Sum(k => 0.15 * Math.Sin(2 * Math.PI * k * i / 128)))];
        float[] pad = [.. Enumerable.Range(0, 16384).Select(i => (float)new[] { 55, 56, 69, 70, 82, 83 }.Sum(k => 0.15 * Math.Sin(2 * Math.PI * k * i / 16384)))];
        float[] triad = [.. Enumerable.Range(0, 16384).Select(i => (float)new[] { 55, 69, 82 }.Sum(r => (0.2 * Math.Sin(2 * Math.PI * r * i / 16384)) + (0.1 * Math.Sin(4 * Math.PI * r * i / 16384)) + (0.07 * Math.Sin(6 * Math.PI * r * i / 16384))))];
        Sample[] samples =
        [
            new("organ", tone, new SampleLoop(0, 1024)),
            new("kick", thump, null),
            new("tom", thump, null),
            new("hat", white, null),
            new("empty", [], null),
            new("bells", bells, new SampleLoop(7400, 600)),
            new("hiss", white, new SampleLoop(0, 2000)),
            new("pluck", pluck, new SampleLoop(7400, 600)),
            new("octave", octave, new SampleLoop(0, 128)),
            new("saw", saw, new SampleLoop(0, 64)),
            new("chord", chord, new SampleLoop(0, 128)),
            new("pad", pad, new SampleLoop(0, 16384)),
            new("triad", triad, new SampleLoop(0, 16384)),
        ];
        Instrument[] instruments = [.. samples.Select((_, i) => new Instrument(i, 0.5, 0))];
        Pattern[] built = [.. patterns.Select(rows => new Pattern([.. rows.Select(r => r.Cells.Count == channels ? r : new Row([.. r.Cells.Take(channels), .. Enumerable.Repeat(Cell.Empty, channels - r.Cells.Count)]))]))];
        return new Song("test", [.. Enumerable.Repeat(new Channel(0), channels)], [.. Enumerable.Range(0, built.Length)], null, built, instruments, samples, new Timing(6, 125));
    }

    /// <summary><paramref name="song"/> with its channels panned −1 (left) to +1 (right), one value per channel.</summary>
    private static Song Panned(Song song, params double[] pans) =>
        new(song.Title, [.. pans.Select(p => new Channel(p))], song.Orders, song.LoopOrder, song.Patterns, song.Instruments, song.Samples, song.Timing);

    /// <summary>Maps <paramref name="song"/>, with <paramref name="fits"/> when given; without <paramref name="playback"/> (no tick player for a song built in code) every note is treated as never played.</summary>
    private static (GenesisPlan Plan, List<string> Notes) Map(Song song, MappingOptions? options = null, Playback? playback = null, DacLoser loser = DacLoser.Default, FittedFm? fits = null)
    {
        CollectingSink sink = new();
        ConversionNotes notes = new(sink.CreateLogger());
        IChannelMapper sut = new SimpleMapper(loser, fits);
        GenesisPlan plan = sut.Map(song, playback ?? new Playback([], null, PlaybackEnd.Stops), options ?? MappingOptions.Default, notes);
        return (plan, [.. notes.Messages]);
    }

    /// <summary>The plan's cell on <paramref name="channel"/> at an order and row.</summary>
    private static PlanCell CellAt(GenesisPlan plan, GenesisChannel channel, int order = 0, int row = 0) =>
        plan.Patterns[order][row].Cells[(int)channel];

    /// <summary>Options with the given requirements.</summary>
    private static MappingOptions Options(int[]? requireFm = null, int[]? requireDac = null, int[]? requirePsg = null, bool useDac = true) =>
        new(new HashSet<int>(requireFm ?? []), new HashSet<int>(requireDac ?? []), new HashSet<int>(requirePsg ?? []), useDac);
}
