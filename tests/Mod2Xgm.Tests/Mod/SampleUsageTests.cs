using Mod2Xgm.Mod;
using Serilog.Core;

namespace Mod2Xgm.Tests.Mod;

/// <summary>Tests for <see cref="SampleUsageScanner"/>: which notes count, and how the song's flow is followed (plan T2.4).</summary>
public sealed class SampleUsageTests
{
    private const int Note = 428; // C-2

    private readonly ConversionNotes _notes = new(Logger.None);

    /// <summary>A period plays the channel's current sample; a sample number alone only selects it.</summary>
    [Fact]
    public void Scan_WithNotesAndBareSampleNumbers_CountsOnlyNotesThatStart()
    {
        // Arrange
        ModBuilder song = Song()
            .WithCell(0, row: 0, channel: 0, sample: 1, period: Note)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: Note)
            .WithCell(0, row: 2, channel: 0, sample: 2, period: 0)
            .WithCell(0, row: 3, channel: 0, sample: 0, period: Note);

        // Act
        SampleUsage usage = Scan(song);

        // Assert
        Assert.Equal(new SampleUse(1, 2, SampleStatus.Used), usage.Samples[0]);
        Assert.Equal(1, Plays(usage, 2));
    }

    /// <summary>Every slot is listed, numbered from 1; a slot with sound that is never played is unused, one without sound empty.</summary>
    [Fact]
    public void Scan_WithUnplayedSlots_ListsThemAsUnusedOrEmpty()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 0, channel: 0, sample: 1, period: Note);

        // Act
        SampleUsage usage = Scan(song);

        // Assert
        Assert.Equal(Enumerable.Range(1, 31), usage.Samples.Select(s => s.Number));
        Assert.Equal(new SampleUse(3, 0, SampleStatus.Unused), usage.Samples[2]);
        Assert.Equal(new SampleUse(4, 0, SampleStatus.Empty), usage.Samples[3]);
    }

    /// <summary>A sample that holds sound but is never played is reported.</summary>
    [Fact]
    public void Scan_WithUnusedSample_Notes()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 0, channel: 0, sample: 1, period: Note);

        // Act
        Scan(song);

        // Assert
        AssertNote("Sample 3 spare: holds 64 bytes but is never played");
    }

    /// <summary>Each channel remembers its own sample.</summary>
    [Fact]
    public void Scan_WithSamplesOnTwoChannels_KeepsEachChannelsSample()
    {
        // Arrange
        ModBuilder song = Song()
            .WithCell(0, row: 0, channel: 0, sample: 1, period: Note)
            .WithCell(0, row: 0, channel: 3, sample: 2, period: Note)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: Note)
            .WithCell(0, row: 1, channel: 3, sample: 0, period: Note);

        // Act
        SampleUsage usage = Scan(song);

        // Assert
        Assert.Equal((2, 2), (Plays(usage, 1), Plays(usage, 2)));
    }

    /// <summary>
    /// Under Ultimate Soundtracker rules (the 15-sample default) effects 3–F don't exist: a 3xx note starts a sound and
    /// F00 doesn't stop the song.
    /// </summary>
    [Fact]
    public void Scan_WithUltimateSoundtrackerRules_IgnoresEffectsAboveTwo()
    {
        // Arrange
        ModBuilder song = new ModBuilder { Soundtracker = true, RestartPosition = 120 }
            .WithSample(1, "kick", 64)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: Note)
            .WithCell(0, row: 1, channel: 0, sample: 1, period: 381, effect: 0x3, parameter: 0x10)
            .WithCell(0, row: 2, channel: 1, sample: 0, period: 0, effect: 0xF, parameter: 0x00)
            .WithCell(0, row: 3, channel: 0, sample: 1, period: Note);

        // Act
        SampleUsage usage = Scan(song);

        // Assert
        Assert.Equal(3, Plays(usage, 1));
    }

    /// <summary>Tone portamento slides the playing sound and starts nothing.</summary>
    [Theory]
    [InlineData(0x3)]
    [InlineData(0x5)]
    public void Scan_WithTonePortamento_DoesNotCountIt(int effect)
    {
        // Arrange
        ModBuilder song = Song()
            .WithCell(0, row: 0, channel: 0, sample: 1, period: Note)
            .WithCell(0, row: 1, channel: 0, sample: 1, period: 381, effect: effect, parameter: 0x10);

        // Act
        SampleUsage usage = Scan(song);

        // Assert
        Assert.Equal(1, Plays(usage, 1));
    }

    /// <summary>Retrigger restarts one note several times; it counts once.</summary>
    [Fact]
    public void Scan_WithRetrigger_CountsOnce()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 0, channel: 0, sample: 1, period: Note, effect: 0xE, parameter: 0x92);

        // Act
        SampleUsage usage = Scan(song);

        // Assert
        Assert.Equal(1, Plays(usage, 1));
    }

    /// <summary>A note delayed past the end of its row (EDx with x at least the speed) never plays, and one note counts them.</summary>
    [Fact]
    public void Scan_WithNotesDelayedPastTheirRow_DoesNotCountThemAndNotes()
    {
        // Arrange
        ModBuilder song = Song()
            .WithCell(0, row: 0, channel: 0, sample: 1, period: Note, effect: 0xE, parameter: 0xD5)
            .WithCell(0, row: 1, channel: 0, sample: 1, period: Note, effect: 0xE, parameter: 0xD6)
            .WithCell(0, row: 2, channel: 0, sample: 2, period: Note, effect: 0xE, parameter: 0xD3)
            .WithCell(0, row: 2, channel: 1, sample: 0, period: 0, effect: 0xF, parameter: 0x03)
            .WithCell(0, row: 3, channel: 0, sample: 2, period: Note, effect: 0xE, parameter: 0xD2)
            .WithCell(0, row: 4, channel: 1, sample: 0, period: 0, effect: 0xF, parameter: 0x7D);

        // Act
        SampleUsage usage = Scan(song);

        // Assert
        Assert.Equal((1, 1), (Plays(usage, 1), Plays(usage, 2)));
        AssertNote("2 notes are delayed (EDx) past the end of their row and never play (first at order 0 row 1 channel 1)");
    }

    /// <summary>A note on a channel that has no sample yet is silent and reported.</summary>
    [Fact]
    public void Scan_WithNoteBeforeAnySample_CountsNothingAndNotes()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 7, channel: 2, sample: 0, period: Note);

        // Act
        SampleUsage usage = Scan(song);

        // Assert
        Assert.All(usage.Samples, s => Assert.Equal(0, s.Notes));
        AssertNote("1 notes start before their channel has a sample and are silent (first at order 0 row 7 channel 3)");
    }

    /// <summary>Sample numbers above the module's sample count don't select anything: the channel keeps its sample.</summary>
    [Fact]
    public void Scan_WithUnknownSampleNumber_KeepsChannelsSample()
    {
        // Arrange
        ModBuilder song = Song()
            .WithCell(0, row: 0, channel: 0, sample: 1, period: Note)
            .WithCell(0, row: 1, channel: 0, sample: 40, period: Note);

        // Act
        SampleUsage usage = Scan(song);

        // Assert
        Assert.Equal(2, Plays(usage, 1));
        Assert.DoesNotContain(_notes.Messages, m => m.Contains("before their channel has a sample", StringComparison.Ordinal));
    }

    /// <summary>Playing an empty slot is silent, so it stays empty and is reported.</summary>
    [Fact]
    public void Scan_WithEmptySamplePlayed_KeepsItEmptyAndNotes()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 0, channel: 0, sample: 4, period: Note);

        // Act
        SampleUsage usage = Scan(song);

        // Assert
        Assert.Equal(new SampleUse(4, 1, SampleStatus.Empty), usage.Samples[3]);
        AssertNote("Sample 4 credits: played 1 times but holds no sound");
    }

    /// <summary>A pattern listed twice in the order table is counted twice; a song that just ends is walked once.</summary>
    [Fact]
    public void Scan_WithSongThatEnds_WalksOrdersOnce()
    {
        // Arrange
        ModBuilder song = Song()
            .WithOrders(0, 1, 0)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: Note)
            .WithCell(1, row: 0, channel: 0, sample: 2, period: Note);

        // Act
        SampleUsage usage = Scan(song);

        // Assert
        Assert.Equal((2, 1), (Plays(usage, 1), Plays(usage, 2)));
        Assert.Equal(3 * 64, usage.RowsPlayed);
        Assert.False(usage.LoopsBack);
        Assert.DoesNotContain(_notes.Messages, m => m.Contains("jumps back", StringComparison.Ordinal));
    }

    /// <summary>Dxx goes to the given row (two decimal digits) of the next order; rows past the end mean row 0.</summary>
    [Theory]
    [InlineData(0x16, 16, 0)]
    [InlineData(0x70, 0, 1)]
    public void Scan_WithPatternBreak_SkipsToRowInNextOrder(int parameter, int targetRow, int firstSamplePlays)
    {
        // Arrange
        ModBuilder song = Song()
            .WithOrders(0, 1)
            .WithCell(0, row: 10, channel: 0, sample: 0, period: 0, effect: 0xD, parameter: parameter)
            .WithCell(0, row: 11, channel: 0, sample: 3, period: Note)
            .WithCell(1, row: 15, channel: 0, sample: 1, period: Note)
            .WithCell(1, row: 16, channel: 0, sample: 2, period: Note);

        // Act
        SampleUsage usage = Scan(song);

        // Assert
        Assert.Equal((firstSamplePlays, 1, 0), (Plays(usage, 1), Plays(usage, 2), Plays(usage, 3)));
        Assert.Equal(11 + (64 - targetRow), usage.RowsPlayed);
    }

    /// <summary>Bxx can skip orders; skipped orders and patterns that never play are reported.</summary>
    [Fact]
    public void Scan_WithPositionJumpForward_SkipsOrdersAndNotes()
    {
        // Arrange
        ModBuilder song = Song()
            .WithOrders(0, 1, 2)
            .WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xB, parameter: 2)
            .WithCell(1, row: 0, channel: 0, sample: 1, period: Note)
            .WithCell(2, row: 0, channel: 0, sample: 2, period: Note);

        // Act
        SampleUsage usage = Scan(song);

        // Assert
        Assert.Equal((0, 1), (Plays(usage, 1), Plays(usage, 2)));
        AssertNote("Orders 1 are never reached (jumps skip them)");
        AssertNote("Patterns 1 are stored in the file but never played");
    }

    /// <summary>Bxx together with Dxx jumps to that row of that order.</summary>
    [Fact]
    public void Scan_WithPositionJumpAndPatternBreak_GoesToThatRow()
    {
        // Arrange
        ModBuilder song = Song()
            .WithOrders(0, 1, 2)
            .WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xB, parameter: 2)
            .WithCell(0, row: 0, channel: 1, sample: 0, period: 0, effect: 0xD, parameter: 0x05)
            .WithCell(2, row: 4, channel: 0, sample: 1, period: Note)
            .WithCell(2, row: 5, channel: 0, sample: 2, period: Note);

        // Act
        SampleUsage usage = Scan(song);

        // Assert
        Assert.Equal((0, 1), (Plays(usage, 1), Plays(usage, 2)));
        Assert.Equal(1 + 59, usage.RowsPlayed);
    }

    /// <summary>A jump back to rows already played: the intro plays once, the loop once through and one more time around, then the walk stops.</summary>
    [Fact]
    public void Scan_WithJumpBack_WalksLoopOneMoreTime()
    {
        // Arrange
        ModBuilder song = Song()
            .WithOrders(0, 1, 2)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: Note)
            .WithCell(1, row: 0, channel: 0, sample: 2, period: Note)
            .WithCell(2, row: 63, channel: 0, sample: 0, period: 0, effect: 0xB, parameter: 1);

        // Act
        SampleUsage usage = Scan(song);

        // Assert
        Assert.Equal((1, 2), (Plays(usage, 1), Plays(usage, 2)));
        Assert.True(usage.LoopsBack);
        Assert.Equal(64 + (2 * 128), usage.RowsPlayed);
        AssertNote("The song jumps back to order 1 row 0 and repeats from there");
    }

    /// <summary>E6x repeats rows on purpose, so every repeat counts and it isn't treated as the song looping.</summary>
    [Fact]
    public void Scan_WithPatternLoop_CountsEveryRepeat()
    {
        // Arrange
        ModBuilder song = Song()
            .WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xE, parameter: 0x60)
            .WithCell(0, row: 1, channel: 0, sample: 1, period: Note)
            .WithCell(0, row: 2, channel: 0, sample: 0, period: 0, effect: 0xE, parameter: 0x62)
            .WithCell(0, row: 3, channel: 0, sample: 2, period: Note);

        // Act
        SampleUsage usage = Scan(song);

        // Assert
        Assert.Equal((3, 1), (Plays(usage, 1), Plays(usage, 2)));
        Assert.False(usage.LoopsBack);
        Assert.Equal((3 * 3) + 61, usage.RowsPlayed);
        Assert.DoesNotContain(_notes.Messages, m => m.Contains("jumps back", StringComparison.Ordinal));
    }

    /// <summary>Pattern loops on two channels both have to finish before the song moves on.</summary>
    [Fact]
    public void Scan_WithPatternLoopsOnTwoChannels_FinishesBoth()
    {
        // Arrange
        ModBuilder song = Song()
            .WithCell(0, row: 0, channel: 0, sample: 1, period: Note)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: 0, effect: 0xE, parameter: 0x61)
            .WithCell(0, row: 1, channel: 1, sample: 0, period: 0, effect: 0xE, parameter: 0x62);

        // Act
        SampleUsage usage = Scan(song);

        // Assert
        Assert.Equal(6, Plays(usage, 1));
        Assert.False(usage.LoopsBack);
    }

    /// <summary>F00 stops the song; later rows aren't counted.</summary>
    [Fact]
    public void Scan_WithF00_StopsSongAndNotes()
    {
        // Arrange
        ModBuilder song = Song()
            .WithCell(0, row: 5, channel: 0, sample: 1, period: Note, effect: 0xF, parameter: 0x00)
            .WithCell(0, row: 6, channel: 0, sample: 2, period: Note);

        // Act
        SampleUsage usage = Scan(song);

        // Assert
        Assert.Equal((1, 0), (Plays(usage, 1), Plays(usage, 2)));
        Assert.Equal(6, usage.RowsPlayed);
        AssertNote("Order 0 row 5: effect F00 stops the song");
    }

    /// <summary>A jump past the last order ends the song, with a note.</summary>
    [Fact]
    public void Scan_WithJumpPastEnd_EndsSongAndNotes()
    {
        // Arrange
        ModBuilder song = Song()
            .WithOrders(0, 1)
            .WithCell(0, row: 3, channel: 0, sample: 0, period: 0, effect: 0xB, parameter: 9)
            .WithCell(1, row: 0, channel: 0, sample: 1, period: Note);

        // Act
        SampleUsage usage = Scan(song);

        // Assert
        Assert.Equal(0, Plays(usage, 1));
        Assert.Equal(4, usage.RowsPlayed);
        AssertNote("Order 0 row 3: jump to order 9, past the song's 2 orders; treated as the end of the song");
    }

    /// <summary>A pattern break on the last order ends the song without a jump note.</summary>
    [Fact]
    public void Scan_WithPatternBreakOnLastOrder_EndsSongWithoutNote()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 1, channel: 0, sample: 0, period: 0, effect: 0xD, parameter: 0);

        // Act
        SampleUsage usage = Scan(song);

        // Assert
        Assert.Equal(2, usage.RowsPlayed);
        Assert.DoesNotContain(_notes.Messages, m => m.Contains("past the song", StringComparison.Ordinal));
    }

    /// <summary>15-sample files are scanned the same way, with 15 slots.</summary>
    [Fact]
    public void Scan_WithFifteenSampleFile_ScansFifteenSlots()
    {
        // Arrange
        ModBuilder song = new ModBuilder { Soundtracker = true }
            .WithSample(15, "last", 64)
            .WithCell(0, row: 0, channel: 0, sample: 15, period: Note);

        // Act
        SampleUsage usage = Scan(song);

        // Assert
        Assert.Equal(15, usage.Samples.Count);
        Assert.Equal(1, Plays(usage, 15));
    }

    /// <summary>A builder with samples 1–3 holding sound and sample 4 empty.</summary>
    private static ModBuilder Song() => new ModBuilder()
        .WithSample(1, "kick", 64)
        .WithSample(2, "snare", 64)
        .WithSample(3, "spare", 64)
        .WithSample(4, "credits", 0);

    /// <summary>Reads the built module and scans it; only the scan's own notes go to this test's notes.</summary>
    private SampleUsage Scan(ModBuilder builder)
    {
        ModModule module = ModReader.Read(builder.Build(), new ConversionNotes(Logger.None));
        return SampleUsageScanner.Scan(module, _notes);
    }

    /// <summary>The play count of sample <paramref name="number"/>.</summary>
    private static int Plays(SampleUsage usage, int number) => usage.Samples[number - 1].Notes;

    /// <summary>Asserts that some note contains <paramref name="text"/>.</summary>
    private void AssertNote(string text) =>
        Assert.Contains(_notes.Messages, m => m.Contains(text, StringComparison.Ordinal));
}
