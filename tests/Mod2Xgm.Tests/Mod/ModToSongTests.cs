using Mod2Xgm.Mod;
using Mod2Xgm.Model;
using Serilog.Core;

namespace Mod2Xgm.Tests.Mod;

/// <summary>Tests for building the song model from a MOD (plan T3.3): structure, cells and conversion notes.</summary>
public sealed class ModToSongTests
{
    private const int C2 = 428;

    /// <summary>Reads the built module and converts it with the PAL clock, returning only the conversion's own notes.</summary>
    private static (Song Song, ConversionNotes Notes) Convert(ModBuilder builder, double clockHz = AmigaPitch.PalClockHz)
    {
        ModModule module = ModReader.Read(builder.Build(), new ConversionNotes(Logger.None));
        ConversionNotes notes = new(Logger.None);
        return (ModToSong.Convert(module, clockHz, ModPlayer.ProTracker2, notes), notes);
    }

    /// <summary>The cell at <paramref name="row"/> and <paramref name="channel"/> of pattern 0.</summary>
    private static Cell CellAt(Song song, int row, int channel) => song.Patterns[0].Rows[row].Cells[channel];

    /// <summary>The note containing <paramref name="text"/>, which must be there exactly once.</summary>
    private static string NoteWith(ConversionNotes notes, string text) =>
        Assert.Single(notes.Messages, m => m.Contains(text, StringComparison.Ordinal));

    /// <summary>Title, channels, orders, instruments, samples and starting timing carry over from the MOD.</summary>
    [Fact]
    public void TheSongMirrorsTheModule()
    {
        (Song song, ConversionNotes notes) = Convert(new ModBuilder()
            .WithOrders(0, 1, 0)
            .WithSample(1, "kick", 64, fineTune: -1, volume: 32, loopStart: 16, loopLength: 32)
            .WithSample(2, "  by someone", 0)
            .WithCell(1, row: 0, channel: 0, sample: 1, period: C2));

        Assert.Equal("test song", song.Title);
        double[] pans = [-1, 1, 1, -1];
        Assert.Equal(pans, song.Channels.Select(c => c.Pan));
        int[] orders = [0, 1, 0];
        Assert.Equal(orders, song.Orders);
        Assert.Null(song.LoopOrder);
        Assert.Equal(new Timing(6, 125), song.Timing);
        Assert.Equal(2, song.Patterns.Count);
        Assert.All(song.Patterns, p => Assert.Equal(64, p.Rows.Count));

        Assert.Equal(31, song.Instruments.Count);
        Assert.Equal(new Instrument("kick", 0, 0.5, -12.5), song.Instruments[0]);
        Assert.Equal(new Instrument("  by someone", 1, 1.0, 0), song.Instruments[1]);

        Assert.Equal(31, song.Samples.Count);
        Sample kick = song.Samples[0];
        Assert.Equal("kick", kick.Name);
        Assert.Equal(64, kick.Length);
        Assert.Equal(63 / 128f, kick.Data[63]);
        Assert.Equal(new SampleLoop(16, 32), kick.Loop);
        Assert.True(song.Samples[1].IsEmpty);
        Assert.Null(song.Samples[1].Loop);
        Assert.Empty(notes.Messages);
    }

    /// <summary>8-bit sample values convert exactly to −1…+1 (value / 128).</summary>
    [Fact]
    public void SampleDataConvertsExactly()
    {
        (Song song, _) = Convert(new ModBuilder().WithSample(1, "edge", 4, data: i => i switch
        {
            0 => sbyte.MinValue,
            1 => (sbyte)-1,
            2 => (sbyte)0,
            _ => sbyte.MaxValue,
        }));

        float[] expected = [-1f, -1 / 128f, 0f, 127 / 128f];
        Assert.Equal(expected, song.Samples[0].Data);
    }

    /// <summary>A cell keeps its note (as a pitch), its instrument (0-based), its Cxx volume and its decoded effects.</summary>
    [Fact]
    public void CellsCarryNoteInstrumentVolumeAndEffects()
    {
        (Song song, _) = Convert(new ModBuilder()
            .WithSample(1, "kick", 64)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0xC, parameter: 0x20)
            .WithCell(0, row: 1, channel: 1, sample: 0, period: 0, effect: 0xA, parameter: 0x04)
            .WithCell(0, row: 2, channel: 2, sample: 0, period: C2, effect: 0x5, parameter: 0x10));

        Cell first = CellAt(song, 0, 0);
        Assert.Equal(AmigaPitch.FromPeriod(C2, AmigaPitch.PalClockHz), first.Note);
        Assert.Equal(0, first.Instrument);
        Assert.Equal(0.5, first.Volume);
        Assert.Empty(first.Effects);

        Cell slide = CellAt(song, 1, 1);
        Assert.Null(slide.Note);
        Assert.Null(slide.Instrument);
        Effect[] volumeDown = [new VolumeSlide(-4 / 64.0, SlideTiming.EveryTickAfterFirst)];
        Assert.Equal(volumeDown, slide.Effects);

        Cell porta = CellAt(song, 2, 2);
        Assert.Equal(24, porta.Note?.Semitone);
        Effect[] portaUp = [new TonePortamento(null, PitchScale.AmigaPeriod), new VolumeSlide(1 / 64.0, SlideTiming.EveryTickAfterFirst)];
        Assert.Equal(portaUp, porta.Effects);
    }

    /// <summary>A cell that does nothing (or only a command that does nothing, like A00) is the shared empty cell.</summary>
    [Fact]
    public void CellsThatDoNothingAreEmpty()
    {
        (Song song, _) = Convert(new ModBuilder().WithCell(0, row: 5, channel: 3, sample: 0, period: 0, effect: 0xA, parameter: 0));

        Assert.Same(Cell.Empty, CellAt(song, 5, 3));
        Assert.Same(Cell.Empty, CellAt(song, 6, 0));
    }

    /// <summary>The input region's clock sets the pitch: the same period is 15.9 cents higher on an NTSC Amiga.</summary>
    [Fact]
    public void TheClockSetsThePitch()
    {
        ModBuilder builder = new ModBuilder().WithSample(1, "kick", 64).WithCell(0, row: 0, channel: 0, sample: 1, period: C2);

        NotePitch? pal = CellAt(Convert(builder).Song, 0, 0).Note;
        NotePitch? ntsc = CellAt(Convert(builder, AmigaPitch.NtscClockHz).Song, 0, 0).Note;

        Assert.Equal(0.0, pal?.Cents);
        Assert.Equal(15.863513114550358, ntsc?.Cents ?? 0, 1e-9);
        Assert.Equal(AmigaPitch.NtscClockHz / C2, ntsc?.PlaybackRateHz);
    }

    /// <summary>Each kind of decoding issue becomes one conversion note with its count and first location.</summary>
    [Fact]
    public void DecodingIssuesBecomeOneNotePerKind()
    {
        (_, ConversionNotes notes) = Convert(new ModBuilder()
            .WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xC, parameter: 0x50)
            .WithCell(0, row: 3, channel: 2, sample: 0, period: 0, effect: 0xC, parameter: 0xFF)
            .WithCell(0, row: 1, channel: 1, sample: 0, period: 0, effect: 0x8, parameter: 0x80)
            .WithCell(0, row: 2, channel: 0, sample: 0, period: 0, effect: 0xD, parameter: 0x1A)
            .WithCell(0, row: 2, channel: 1, sample: 0, period: 0, effect: 0xD, parameter: 0x70)
            .WithCell(0, row: 2, channel: 2, sample: 0, period: 0, effect: 0xE, parameter: 0x81));

        Assert.Equal(5, notes.Count);
        Assert.Contains("2 cells set a volume above 64 with Cxx (first at pattern 0 row 0 channel 1)", NoteWith(notes, "volume above 64"), StringComparison.Ordinal);
        Assert.Contains("1 cells use 8xx panning (first at pattern 0 row 1 channel 2)", NoteWith(notes, "8xx panning"), StringComparison.Ordinal);
        Assert.Contains("have a digit above 9 (first at pattern 0 row 2 channel 1)", NoteWith(notes, "digit above 9"), StringComparison.Ordinal);
        Assert.Contains("name a row above 63 (first at pattern 0 row 2 channel 2)", NoteWith(notes, "row above 63"), StringComparison.Ordinal);
        Assert.Contains("(first at pattern 0 row 2 channel 3)", NoteWith(notes, "E8x"), StringComparison.Ordinal);
    }

    /// <summary>In a 15-sample file, effects 1 and 2 are read as slides, with a note about Ultimate Soundtracker.</summary>
    [Fact]
    public void SoundtrackerEffectsOneAndTwoAreNoted()
    {
        (Song song, ConversionNotes notes) = Convert(new ModBuilder { Soundtracker = true, RestartPosition = 120 }
            .WithSample(1, "kick", 64)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0x1, parameter: 0x03)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: 0, effect: 0x2, parameter: 0x03)
            .WithCell(0, row: 2, channel: 0, sample: 0, period: 0, effect: 0x1, parameter: 0x00));

        Effect[] slideUp = [new PitchSlide(3, PitchScale.AmigaPeriod, SlideTiming.EveryTickAfterFirst)];
        Assert.Equal(slideUp, CellAt(song, 0, 0).Effects);
        Assert.StartsWith("2 cells use effect 1 or 2 in this 15-sample file (first at pattern 0 row 0 channel 1)", NoteWith(notes, "Ultimate Soundtracker"), StringComparison.Ordinal);
    }

    /// <summary>A 31-sample file's effects 1 and 2 are plain ProTracker slides, with no note.</summary>
    [Fact]
    public void ProTrackerEffectsOneAndTwoNeedNoNote()
    {
        (_, ConversionNotes notes) = Convert(new ModBuilder().WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0x1, parameter: 0x03));

        Assert.Empty(notes.Messages);
    }

    /// <summary>A sample number above the module's sample count names no sample, with a note.</summary>
    [Fact]
    public void SampleNumbersAboveTheCountNameNoSample()
    {
        (Song song, ConversionNotes notes) = Convert(new ModBuilder { Soundtracker = true, RestartPosition = 120 }
            .WithSample(1, "kick", 64)
            .WithCell(0, row: 0, channel: 3, sample: 16, period: C2));

        Assert.Equal(15, song.Instruments.Count);
        Assert.Null(CellAt(song, 0, 3).Instrument);
        Assert.NotNull(CellAt(song, 0, 3).Note);
        Assert.StartsWith("1 cells name a sample above 15 (first at pattern 0 row 0 channel 4)", NoteWith(notes, "read as naming no sample"), StringComparison.Ordinal);
    }

    /// <summary>A default volume above 64 plays at full volume, as players cap it.</summary>
    [Fact]
    public void InstrumentVolumeIsCappedAt64()
    {
        (Song song, _) = Convert(new ModBuilder().WithSample(1, "loud", 64, volume: 80));

        Assert.Equal(1.0, song.Instruments[0].DefaultVolume);
    }
}
