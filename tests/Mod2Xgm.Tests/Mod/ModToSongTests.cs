using Mod2Xgm.Mod;
using Mod2Xgm.Model;
using Serilog.Core;

namespace Mod2Xgm.Tests.Mod;

/// <summary>Tests for building the song model from a MOD (plan T3.3): structure, cells and conversion notes.</summary>
public sealed class ModToSongTests
{
    private const int C2 = 428;

    private readonly ConversionNotes _notes = new(Logger.None);

    /// <summary>Title, channels, orders, loop and starting timing carry over from the MOD, with nothing to note.</summary>
    [Fact]
    public void Convert_WithModule_MirrorsSongStructure()
    {
        // Arrange
        ModBuilder module = new ModBuilder()
            .WithOrders(0, 1, 0)
            .WithCell(1, row: 0, channel: 0, sample: 1, period: C2);
        double[] pans = [-1, 1, 1, -1];
        int[] orders = [0, 1, 0];

        // Act
        Song song = Convert(module);

        // Assert
        Assert.Equal("test song", song.Title);
        Assert.Equal(pans, song.Channels.Select(c => c.Pan));
        Assert.Equal(orders, song.Orders);
        Assert.Null(song.LoopOrder);
        Assert.Equal(new Timing(6, 125), song.Timing);
        Assert.Equal(2, song.Patterns.Count);
        Assert.All(song.Patterns, p => Assert.Equal(64, p.Rows.Count));
        Assert.Empty(_notes.Messages);
    }

    /// <summary>Every sample slot becomes an instrument, with its volume and finetune in cents; empty slots keep their names.</summary>
    [Fact]
    public void Convert_WithSamples_MirrorsInstruments()
    {
        // Arrange
        ModBuilder module = new ModBuilder()
            .WithSample(1, "kick", 64, fineTune: -1, volume: 32, loopStart: 16, loopLength: 32)
            .WithSample(2, "  by someone", 0);

        // Act
        Song song = Convert(module);

        // Assert
        Assert.Equal(31, song.Instruments.Count);
        Assert.Equal(new Instrument("kick", 0, 0.5, -12.5), song.Instruments[0]);
        Assert.Equal(new Instrument("  by someone", 1, 1.0, 0), song.Instruments[1]);
    }

    /// <summary>Every sample slot becomes a sample with its data and loop; a slot without sound is empty and has no loop.</summary>
    [Fact]
    public void Convert_WithSamples_MirrorsSampleData()
    {
        // Arrange
        ModBuilder module = new ModBuilder()
            .WithSample(1, "kick", 64, fineTune: -1, volume: 32, loopStart: 16, loopLength: 32)
            .WithSample(2, "  by someone", 0);

        // Act
        Song song = Convert(module);

        // Assert
        Assert.Equal(31, song.Samples.Count);
        Sample kick = song.Samples[0];
        Assert.Equal("kick", kick.Name);
        Assert.Equal(64, kick.Length);
        Assert.Equal(63 / 128f, kick.Data[63]);
        Assert.Equal(new SampleLoop(16, 32), kick.Loop);
        Assert.True(song.Samples[1].IsEmpty);
        Assert.Null(song.Samples[1].Loop);
    }

    /// <summary>8-bit sample values convert exactly to −1…+1 (value / 128).</summary>
    [Fact]
    public void Convert_WithEdgeSampleValues_ConvertsExactly()
    {
        // Arrange
        ModBuilder module = new ModBuilder().WithSample(1, "edge", 4, data: i => i switch
        {
            0 => sbyte.MinValue,
            1 => -1,
            2 => 0,
            _ => sbyte.MaxValue,
        });
        float[] expected = [-1f, -1 / 128f, 0f, 127 / 128f];

        // Act
        Song song = Convert(module);

        // Assert
        Assert.Equal(expected, song.Samples[0].Data);
    }

    /// <summary>A cell keeps its note (as a pitch), its instrument (0-based) and its Cxx volume, which isn't an effect any more.</summary>
    [Fact]
    public void Convert_WithNoteAndVolume_KeepsThemOnCell()
    {
        // Arrange
        ModBuilder module = new ModBuilder()
            .WithSample(1, "kick", 64)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0xC, parameter: 0x20);

        // Act
        Cell cell = CellAt(Convert(module), 0, 0);

        // Assert
        Assert.Equal(AmigaPitch.FromPeriod(C2, AmigaPitch.PalClockHz), cell.Note);
        Assert.Equal(0, cell.Instrument);
        Assert.Equal(0.5, cell.Volume);
        Assert.Empty(cell.Effects);
    }

    /// <summary>A cell with only a command has no note or instrument, and keeps the decoded effect.</summary>
    [Fact]
    public void Convert_WithEffectOnly_KeepsDecodedEffect()
    {
        // Arrange
        ModBuilder module = new ModBuilder().WithCell(0, row: 1, channel: 1, sample: 0, period: 0, effect: 0xA, parameter: 0x04);
        Effect[] expected = [new VolumeSlide(-4 / 64.0, SlideTiming.EveryTickAfterFirst)];

        // Act
        Cell cell = CellAt(Convert(module), 1, 1);

        // Assert
        Assert.Null(cell.Note);
        Assert.Null(cell.Instrument);
        Assert.Equal(expected, cell.Effects);
    }

    /// <summary>A combined command (5xy) keeps its note as the target and decodes to both of its effects.</summary>
    [Fact]
    public void Convert_WithPortamentoAndVolumeSlide_KeepsNoteAndBothEffects()
    {
        // Arrange
        ModBuilder module = new ModBuilder().WithCell(0, row: 2, channel: 2, sample: 0, period: C2, effect: 0x5, parameter: 0x10);
        Effect[] expected = [new TonePortamento(null, PitchScale.AmigaPeriod), new VolumeSlide(1 / 64.0, SlideTiming.EveryTickAfterFirst)];

        // Act
        Cell cell = CellAt(Convert(module), 2, 2);

        // Assert
        Assert.Equal(24, cell.Note?.Semitone);
        Assert.Equal(expected, cell.Effects);
    }

    /// <summary>A cell that does nothing (or only a command that does nothing, like A00) is the shared empty cell.</summary>
    [Theory]
    [InlineData(5, 3)]
    [InlineData(6, 0)]
    public void Convert_WithCellThatDoesNothing_UsesSharedEmptyCell(int row, int channel)
    {
        // Arrange
        ModBuilder module = new ModBuilder().WithCell(0, row: 5, channel: 3, sample: 0, period: 0, effect: 0xA, parameter: 0);

        // Act
        Song song = Convert(module);

        // Assert
        Assert.Same(Cell.Empty, CellAt(song, row, channel));
    }

    /// <summary>On a PAL Amiga the period table is exactly on pitch.</summary>
    [Fact]
    public void Convert_WithPalClock_PlaysNoteOnPitch()
    {
        // Arrange
        ModBuilder module = new ModBuilder().WithSample(1, "kick", 64).WithCell(0, row: 0, channel: 0, sample: 1, period: C2);

        // Act
        Song song = Convert(module);

        // Assert
        Assert.Equal(0.0, CellAt(song, 0, 0).Note?.Cents);
    }

    /// <summary>The input region's clock sets the pitch: the same period is 15.9 cents higher on an NTSC Amiga.</summary>
    [Fact]
    public void Convert_WithNtscClock_RaisesPitch()
    {
        // Arrange
        ModBuilder module = new ModBuilder().WithSample(1, "kick", 64).WithCell(0, row: 0, channel: 0, sample: 1, period: C2);

        // Act
        NotePitch? pitch = CellAt(Convert(module, AmigaPitch.NtscClockHz), 0, 0).Note;

        // Assert
        Assert.Equal(15.863513114550358, pitch?.Cents ?? 0, 1e-9);
        Assert.Equal(AmigaPitch.NtscClockHz / C2, pitch?.PlaybackRateHz);
    }

    /// <summary>Each kind of decoding issue becomes one conversion note with its count and first location.</summary>
    [Fact]
    public void Convert_WithDecodingIssues_AddsOneNotePerKind()
    {
        // Arrange
        ModBuilder module = new ModBuilder()
            .WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xC, parameter: 0x50)
            .WithCell(0, row: 3, channel: 2, sample: 0, period: 0, effect: 0xC, parameter: 0xFF)
            .WithCell(0, row: 1, channel: 1, sample: 0, period: 0, effect: 0x8, parameter: 0x80)
            .WithCell(0, row: 2, channel: 0, sample: 0, period: 0, effect: 0xD, parameter: 0x1A)
            .WithCell(0, row: 2, channel: 1, sample: 0, period: 0, effect: 0xD, parameter: 0x70)
            .WithCell(0, row: 2, channel: 2, sample: 0, period: 0, effect: 0xE, parameter: 0x81);

        // Act
        Convert(module);

        // Assert
        Assert.Equal(5, _notes.Count);
        Assert.Contains("2 cells set a volume above 64 with Cxx (first at pattern 0 row 0 channel 1)", NoteWith("volume above 64"), StringComparison.Ordinal);
        Assert.Contains("1 cells use 8xx panning (first at pattern 0 row 1 channel 2)", NoteWith("8xx panning"), StringComparison.Ordinal);
        Assert.Contains("have a digit above 9 (first at pattern 0 row 2 channel 1)", NoteWith("digit above 9"), StringComparison.Ordinal);
        Assert.Contains("name a row above 63 (first at pattern 0 row 2 channel 2)", NoteWith("row above 63"), StringComparison.Ordinal);
        Assert.Contains("(first at pattern 0 row 2 channel 3)", NoteWith("E8x"), StringComparison.Ordinal);
    }

    /// <summary>A 15-sample file read with ProTracker rules reads effects 1 and 2 as slides, with a note about Ultimate Soundtracker.</summary>
    [Fact]
    public void Convert_WithSoundtrackerFileUnderProTrackerRules_ReadsSlidesAndNotes()
    {
        // Arrange
        ModBuilder module = Soundtracker()
            .WithSample(1, "kick", 64)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0x1, parameter: 0x03)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: 0, effect: 0x2, parameter: 0x03)
            .WithCell(0, row: 2, channel: 0, sample: 0, period: 0, effect: 0x1, parameter: 0x00);
        Effect[] slideUp = [new PitchSlide(3, PitchScale.AmigaPeriod, SlideTiming.EveryTickAfterFirst)];

        // Act
        Song song = Convert(module, player: ModPlayer.ProTracker2);

        // Assert
        Assert.Equal(slideUp, CellAt(song, 0, 0).Effects);
        Assert.StartsWith("2 cells use effect 1 or 2 in this 15-sample file (first at pattern 0 row 0 channel 1)", NoteWith("Ultimate Soundtracker"), StringComparison.Ordinal);
    }

    /// <summary>A 31-sample file's effects 1 and 2 are plain ProTracker slides, with no note.</summary>
    [Fact]
    public void Convert_WithProTrackerSlides_AddsNoNote()
    {
        // Arrange
        ModBuilder module = new ModBuilder().WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0x1, parameter: 0x03);

        // Act
        Convert(module);

        // Assert
        Assert.Empty(_notes.Messages);
    }

    /// <summary>A sample number above the module's sample count names no sample, with a note; the cell's note stays.</summary>
    [Fact]
    public void Convert_WithSampleNumberAboveCount_NamesNoSampleAndNotes()
    {
        // Arrange
        ModBuilder module = Soundtracker()
            .WithSample(1, "kick", 64)
            .WithCell(0, row: 0, channel: 3, sample: 16, period: C2);

        // Act
        Song song = Convert(module);

        // Assert
        Assert.Equal(15, song.Instruments.Count);
        Assert.Null(CellAt(song, 0, 3).Instrument);
        Assert.NotNull(CellAt(song, 0, 3).Note);
        Assert.StartsWith("1 cells name a sample above 15 (first at pattern 0 row 0 channel 4)", NoteWith("read as naming no sample"), StringComparison.Ordinal);
    }

    /// <summary>Ultimate Soundtracker rules (the 15-sample default): 1xy is arpeggio.</summary>
    [Fact]
    public void Convert_WithUltimateSoundtrackerRules_Reads1xyAsArpeggio()
    {
        // Arrange
        ModBuilder module = Soundtracker().WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0x1, parameter: 0x37);
        Effect[] expected = [new Arpeggio(3, 7)];

        // Act
        Song song = Convert(module);

        // Assert
        Assert.Equal(expected, CellAt(song, 0, 0).Effects);
    }

    /// <summary>Ultimate Soundtracker rules: 2xy bends y up or else x down, with no note about slides.</summary>
    [Theory]
    [InlineData(0x03, 3)]
    [InlineData(0x40, -4)]
    public void Convert_WithUltimateSoundtrackerRules_Reads2xyAsBend(int parameter, int expectedAmount)
    {
        // Arrange
        ModBuilder module = Soundtracker().WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0x2, parameter: parameter);
        Effect[] expected = [new PitchSlide(expectedAmount, PitchScale.AmigaPeriod, SlideTiming.EveryTickAfterFirst)];

        // Act
        Song song = Convert(module);

        // Assert
        Assert.Equal(expected, CellAt(song, 0, 0).Effects);
        Assert.DoesNotContain(_notes.Messages, m => m.Contains("read as pitch slides", StringComparison.Ordinal));
    }

    /// <summary>Ultimate Soundtracker rules: any command from 3 up is ignored, with one note.</summary>
    [Fact]
    public void Convert_WithUltimateSoundtrackerRules_IgnoresOtherEffectsAndNotes()
    {
        // Arrange
        ModBuilder module = Soundtracker()
            .WithCell(0, row: 3, channel: 1, sample: 0, period: 0, effect: 0xC, parameter: 0x20)
            .WithCell(0, row: 4, channel: 1, sample: 0, period: 0, effect: 0xB, parameter: 0x01);

        // Act
        Song song = Convert(module);

        // Assert
        Assert.Same(Cell.Empty, CellAt(song, 3, 1));
        Assert.Same(Cell.Empty, CellAt(song, 4, 1));
        Assert.StartsWith("2 cells use effects 3–F, which Ultimate Soundtracker doesn't have (first at pattern 0 row 3 channel 2)", NoteWith("doesn't have"), StringComparison.Ordinal);
    }

    /// <summary>Ultimate Soundtracker's tempo comes from byte 471: 0x78 and 0 are 125 BPM, others follow the timer formula, 240+ fall back.</summary>
    [Theory]
    [InlineData(0x78, 125.0, false)]
    [InlineData(0x00, 125.0, false)]
    [InlineData(100, 103.8318, false)]
    [InlineData(0xF0, 125.0, true)]
    public void Convert_WithUltimateSoundtrackerRules_TakesTempoFromByte471(int tempoByte, double bpm, bool noted)
    {
        // Arrange
        ModBuilder module = Soundtracker(tempoByte).WithSample(1, "kick", 64);

        // Act
        Song song = Convert(module);

        // Assert
        Assert.Equal(6, song.Timing.Speed);
        Assert.Equal(bpm, song.Timing.Bpm, 1e-4);
        Assert.Equal(noted, _notes.Messages.Any(m => m.Contains("can't use as a tempo", StringComparison.Ordinal)));
    }

    /// <summary>Under Ultimate Soundtracker rules finetunes are ignored and looped samples start at their loop start, each with a note.</summary>
    [Fact]
    public void Convert_WithUltimateSoundtrackerRules_IgnoresFinetuneAndStartsSamplesAtLoopStart()
    {
        // Arrange
        ModBuilder module = Soundtracker()
            .WithSample(1, "pad", 100, fineTune: 3, loopStartWords: 60, loopLength: 40)
            .WithSample(2, "kick", 64);

        // Act
        Song song = Convert(module);

        // Assert
        Assert.Equal(0, song.Instruments[0].FineTuneCents);
        Assert.Equal(40, song.Samples[0].Length);
        Assert.Equal(60 / 128f, song.Samples[0].Data[0]);
        Assert.Equal(new SampleLoop(0, 40), song.Samples[0].Loop);
        Assert.Equal(64, song.Samples[1].Length);
        Assert.Single(_notes.Messages, m => m.Contains("1 samples have a finetune; Ultimate Soundtracker had none", StringComparison.Ordinal));
        Assert.Single(_notes.Messages, m => m.Contains("1 looping samples start at their loop start", StringComparison.Ordinal));
    }

    /// <summary>A default volume above 64 plays at full volume, as players cap it.</summary>
    [Fact]
    public void Convert_WithInstrumentVolumeAbove64_CapsIt()
    {
        // Arrange
        ModBuilder module = new ModBuilder().WithSample(1, "loud", 64, volume: 80);

        // Act
        Song song = Convert(module);

        // Assert
        Assert.Equal(1.0, song.Instruments[0].DefaultVolume);
    }

    /// <summary>
    /// Reads the built module (with <paramref name="player"/>'s rules, or the file's default) and converts it; only the
    /// conversion's own notes go to this test's notes.
    /// </summary>
    private Song Convert(ModBuilder builder, double clockHz = AmigaPitch.PalClockHz, ModPlayer? player = null)
    {
        ModModule module = ModReader.Read(builder.Build(), new ConversionNotes(Logger.None), player);
        return ModToSong.Convert(module, clockHz, _notes);
    }

    /// <summary>A 15-sample builder whose byte 471 holds <paramref name="tempoByte"/>.</summary>
    private static ModBuilder Soundtracker(int tempoByte = 120) => new() { Soundtracker = true, RestartPosition = tempoByte };

    /// <summary>The cell at <paramref name="row"/> and <paramref name="channel"/> of pattern 0.</summary>
    private static Cell CellAt(Song song, int row, int channel) => song.Patterns[0].Rows[row].Cells[channel];

    /// <summary>The note containing <paramref name="text"/>, which must be there exactly once.</summary>
    private string NoteWith(string text) =>
        Assert.Single(_notes.Messages, m => m.Contains(text, StringComparison.Ordinal));
}
