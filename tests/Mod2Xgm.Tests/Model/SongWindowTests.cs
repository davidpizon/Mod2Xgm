using Mod2Xgm.Mod;
using Mod2Xgm.Model;
using Mod2Xgm.Model.Window;
using Mod2Xgm.Tests.Mod;
using Serilog.Core;

namespace Mod2Xgm.Tests.Model;

/// <summary>
/// Tests for <see cref="SongWindow"/> (docs/adr/0116-song-window.md): the window's ticks come from the whole song's
/// playback, its song is written in play order, and what the start inherits is written into its first rows. Songs are
/// built with <see cref="ModBuilder"/> and played by the tick player, at 6 ticks a row and 125 BPM unless they say otherwise.
/// </summary>
public sealed class SongWindowTests
{
    private const int C2 = 428;
    private const int C3 = 214;
    private const int TicksPerRow = 6;
    private const int RowsPerPattern = 64;

    private readonly ConversionNotes _notes = new(Logger.None);

    /// <summary>A window from the start of a looping song with no end keeps every tick and the loop.</summary>
    [Fact]
    public void Cut_WithStartAtZero_KeepsTicks()
    {
        // Arrange
        (Song song, Playback playback) = Load(Note());

        // Act
        WindowedSong sut = SongWindow.Cut(song, playback, new SongWindowRequest(new TimePosition(0), null, null, null), _notes);

        // Assert
        Assert.Equal(playback.Ticks.Count, sut.Playback.Ticks.Count);
        Assert.Equal(playback.Ticks.Select(t => (t.TimeSeconds, t.Order, t.Row, t.Tick)), sut.Playback.Ticks.Select(t => (t.TimeSeconds, t.Order, t.Row, t.Tick)));
        Assert.Equal(playback.Ticks.Select(t => t.Channels), sut.Playback.Ticks.Select(t => t.Channels));
        Assert.Equal(0, sut.Playback.LoopTick);
        Assert.Equal(PlaybackEnd.Loops, sut.Playback.End);
    }

    /// <summary>A note still sounding at the start is written again with its instrument, volume, pitch and the frame it reached.</summary>
    [Fact]
    public void Cut_StartMidNote_StartsNoteWithOffsetVolumeAndInstrument()
    {
        // Arrange
        (Song song, Playback playback) = Load(Note().WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0xC, parameter: 32));

        // Act
        WindowedSong sut = SongWindow.Cut(song, playback, new SongWindowRequest(new TrackerPosition(0, 4), null, null, null), _notes);

        // Assert
        Cell first = sut.Song.Patterns[0].Rows[0].Cells[0];
        int frame = Assert.IsType<int>(Assert.Single(first.Effects.OfType<SampleOffset>()).Frame);
        Assert.InRange(frame, 3977, 3979); // 24 ticks of C-2 at 8287 Hz: 0.48 s.
        Assert.Equal(playback.Ticks[23].Channels[0].Pitch, first.Note);
        Assert.Equal(0, first.Instrument);
        Assert.Equal(0.5, first.Volume);
        Assert.Equal(new SampleBlock(0, frame, 16_000 - frame), sut.Playback.Ticks[0].Channels[0].Started);
        Assert.Equal(first.Note, sut.Playback.NotePitches[new NotePosition(0, 0, 0)]);
    }

    /// <summary>The speed set before the window becomes the window's starting speed.</summary>
    [Fact]
    public void Cut_AfterSpeedChange_SetsTiming()
    {
        // Arrange
        (Song song, Playback playback) = Load(Note().WithCell(0, row: 1, channel: 1, sample: 0, period: 0, effect: 0xF, parameter: 3));

        // Act
        WindowedSong sut = SongWindow.Cut(song, playback, new SongWindowRequest(new TrackerPosition(0, 2), null, null, null), _notes);

        // Assert
        Assert.Equal(new Timing(3, 125), sut.Song.Timing);
    }

    /// <summary>An effect that repeats its last parameters (4xx with 0) gets the parameters set before the window.</summary>
    [Fact]
    public void Cut_WithMemoryEffects_ResolvesNullParameters()
    {
        // Arrange
        ModBuilder builder = Note()
            .WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0x4, parameter: 0x44)
            .WithCell(0, row: 4, channel: 0, sample: 0, period: 0, effect: 0x4, parameter: 0);
        (Song song, Playback playback) = Load(builder);
        Vibrato set = song.Patterns[0].Rows[0].Cells[0].Effects.OfType<Vibrato>().Single();

        // Act
        WindowedSong sut = SongWindow.Cut(song, playback, new SongWindowRequest(new TrackerPosition(0, 2), null, null, null), _notes);

        // Assert
        Assert.Equal(set, sut.Song.Patterns[0].Rows[2].Cells[0].Effects.OfType<Vibrato>().Single());
    }

    /// <summary>A tone portamento running into the window gets its target note written on its first row there, with no instrument.</summary>
    [Fact]
    public void Cut_DuringPortamento_AddsTarget()
    {
        // Arrange
        ModBuilder builder = Note()
            .WithCell(0, row: 1, channel: 0, sample: 0, period: C3, effect: 0x3, parameter: 8)
            .WithCell(0, row: 3, channel: 0, sample: 0, period: 0, effect: 0x3, parameter: 0);
        (Song song, Playback playback) = Load(builder);

        // Act
        WindowedSong sut = SongWindow.Cut(song, playback, new SongWindowRequest(new TrackerPosition(0, 2), null, null, null), _notes);

        // Assert
        Cell slide = sut.Song.Patterns[0].Rows[1].Cells[0];
        Assert.Equal(playback.NotePitches[new NotePosition(0, 1, 0)], slide.Note);
        Assert.Null(slide.Instrument);
        Assert.NotNull(slide.Effects.OfType<TonePortamento>().Single().Speed);
        Assert.Contains(_notes.Messages, m => m.Contains("1 tone portamentos running into the window", StringComparison.Ordinal));
    }

    /// <summary>An end stops the song before that row: the last row played gets a stop, and the playback stops.</summary>
    [Fact]
    public void Cut_WithEnd_StopsSong()
    {
        // Arrange
        (Song song, Playback playback) = Load(Note());

        // Act
        WindowedSong sut = SongWindow.Cut(song, playback, new SongWindowRequest(null, new TrackerPosition(0, 10), null, null), _notes);

        // Assert
        Assert.Equal(10 * TicksPerRow, sut.Playback.Ticks.Count);
        Assert.Equal((0, 9), (sut.Playback.Ticks[^1].Order, sut.Playback.Ticks[^1].Row));
        Assert.Contains(sut.Song.Patterns[0].Rows[9].Cells[0].Effects, e => e is StopSong);
        Assert.Null(sut.Playback.LoopTick);
        Assert.Equal(PlaybackEnd.Stops, sut.Playback.End);
        Assert.Null(sut.Song.LoopOrder);
    }

    /// <summary>The loop row starts an order of its own, the order before it breaks early, and the last row jumps back to it.</summary>
    [Fact]
    public void Cut_WithLoopFrom_PutsLoopRowAtOrderStart()
    {
        // Arrange
        (Song song, Playback playback) = Load(Note());

        // Act
        WindowedSong sut = SongWindow.Cut(song, playback, new SongWindowRequest(null, new TrackerPosition(0, 10), new TrackerPosition(0, 5), null), _notes);

        // Assert
        Assert.Equal([0, 1], sut.Song.Orders);
        Assert.Equal(1, sut.Song.LoopOrder);
        Assert.Contains(new PatternBreak(0), sut.Song.Patterns[0].Rows[4].Cells[0].Effects);
        Assert.Contains(new PositionJump(1), sut.Song.Patterns[1].Rows[4].Cells[0].Effects);
        Assert.Equal(5 * TicksPerRow, sut.Playback.LoopTick);
        Assert.Equal((1, 0), (sut.Playback.Ticks[5 * TicksPerRow].Order, sut.Playback.Ticks[5 * TicksPerRow].Row));
        Assert.Equal(PlaybackEnd.Loops, sut.Playback.End);
    }

    /// <summary>A start inside the song's loop, with no end, plays one whole turn of the loop from there and loops to the start.</summary>
    [Fact]
    public void Cut_StartAfterSongLoop_RotatesLoop()
    {
        // Arrange
        (Song song, Playback playback) = Load(Looping());

        // Act
        WindowedSong sut = SongWindow.Cut(song, playback, new SongWindowRequest(new TrackerPosition(1, 10), null, null, null), _notes);

        // Assert
        Assert.Equal(RowsPerPattern * TicksPerRow, sut.Playback.Ticks.Count);
        Assert.Equal(0, sut.Playback.LoopTick);
        Assert.Equal(0, sut.Song.LoopOrder);
        Assert.Contains(_notes.Messages, m => m.Contains("--start is inside the song's loop", StringComparison.Ordinal));
    }

    /// <summary>An end past the first pass of a looping song plays the loop again to reach it.</summary>
    [Fact]
    public void Cut_EndPastFirstPass_UnrollsLoop()
    {
        // Arrange
        (Song song, Playback playback) = Load(Looping());

        // Act
        WindowedSong sut = SongWindow.Cut(song, playback, new SongWindowRequest(null, new TimePosition(playback.DurationSeconds + 1), null, null), _notes);

        // Assert
        Assert.Equal(playback.Ticks.Count + (8 * TicksPerRow), sut.Playback.Ticks.Count); // 1 s is nearest the start of the 9th row (0.96 s).
        Assert.Equal(PlaybackEnd.Stops, sut.Playback.End);
        Assert.Contains(_notes.Messages, m => m.Contains("plays the song's loop again", StringComparison.Ordinal));
    }

    /// <summary>A pattern loop (E6x) is played out: its rows are written once per time they play, with no loop effects left.</summary>
    [Fact]
    public void Cut_OverPatternLoop_PlaysItOut()
    {
        // Arrange
        ModBuilder builder = Note()
            .WithCell(0, row: 0, channel: 1, sample: 0, period: 0, effect: 0xE, parameter: 0x60)
            .WithCell(0, row: 1, channel: 1, sample: 0, period: 0, effect: 0xE, parameter: 0x62);
        (Song song, Playback playback) = Load(builder);

        // Act
        WindowedSong sut = SongWindow.Cut(song, playback, new SongWindowRequest(null, new TrackerPosition(0, 4), null, null), _notes);

        // Assert
        Assert.Equal(8 * TicksPerRow, sut.Playback.Ticks.Count);
        Assert.DoesNotContain(sut.Song.Patterns.SelectMany(p => p.Rows).SelectMany(r => r.Cells).SelectMany(c => c.Effects), e => e is PatternLoop or PatternLoopStart);
    }

    /// <summary>An end past the end of a song that stops is cut to the song's end, with a note.</summary>
    [Fact]
    public void Cut_EndPastStoppingSong_ClampsWithNote()
    {
        // Arrange
        (Song song, Playback playback) = Load(Note().WithCell(0, row: 10, channel: 1, sample: 0, period: 0, effect: 0xF, parameter: 0));

        // Act
        WindowedSong sut = SongWindow.Cut(song, playback, new SongWindowRequest(null, new TimePosition(100), null, null), _notes);

        // Assert
        Assert.Equal(playback.Ticks.Count, sut.Playback.Ticks.Count);
        Assert.Null(sut.Summary.End);
        Assert.Contains(_notes.Messages, m => m.Contains("--end 1:40.000 is past the song's end", StringComparison.Ordinal));
    }

    /// <summary>Every tick of a window points at a row of its song, and every note written has its pitch.</summary>
    [Fact]
    public void Cut_AcrossLoop_KeepsTicksAndNotesConsistent()
    {
        // Arrange
        (Song song, Playback playback) = Load(Looping());

        // Act
        WindowedSong sut = SongWindow.Cut(song, playback, new SongWindowRequest(new TrackerPosition(0, 30), new TimePosition(playback.DurationSeconds + 3), null, null), _notes);

        // Assert
        Assert.All(sut.Playback.Ticks, t => Assert.InRange(t.Row, 0, sut.Song.Patterns[sut.Song.Orders[t.Order]].Rows.Count - 1));
        Assert.All(
            NotesWritten(sut.Song),
            position => Assert.True(sut.Playback.NotePitches.ContainsKey(position), $"No pitch for order {position.Order} row {position.Row} channel {position.Channel}"));
    }

    /// <summary>A start past the end of a song that stops can't make a window.</summary>
    [Fact]
    public void Cut_StartPastStoppingSong_Throws()
    {
        // Arrange
        (Song song, Playback playback) = Load(Note().WithCell(0, row: 10, channel: 1, sample: 0, period: 0, effect: 0xF, parameter: 0));

        // Act & Assert
        WindowException ex = Assert.Throws<WindowException>(() => SongWindow.Cut(song, playback, new SongWindowRequest(new TimePosition(100), null, null, null), _notes));
        Assert.Contains("the song ends at", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A tracker position on a row the song never plays (skipped by a break) can't make a window.</summary>
    [Fact]
    public void Cut_StartOnRowNeverPlayed_Throws()
    {
        // Arrange
        ModBuilder builder = Note().WithOrders(0, 1).WithCell(0, row: 5, channel: 1, sample: 0, period: 0, effect: 0xD, parameter: 0);
        (Song song, Playback playback) = Load(builder);

        // Act & Assert
        WindowException ex = Assert.Throws<WindowException>(() => SongWindow.Cut(song, playback, new SongWindowRequest(new TrackerPosition(0, 10), null, null, null), _notes));
        Assert.Contains("never plays order 0 row 10", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>An end that snaps to the start's row leaves nothing to convert.</summary>
    [Fact]
    public void Cut_EndOnStartRow_Throws()
    {
        // Arrange
        (Song song, Playback playback) = Load(Note());

        // Act & Assert
        WindowException ex = Assert.Throws<WindowException>(() => SongWindow.Cut(song, playback, new SongWindowRequest(new TrackerPosition(0, 4), new TimePosition(0.49), null, null), _notes));
        Assert.Contains("the window would be empty", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A --loop-from outside the window can't make a window.</summary>
    [Fact]
    public void Cut_LoopFromAfterEnd_Throws()
    {
        // Arrange
        (Song song, Playback playback) = Load(Note());

        // Act & Assert
        WindowException ex = Assert.Throws<WindowException>(() => SongWindow.Cut(song, playback, new SongWindowRequest(null, new TrackerPosition(0, 10), new TrackerPosition(0, 20), null), _notes));
        Assert.Contains("isn't inside the window", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A fade as long as the window can't make a window.</summary>
    [Fact]
    public void Cut_FadeLongerThanWindow_Throws()
    {
        // Arrange
        (Song song, Playback playback) = Load(Note());

        // Act & Assert
        WindowException ex = Assert.Throws<WindowException>(() => SongWindow.Cut(song, playback, new SongWindowRequest(null, new TrackerPosition(0, 10), null, 5), _notes));
        Assert.Contains("--fade-out", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The summary says where the window lies in the source and how it ends.</summary>
    [Fact]
    public void Cut_WithStartEndAndLoop_SummarizesWindow()
    {
        // Arrange
        (Song song, Playback playback) = Load(Note());

        // Act
        WindowedSong sut = SongWindow.Cut(song, playback, new SongWindowRequest(new TrackerPosition(0, 2), new TrackerPosition(0, 12), new TrackerPosition(0, 4), null), _notes);

        // Assert
        Assert.Equal((0, 2), (sut.Summary.Start.Order, sut.Summary.Start.Row));
        Assert.Equal((0, 12), (sut.Summary.End?.Order, sut.Summary.End?.Row));
        Assert.Equal((0, 4), (sut.Summary.Loop?.Order, sut.Summary.Loop?.Row));
        Assert.Equal(playback.Ticks[12 * TicksPerRow].TimeSeconds - playback.Ticks[2 * TicksPerRow].TimeSeconds, sut.Summary.Seconds, 1e-9);
        Assert.Equal((2 * TicksPerRow, 12 * TicksPerRow, 4 * TicksPerRow), sut.Summary.Ticks);
    }

    /// <summary>A fade-out silences the last tick, leaves the ticks before it alone and sets the faded volume on its rows.</summary>
    [Fact]
    public void Cut_WithFadeOut_FadesEndAndRows()
    {
        // Arrange
        (Song song, Playback playback) = Load(Note().WithCell(0, row: 19, channel: 1, sample: 1, period: C2));

        // Act
        WindowedSong sut = SongWindow.Cut(song, playback, new SongWindowRequest(null, new TrackerPosition(0, 20), null, 0.2), _notes);

        // Assert
        Assert.All(sut.Playback.Ticks[^1].Channels, c => Assert.Equal(0, c.Volume));
        Assert.Equal(playback.Ticks[100].Channels, sut.Playback.Ticks[100].Channels);
        Cell faded = sut.Song.Patterns[0].Rows[19].Cells[0];
        Assert.InRange(faded.Volume ?? 1, 0, 0.99);
        Assert.Null(sut.Song.Patterns[0].Rows[17].Cells[0].Volume);
        Assert.Equal(0.2, sut.Summary.FadeSeconds);
        Assert.Contains(_notes.Messages, m => m.Contains("--fade-out: 1 notes start during the fade", StringComparison.Ordinal));
    }

    /// <summary>
    /// Trimming both ends of a window that stops starts it on the row of its first note and ends it on the row after its
    /// last sound: the 16 000-frame one-shot from row 8 plays 1.93 s, to the first tick of row 24, so the window ends before
    /// row 25. Both moves are conversion notes.
    /// </summary>
    [Fact]
    public void Cut_WithTrimBoth_StartsAtFirstSoundAndEndsAfterLast()
    {
        // Arrange
        (Song song, Playback playback) = Load(new ModBuilder().WithSample(1, "lead", 16_000).WithCell(0, row: 8, channel: 0, sample: 1, period: C2));

        // Act
        WindowedSong sut = SongWindow.Cut(song, playback, new SongWindowRequest(null, new TrackerPosition(0, 40), null, null, TrimSilence.Both), _notes);

        // Assert
        Assert.Equal((8 * TicksPerRow, 25 * TicksPerRow, -1), sut.Summary.Ticks);
        Assert.Equal(PlaybackEnd.Stops, sut.Playback.End);
        Assert.Contains(_notes.Messages, m => m.StartsWith("--trim-silence: 0:00.960 of silence left out at the start", StringComparison.Ordinal));
        Assert.Contains(_notes.Messages, m => m.StartsWith("--trim-silence: 0:01.800 of silence left out at the end", StringComparison.Ordinal));
    }

    /// <summary>A note at volume 0 is silence: the window starts where the volume comes up, on a row start.</summary>
    [Fact]
    public void Cut_WithTrimStartAndSilentNote_StartsWhereVolumeRises()
    {
        // Arrange
        (Song song, Playback playback) = Load(new ModBuilder()
            .WithSample(1, "lead", 16_000)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0xC, parameter: 0)
            .WithCell(0, row: 4, channel: 0, sample: 0, period: 0, effect: 0xC, parameter: 64));

        // Act
        WindowedSong sut = SongWindow.Cut(song, playback, new SongWindowRequest(null, new TrackerPosition(0, 10), null, null, TrimSilence.Start), _notes);

        // Assert
        Assert.Equal(4 * TicksPerRow, sut.Summary.Ticks.Start);
    }

    /// <summary>A window that loops keeps the silence at its end, with a note: the silence is part of the loop's timing.</summary>
    [Fact]
    public void Cut_WithTrimEndOnLoopingSong_KeepsEnd()
    {
        // Arrange
        (Song song, Playback playback) = Load(Note());

        // Act
        WindowedSong sut = SongWindow.Cut(song, playback, new SongWindowRequest(null, null, null, null, TrimSilence.End), _notes);

        // Assert
        Assert.Equal(playback.Ticks.Count, sut.Playback.Ticks.Count);
        Assert.Contains("--trim-silence: the song loops, so its end keeps its silence: it is part of the loop's timing", _notes.Messages);
    }

    /// <summary>Trimming a window where nothing sounds fails, saying so.</summary>
    [Fact]
    public void Cut_WithTrimAndNothingSounding_Throws()
    {
        // Arrange
        (Song song, Playback playback) = Load(new ModBuilder().WithSample(1, "lead", 16_000).WithCell(0, row: 30, channel: 0, sample: 1, period: C2));

        // Act
        WindowException error = Assert.Throws<WindowException>(() => SongWindow.Cut(song, playback, new SongWindowRequest(null, new TrackerPosition(0, 20), null, null, TrimSilence.Start), _notes));

        // Assert
        Assert.Contains("nothing sounds", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The fade drops evenly in dB: halfway through it is 24 dB down.</summary>
    [Theory]
    [InlineData(0.5, 1.0)]
    [InlineData(1.5, 0.063095734448019331)]
    [InlineData(2.0, 0.0039810717055349734)]
    public void Gain_AtTime_FallsEvenlyInDb(double time, double expected)
    {
        // Act
        double gain = FadeOut.Gain(time, fadeStart: 1, seconds: 1);

        // Assert
        Assert.Equal(expected, gain, 1e-12);
    }

    /// <summary>Every position of a note written in <paramref name="song"/>.</summary>
    private static IEnumerable<NotePosition> NotesWritten(Song song) =>
        song.Orders.SelectMany((pattern, order) => song.Patterns[pattern].Rows.SelectMany((row, r) =>
            row.Cells.Select((cell, c) => (cell, c)).Where(x => x.cell.Note is not null).Select(x => new NotePosition(order, r, x.c))));

    /// <summary>One pattern with a long, unlooped note at its start; it loops to the start.</summary>
    private static ModBuilder Note() => new ModBuilder()
        .WithSample(1, "lead", 16_000)
        .WithCell(0, row: 0, channel: 0, sample: 1, period: C2);

    /// <summary>Two orders; the second jumps back to itself, so the song loops from order 1 row 0 (tick 384).</summary>
    private static ModBuilder Looping() => Note()
        .WithOrders(0, 1)
        .WithCell(1, row: 0, channel: 0, sample: 1, period: C3)
        .WithCell(1, row: 32, channel: 0, sample: 1, period: C2)
        .WithCell(1, row: 63, channel: 1, sample: 0, period: 0, effect: 0xB, parameter: 1);

    private (Song Song, Playback Playback) Load(ModBuilder builder)
    {
        ModModule module = ModReader.Read(builder.Build(), _notes);
        return (ModToSong.Convert(module, AmigaPitch.PalClockHz, _notes), ModTickPlayer.Play(module, AmigaPitch.PalClockHz, 1.0, _notes));
    }
}
