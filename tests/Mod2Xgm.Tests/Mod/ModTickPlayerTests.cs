using Mod2Xgm.Mod;
using Mod2Xgm.Model;
using Serilog.Core;

namespace Mod2Xgm.Tests.Mod;

/// <summary>
/// Tests for <see cref="ModTickPlayer"/> (plan T3.4), a port of ProTracker 2's replayer: song length and flow, the loop
/// point, and tick-by-tick channel state, on small songs whose results are worked out by hand from ProTracker's rules.
/// </summary>
public sealed class ModTickPlayerTests
{
    private const int C2 = 428;
    private const int Ticks64Rows = 64 * 6;

    private readonly ConversionNotes _notes = new(Logger.None);

    /// <summary>ProTracker's CIA tick rate: 709 379 Hz / (1 773 447 / BPM + 1), so 125 BPM is a little under 50 Hz.</summary>
    [Theory]
    [InlineData(125, 709_379.0 / 14_188)]
    [InlineData(250, 709_379.0 / 7_094)]
    public void CiaTickHz_WithBpm_ReturnsProTrackersRate(int bpm, double expected)
    {
        // Act
        double hz = ModTickPlayer.CiaTickHz(bpm);

        // Assert
        Assert.Equal(expected, hz);
    }

    /// <summary>One empty pattern: 64 rows of 6 ticks at 125 BPM, then it wraps to the start and loops from tick 0.</summary>
    [Fact]
    public void Play_WithOneEmptyPattern_PlaysSixTicksPerRowAndLoopsToStart()
    {
        // Act
        Playback playback = Play(Song());

        // Assert
        Assert.Equal(Ticks64Rows, playback.Ticks.Count);
        Assert.Equal(PlaybackEnd.Loops, playback.End);
        Assert.Equal(0, playback.LoopTick);
        Assert.Equal(Ticks64Rows / ModTickPlayer.CiaTickHz(125), playback.DurationSeconds, 1e-9);
        Assert.Equal(0.0, playback.LoopStartSeconds);
        PlaybackTick last = playback.Ticks[^1];
        Assert.Equal((0, 63, 5), (last.Order, last.Row, last.Tick));
        Assert.Equal((0, 1, 0), (playback.Ticks[6].Order, playback.Ticks[6].Row, playback.Ticks[6].Tick));
    }

    /// <summary>Fxx below 0x20 sets the ticks per row at once.</summary>
    [Fact]
    public void Play_WithSetSpeed_ChangesTicksPerRow()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xF, parameter: 3);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(64 * 3, playback.Ticks.Count);
    }

    /// <summary>Fxx from 0x20 sets the BPM one tick late, as ProTracker's CIA timer does.</summary>
    [Fact]
    public void Play_WithSetTempo_ChangesTickLengthOneTickLate()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xF, parameter: 250);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(1 / ModTickPlayer.CiaTickHz(125), playback.Ticks[0].DurationSeconds, 1e-12);
        Assert.Equal(1 / ModTickPlayer.CiaTickHz(250), playback.Ticks[1].DurationSeconds, 1e-12);
        Assert.Equal((1 / ModTickPlayer.CiaTickHz(125)) + ((Ticks64Rows - 1) / ModTickPlayer.CiaTickHz(250)), playback.DurationSeconds, 1e-9);
    }

    /// <summary>An NTSC Amiga runs 60 ticks a second at 125 BPM: the same ticks, 6/5 as fast.</summary>
    [Fact]
    public void Play_WithNtscTickRate_PlaysSameTicksFaster()
    {
        // Arrange
        Playback pal = Play(Song());

        // Act
        Playback ntsc = Play(Song(), tickRateScale: 1.2);

        // Assert
        Assert.Equal(pal.Ticks.Count, ntsc.Ticks.Count);
        Assert.Equal(pal.DurationSeconds / 1.2, ntsc.DurationSeconds, 1e-9);
    }

    /// <summary>Bxx jumps after the row; jumping back to a played row ends one pass and marks the loop there.</summary>
    [Fact]
    public void Play_WithPositionJumpBack_EndsPassAndLoops()
    {
        // Arrange
        ModBuilder song = Song().WithOrders(0, 1, 2).WithCell(1, row: 0, channel: 0, sample: 0, period: 0, effect: 0xB, parameter: 0);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(Ticks64Rows + 6, playback.Ticks.Count);
        Assert.Equal(0, playback.LoopTick);
        Assert.DoesNotContain(playback.Ticks, t => t.Order == 2);
    }

    /// <summary>A jump back to the second order loops from that order's first tick, not the song's start.</summary>
    [Fact]
    public void Play_WithJumpToSecondOrder_LoopsMidSong()
    {
        // Arrange
        ModBuilder song = Song().WithOrders(0, 1).WithCell(1, row: 63, channel: 0, sample: 0, period: 0, effect: 0xB, parameter: 1);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(2 * Ticks64Rows, playback.Ticks.Count);
        Assert.Equal(Ticks64Rows, playback.LoopTick);
        Assert.Equal(Ticks64Rows / ModTickPlayer.CiaTickHz(125), playback.LoopStartSeconds ?? 0, 1e-9);
    }

    /// <summary>Dxx breaks to a row of the next order (written in decimal); a row above 63 means row 0.</summary>
    [Theory]
    [InlineData(0x10, 10)]
    [InlineData(0x70, 0)]
    public void Play_WithPatternBreak_SkipsToRowOfNextOrder(int parameter, int targetRow)
    {
        // Arrange
        ModBuilder song = Song().WithOrders(0, 1).WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xD, parameter: parameter);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal((1 + 64 - targetRow) * 6, playback.Ticks.Count);
        Assert.Equal((1, targetRow), (playback.Ticks[6].Order, playback.Ticks[6].Row));
    }

    /// <summary>Bxx and Dxx on the same row go to that row of that order.</summary>
    [Fact]
    public void Play_WithJumpAndBreakOnSameRow_GoesToThatRowOfThatOrder()
    {
        // Arrange
        ModBuilder song = Song()
            .WithOrders(0, 1, 2)
            .WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xB, parameter: 2)
            .WithCell(0, row: 0, channel: 1, sample: 0, period: 0, effect: 0xD, parameter: 0x05);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal((2, 5), (playback.Ticks[6].Order, playback.Ticks[6].Row));
    }

    /// <summary>F00 stops the song after its tick; there is no loop.</summary>
    [Fact]
    public void Play_WithF00_StopsWithoutLoop()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 2, channel: 0, sample: 0, period: 0, effect: 0xF, parameter: 0);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal((2 * 6) + 1, playback.Ticks.Count);
        Assert.Equal(PlaybackEnd.Stops, playback.End);
        Assert.Null(playback.LoopTick);
        Assert.Null(playback.LoopStartSeconds);
    }

    /// <summary>E60 marks a loop start and E62 plays the rows twice more; the repeats don't count as a song loop.</summary>
    [Fact]
    public void Play_WithPatternLoop_RepeatsRowsWithoutEndingSong()
    {
        // Arrange
        ModBuilder song = Song()
            .WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xE, parameter: 0x60)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: 0, effect: 0xE, parameter: 0x62);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal((6 + 62) * 6, playback.Ticks.Count);
        Assert.Equal(PlaybackEnd.Loops, playback.End);
        Assert.Equal(0, playback.LoopTick);
    }

    /// <summary>EEx holds the row for x more rows' worth of ticks.</summary>
    [Fact]
    public void Play_WithPatternDelay_HoldsRow()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xE, parameter: 0xE2);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal((64 + 2) * 6, playback.Ticks.Count);
        Assert.All(playback.Ticks.Take(18), t => Assert.Equal(0, t.Row));
        Assert.Equal(1, playback.Ticks[18].Row);
    }

    /// <summary>Wrapping past the end to a row never played isn't a loop: playback ends there, with a note.</summary>
    [Fact]
    public void Play_WhenWrappingToUnplayedRow_StopsWithNote()
    {
        // Arrange
        ModBuilder song = Song()
            .WithOrders(0, 1)
            .WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xD, parameter: 0)
            .WithCell(1, row: 63, channel: 0, sample: 0, period: 0, effect: 0xD, parameter: 0x30);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(PlaybackEnd.Stops, playback.End);
        Assert.Null(playback.LoopTick);
        Assert.Contains(_notes.Messages, m => m.Contains("wraps to order 0 row 30", StringComparison.Ordinal));
    }

    /// <summary>A note starts its sample on the row's first tick at the period's pitch, full volume, hard left on channel 1.</summary>
    [Fact]
    public void Play_WithNote_StartsSampleOnFirstTick()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 0, channel: 0, sample: 1, period: C2);

        // Act
        Playback playback = Play(song);

        // Assert
        ChannelTick first = At(playback, 0);
        Assert.True(first.Playing);
        Assert.Equal(new SampleBlock(0, 0, 1024), first.Started);
        Assert.Equal(new SampleBlock(0, 0, 2), first.Next);
        Assert.Equal(AmigaPitch.FromPeriod(C2, AmigaPitch.PalClockHz), first.Pitch);
        Assert.Equal(1.0, first.Volume);
        Assert.Equal(-1.0, first.Pan);
        Assert.Null(At(playback, 1).Started);
        Assert.True(At(playback, 1).Playing);
    }

    /// <summary>A silent channel 2 isn't playing and is panned hard right.</summary>
    [Fact]
    public void Play_WithSilentSecondChannel_IsNotPlayingAndPannedRight()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 0, channel: 0, sample: 1, period: C2);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.False(At(playback, 0, channel: 1).Playing);
        Assert.Equal(1.0, At(playback, 0, channel: 1).Pan);
    }

    /// <summary>A looping sample plays up to its loop end, then repeats the loop.</summary>
    [Fact]
    public void Play_WithLoopingSample_PlaysToLoopEndThenRepeatsLoop()
    {
        // Arrange
        ModBuilder song = new ModBuilder().WithSample(1, "pad", 1024, loopStart: 256, loopLength: 512).WithCell(0, row: 0, channel: 0, sample: 1, period: C2);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(new SampleBlock(0, 0, 768), At(playback, 0).Started);
        Assert.Equal(new SampleBlock(0, 256, 512), At(playback, 0).Next);
    }

    /// <summary>A loop that starts at 0 ends the first pass at its end too, as in OpenMPT; ProTracker played the whole sample once.</summary>
    [Fact]
    public void Play_WithLoopAtStart_EndsFirstPassAtLoopEnd()
    {
        // Arrange
        ModBuilder song = new ModBuilder().WithSample(1, "pad", 1024, loopStart: 0, loopLength: 512).WithCell(0, row: 0, channel: 0, sample: 1, period: C2);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(new SampleBlock(0, 0, 512), At(playback, 0).Started);
        Assert.Equal(new SampleBlock(0, 0, 512), At(playback, 0).Next);
    }

    /// <summary>
    /// A finetuned note plays OpenMPT's period, measured with libopenmpt (ADR 0060): its finetune's C-1 octave halved for
    /// each octave up, to the quarter (C-3 at +1 is 850 ÷ 4 = 212.5, where ProTracker's table has 213), never below 113.
    /// </summary>
    [Theory]
    [InlineData(C2, 1, 425.0)]
    [InlineData(214, 1, 212.5)]
    [InlineData(127, 1, 126.25)]
    [InlineData(856, -8, 907.0)]
    [InlineData(127, -8, 134.5)]
    [InlineData(113, 0, 113.25)]
    [InlineData(120, 0, 120.0)]
    [InlineData(113, 7, 113.0)]
    [InlineData(120, 7, 114.25)]
    public void Play_WithFinetunedNote_PlaysOpenMptsPeriod(int written, int fineTune, double expected)
    {
        // Arrange
        ModBuilder song = new ModBuilder().WithSample(1, "lead", 1024, fineTune: fineTune).WithCell(0, row: 0, channel: 0, sample: 1, period: written);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(expected, QuarterPeriodAt(playback, 0));
    }

    /// <summary>
    /// Each written note's pitch is kept with the playing instrument's finetune (ADR 0061), also when the cell names no
    /// instrument; the mapper places the DMF's notes there. Rows without a note have none.
    /// </summary>
    [Fact]
    public void Play_WithFinetunedInstrument_KeepsNotePitchesWithFinetune()
    {
        // Arrange
        ModBuilder song = new ModBuilder()
            .WithSample(1, "lead", 1024, fineTune: 1)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 214)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: C2)
            .WithCell(0, row: 2, channel: 1, sample: 1, period: 127);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(212.5, NotePeriodAt(playback, 0, 0));
        Assert.Equal(425, NotePeriodAt(playback, 0, 1));
        Assert.Equal(126.25, NotePeriodAt(playback, 0, 2, channel: 1));
        Assert.Null(NotePeriodAt(playback, 0, 3));
    }

    /// <summary>An E5x on the note's row tunes that note; a tone portamento's note is kept as its target.</summary>
    [Fact]
    public void Play_WithFineTuneEffectsAndPortamento_KeepsTunedAndTargetPitches()
    {
        // Arrange
        ModBuilder song = new ModBuilder()
            .WithSample(1, "lead", 1024)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 214, effect: 0xE, parameter: 0x51)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: C2)
            .WithCell(0, row: 2, channel: 0, sample: 0, period: 856, effect: 0x3, parameter: 0x01)
            .WithCell(0, row: 3, channel: 1, sample: 1, period: C2, effect: 0xE, parameter: 0x58);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(212.5, NotePeriodAt(playback, 0, 0));
        Assert.Equal(425, NotePeriodAt(playback, 0, 1));
        Assert.Equal(850, NotePeriodAt(playback, 0, 2));
        Assert.Equal(453.5, NotePeriodAt(playback, 0, 3, channel: 1));
    }

    /// <summary>
    /// A row played again (E6x) keeps the pitch of its first time, though the second time it plays at another finetune;
    /// rows the song never reaches have none.
    /// </summary>
    [Fact]
    public void Play_WithRowPlayedTwice_KeepsPitchOfFirstTime()
    {
        // Arrange
        ModBuilder song = new ModBuilder()
            .WithSample(1, "lead", 1024)
            .WithSample(2, "bass", 1024, fineTune: -8)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 0)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: C2, effect: 0xE, parameter: 0x60)
            .WithCell(0, row: 2, channel: 0, sample: 2, period: 0, effect: 0xE, parameter: 0x61)
            .WithCell(0, row: 3, channel: 0, sample: 0, period: 0, effect: 0xF, parameter: 0x00)
            .WithCell(1, row: 0, channel: 0, sample: 1, period: C2)
            .WithOrders(0, 1);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(453.5, QuarterPeriodAt(playback, 3 * 6));
        Assert.Equal(C2, NotePeriodAt(playback, 0, 1));
        Assert.Null(NotePeriodAt(playback, 1, 0));
    }

    /// <summary>Cxx sets the volume and Axy slides it on every tick after the first.</summary>
    [Fact]
    public void Play_WithVolumeSlide_SlidesEveryTickAfterFirst()
    {
        // Arrange
        ModBuilder song = Song()
            .WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0xC, parameter: 0x30)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: 0, effect: 0xA, parameter: 0x04);
        double[] expected = [48 / 64.0, 44 / 64.0, 40 / 64.0, 36 / 64.0, 32 / 64.0, 28 / 64.0];

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(expected, Enumerable.Range(6, 6).Select(t => At(playback, t).Volume));
    }

    /// <summary>0xy plays the note, then +x, then +y semitones from the period table, tick by tick.</summary>
    [Fact]
    public void Play_WithArpeggio_CyclesThroughPeriodTable()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0x0, parameter: 0x37);
        int[] expected = [428, 360, 285, 428, 360, 285];

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(expected, Enumerable.Range(0, 6).Select(t => PeriodAt(playback, t)));
    }

    /// <summary>An arpeggio on a finetuned note plays OpenMPT's periods for the finetune, to the quarter (ADR 0060).</summary>
    [Fact]
    public void Play_WithArpeggioOnFinetunedNote_PlaysFinetunesPeriods()
    {
        // Arrange
        ModBuilder song = new ModBuilder().WithSample(1, "lead", 1024, fineTune: 1).WithCell(0, row: 0, channel: 0, sample: 1, period: 214, effect: 0x0, parameter: 0x37);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal([212.5, 178.75, 141.75, 212.5], QuarterPeriods(playback, 0, 4));
    }

    /// <summary>
    /// Past B-3 an arpeggio reads the same finetune's notes again from C-1, 37 notes back, as OpenMPT does (A-3 + 12 is
    /// G#-1, + 7 is E-1, at finetune +1: 535 and 715); ProTracker read on into the next finetune's table.
    /// </summary>
    [Fact]
    public void Play_WithArpeggioPastTopNote_ReadsFinetuneAgainFromC1()
    {
        // Arrange
        ModBuilder song = new ModBuilder().WithSample(1, "lead", 1024, fineTune: 1).WithCell(0, row: 0, channel: 0, sample: 1, period: 127, effect: 0x0, parameter: 0xC7);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal([126.25, 535, 715], QuarterPeriods(playback, 0, 3));
    }

    /// <summary>After a slide an arpeggio snaps its base to the next note up, on its first tick too; the slid period comes back after it.</summary>
    [Fact]
    public void Play_WithArpeggioAfterSlide_SnapsBaseToNoteThenRestoresSlidPeriod()
    {
        // Arrange
        ModBuilder song = Song()
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 214, effect: 0x1, parameter: 0x01)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: 0, effect: 0x0, parameter: 0x37);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal([202, 170, 135, 202, 170, 135, 209], QuarterPeriods(playback, 6, 7));
    }

    /// <summary>1xx slides the period down by xx on every tick after the first and stops at B-3 (113¼ at finetune 0, as in OpenMPT).</summary>
    [Fact]
    public void Play_WithPortamentoUp_StopsAtTopNote()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 0, channel: 0, sample: 1, period: 120, effect: 0x1, parameter: 0x04);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal([120, 116, 113.25, 113.25], QuarterPeriods(playback, 0, 4));
    }

    /// <summary>
    /// Slides stop at the finetune's own B-3 (not below 113) and C-1, and keep a note's quarter; a slide past 0 stops
    /// instead of wrapping as in ProTracker (all measured with libopenmpt, ADR 0060).
    /// </summary>
    [Theory]
    [InlineData(-8, 1, 0xFF, 120.0)]
    [InlineData(-8, 2, 0xFF, 907.0)]
    [InlineData(1, 1, 0xFF, 113.0)]
    [InlineData(1, 2, 0xFF, 850.0)]
    [InlineData(7, 2, 0xFF, 814.0)]
    public void Play_WithLongSlide_StopsAtFinetunesLimit(int fineTune, int effect, int parameter, double expected)
    {
        // Arrange
        ModBuilder song = new ModBuilder()
            .WithSample(1, "lead", 1024, fineTune: fineTune)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 120, effect: effect, parameter: parameter)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: 0, effect: effect, parameter: parameter);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(expected, QuarterPeriodAt(playback, 11));
    }

    /// <summary>A slide keeps the quarter of a finetuned note: A-3 at +1 is 126¼, then 125¼, 124¼.</summary>
    [Fact]
    public void Play_WithSlideOnFinetunedNote_KeepsQuarter()
    {
        // Arrange
        ModBuilder song = new ModBuilder().WithSample(1, "lead", 1024, fineTune: 1).WithCell(0, row: 0, channel: 0, sample: 1, period: 127, effect: 0x1, parameter: 0x01);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal([126.25, 125.25, 124.25], QuarterPeriods(playback, 0, 3));
    }

    /// <summary>3xx slides toward the new note by xx a tick without restarting the sample, and stops on it.</summary>
    [Fact]
    public void Play_WithTonePortamento_SlidesToNoteWithoutRestart()
    {
        // Arrange
        ModBuilder song = Song()
            .WithCell(0, row: 0, channel: 0, sample: 1, period: C2)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: 404, effect: 0x3, parameter: 0x10);
        int[] expected = [428, 412, 404, 404];

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(expected, Enumerable.Range(6, 4).Select(t => PeriodAt(playback, t)));
        Assert.Null(At(playback, 6).Started);
    }

    /// <summary>
    /// 4xy moves the period by OpenMPT's 64-step sine × depth ÷ 16 in quarters, rounded toward 0, starting at 0 on the
    /// note: 12, 25, 37, 49 × 15 ÷ 16 → 11, 23, 34, 45 quarters (ADR 0060).
    /// </summary>
    [Fact]
    public void Play_WithVibrato_FollowsOpenMptsSine()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 0, channel: 0, sample: 1, period: 214, effect: 0x4, parameter: 0x1F);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal([214, 214, 216.75, 219.75, 222.5, 225.25], QuarterPeriods(playback, 0, 6));
    }

    /// <summary>Vibrato's ramp and square waves (E4x) use the same steps: the ramp rises by 4 a step, the square is ±127.</summary>
    [Theory]
    [InlineData(1, new[] { 214, 214, 215.75, 217.75, 219.5, 221.5 })]
    [InlineData(2, new[] { 214, 243.75, 243.75, 243.75, 243.75, 243.75 })]
    public void Play_WithVibratoWave_UsesOpenMptsSteps(int wave, double[] expected)
    {
        // Arrange
        ModBuilder song = Song()
            .WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xE, parameter: 0x40 | wave)
            .WithCell(0, row: 1, channel: 0, sample: 1, period: 214, effect: 0x4, parameter: 0x2F);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(expected, QuarterPeriods(playback, 6, 6));
    }

    /// <summary>Vibrato isn't held at 113 the way Paula's period is: on B-3 it dips to 84¾, as in OpenMPT.</summary>
    [Fact]
    public void Play_WithVibratoOnTopNote_GoesPastIt()
    {
        // Arrange
        ModBuilder song = Song()
            .WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xF, parameter: 0x0F)
            .WithCell(0, row: 1, channel: 0, sample: 1, period: 113, effect: 0x4, parameter: 0xFF);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(84.75, QuarterPeriodAt(playback, 15 + 4));
    }

    /// <summary>9xx starts the sample xx × 256 frames in.</summary>
    [Fact]
    public void Play_WithSampleOffset_StartsSampleThatFarIn()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0x9, parameter: 0x02);

        // Act
        Playback playback = Play(song);

        // Assert
        SampleBlock started = Assert.IsType<SampleBlock>(At(playback, 0).Started);
        Assert.Equal((0, 512, 512), (started.Sample, started.StartFrame, started.Frames));
    }

    /// <summary>ECx silences the note on tick x.</summary>
    [Fact]
    public void Play_WithNoteCut_SilencesOnItsTick()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0xE, parameter: 0xC2);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(1.0, At(playback, 1).Volume);
        Assert.Equal(0.0, At(playback, 2).Volume);
    }

    /// <summary>EDx starts the note on tick x instead of tick 0.</summary>
    [Fact]
    public void Play_WithNoteDelay_StartsNoteOnItsTick()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0xE, parameter: 0xD2);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Null(At(playback, 0).Started);
        Assert.Null(At(playback, 1).Started);
        Assert.NotNull(At(playback, 2).Started);
    }

    /// <summary>E9x restarts the note every x ticks.</summary>
    [Fact]
    public void Play_WithRetrigger_RestartsNoteEveryXTicks()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0xE, parameter: 0x92);
        bool[] expected = [true, false, true, false, true, false];

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(expected, Enumerable.Range(0, 6).Select(t => At(playback, t).Started is not null));
    }

    /// <summary>A sample number without a note swaps the sound when the playing part ends, without restarting it.</summary>
    [Fact]
    public void Play_WithSampleWithoutNote_SwapsLoopWithoutRestart()
    {
        // Arrange
        ModBuilder song = Song()
            .WithSample(2, "pad", 512, volume: 32, loopStart: 0, loopLength: 512)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: C2)
            .WithCell(0, row: 1, channel: 0, sample: 2, period: 0);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Null(At(playback, 6).Started);
        Assert.Equal(new SampleBlock(1, 0, 512), At(playback, 6).Next);
        Assert.Equal(0.5, At(playback, 6).Volume);
    }

    /// <summary>E00 turns the LED filter on, E01 off.</summary>
    [Fact]
    public void Play_WithLedCommands_SwitchesFilter()
    {
        // Arrange
        ModBuilder song = Song()
            .WithCell(0, row: 1, channel: 0, sample: 0, period: 0, effect: 0xE, parameter: 0x00)
            .WithCell(0, row: 2, channel: 0, sample: 0, period: 0, effect: 0xE, parameter: 0x01);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.False(playback.Ticks[0].LowPassFilter);
        Assert.True(playback.Ticks[6].LowPassFilter);
        Assert.False(playback.Ticks[12].LowPassFilter);
    }

    /// <summary>EFx (invert loop) isn't reproduced; one note says so.</summary>
    [Fact]
    public void Play_WithInvertLoop_Notes()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0xE, parameter: 0xF3);

        // Act
        Play(song);

        // Assert
        Assert.Contains(_notes.Messages, m => m.Contains("EFx (invert loop)", StringComparison.Ordinal));
    }

    /// <summary>
    /// A note beyond ProTracker's range plays at its own pitch, snapped to OpenMPT's nearest note (57 → A#4's 56, 1700 →
    /// C-0's 1712), and one note says so (ADR 0055).
    /// </summary>
    [Fact]
    public void Play_WithNotesBeyondRange_SnapsToOpenMptsTableAndNotes()
    {
        // Arrange
        ModBuilder song = Song()
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 57)
            .WithCell(0, row: 0, channel: 1, sample: 1, period: 1700)
            .WithCell(0, row: 1, channel: 0, sample: 1, period: C2);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(56, PeriodAt(playback, 0));
        Assert.Equal(1712, PeriodAt(playback, 0, channel: 1));
        Assert.Equal(C2, PeriodAt(playback, 6));
        Assert.Contains(_notes.Messages, m => m.Contains("2 notes lie beyond ProTracker's range", StringComparison.Ordinal));
    }

    /// <summary>
    /// Beyond the range, finetune follows the same rule as within it (ADR 0060): C-0 at −8 is C-1's 907 doubled, 1814;
    /// A#-4 (56) at +1 is ProTracker's B-1 at +1, 450, ÷ 8 = 56.25.
    /// </summary>
    [Theory]
    [InlineData(1712, -8, 1814.0)]
    [InlineData(56, 1, 56.25)]
    public void Play_WithFinetunedNoteBeyondRange_TakesFinetune(int written, int fineTune, double expected)
    {
        // Arrange
        ModBuilder song = new ModBuilder().WithSample(1, "lead", 1024, fineTune: fineTune).WithCell(0, row: 0, channel: 0, sample: 1, period: written);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(expected, QuarterPeriodAt(playback, 0));
    }

    /// <summary>In a song with notes beyond the range, an upward slide runs on to period 14 instead of stopping at 113.</summary>
    [Fact]
    public void Play_WithNotesBeyondRangeAndSlideUp_StopsAtPeriod14()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 0, channel: 0, sample: 1, period: 30, effect: 0x1, parameter: 0x10);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal([30, 14, 14], Enumerable.Range(0, 3).Select(t => PeriodAt(playback, t)));
    }

    /// <summary>In a song with notes beyond the range, a downward slide runs on to period 3424 instead of stopping at 856.</summary>
    [Fact]
    public void Play_WithNotesBeyondRangeAndSlideDown_StopsAtPeriod3424()
    {
        // Arrange
        ModBuilder song = Song()
            .WithCell(0, row: 0, channel: 1, sample: 1, period: 1712, effect: 0x2, parameter: 0xFF)
            .WithCell(0, row: 1, channel: 1, sample: 0, period: 0, effect: 0x2, parameter: 0xFF);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal([1712, 1967, 3242, 3424, 3424], new[] { 0, 1, 7, 8, 9 }.Select(t => PeriodAt(playback, t, channel: 1)));
    }

    /// <summary>A song with every note in range keeps ProTracker's limits and no note is added.</summary>
    [Fact]
    public void Play_WithNotesInRange_KeepsProTrackersLimitsWithoutNote()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 0, channel: 0, sample: 1, period: 856, effect: 0x2, parameter: 0x10);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal([856, 856], Enumerable.Range(0, 2).Select(t => PeriodAt(playback, t)));
        Assert.DoesNotContain(_notes.Messages, m => m.Contains("beyond ProTracker's range", StringComparison.Ordinal));
    }

    /// <summary>Above ProTracker's top note an arpeggio moves along OpenMPT's six-octave table: 56 → 47 (+3) → 37 (+7).</summary>
    [Fact]
    public void Play_WithArpeggioBeyondRange_UsesOpenMptsTable()
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 0, channel: 0, sample: 1, period: 56, effect: 0x0, parameter: 0x37);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal([56, 47, 37, 56], Enumerable.Range(0, 4).Select(t => PeriodAt(playback, t)));
    }

    /// <summary>Past B-5 an arpeggio goes on up by the same rule (G#-5 + 7 is 22.5), and stops at period 14.</summary>
    [Theory]
    [InlineData(33, 0x37, new[] { 33, 28, 22.5 })]
    [InlineData(30, 0x1F, new[] { 30, 28, 14.0 })]
    public void Play_WithArpeggioPastSixOctaves_GoesOnUpToPeriod14(int period, int parameter, double[] expected)
    {
        // Arrange
        ModBuilder song = Song().WithCell(0, row: 0, channel: 0, sample: 1, period: period, effect: 0x0, parameter: parameter);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(expected, QuarterPeriods(playback, 0, 3));
    }

    /// <summary>A tone portamento reaches a note beyond the range, and glissando snaps it to OpenMPT's notes there (101, 85, 71).</summary>
    [Fact]
    public void Play_WithGlissandoPortamentoBeyondRange_SnapsToOpenMptsNotes()
    {
        // Arrange
        ModBuilder song = Song()
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 120, effect: 0xE, parameter: 0x31)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: 56, effect: 0x3, parameter: 0x10);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal([120, 101, 85, 71, 56, 56], Enumerable.Range(6, 6).Select(t => PeriodAt(playback, t)));
    }

    /// <summary>A tone portamento on a finetuned note slides to OpenMPT's period for the note (C-3 at −3: 218.75).</summary>
    [Fact]
    public void Play_WithPortamentoOnFinetunedNote_SlidesToFinetunesPeriod()
    {
        // Arrange
        ModBuilder song = new ModBuilder()
            .WithSample(1, "lead", 1024, fineTune: -3)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: C2)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: 214, effect: 0x3, parameter: 0xFF);

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal([437.5, 218.75, 218.75], QuarterPeriods(playback, 6, 3));
    }

    /// <summary>Ultimate Soundtracker rules: speed 6 always (Fxx doesn't exist) and the tempo from byte 471.</summary>
    [Fact]
    public void Play_WithUltimateSoundtrackerRules_KeepsSpeedSixAtByte471Tempo()
    {
        // Arrange
        ModBuilder song = UltimateSoundtrackerSong();
        double bpm = ModPlayers.UltimateSoundtrackerBpm(100) ?? 0;

        // Act
        Playback playback = Play(song);

        // Assert
        Assert.Equal(Ticks64Rows, playback.Ticks.Count);
        Assert.Equal(Ticks64Rows / (bpm * 2 / 5), playback.DurationSeconds, 1e-9);
    }

    /// <summary>Under Ultimate Soundtracker rules 1xy is an arpeggio.</summary>
    [Fact]
    public void Play_WithUltimateSoundtrackerRules_Plays1xyAsArpeggio()
    {
        // Act
        Playback playback = Play(UltimateSoundtrackerSong());

        // Assert
        Assert.Equal(360, PeriodAt(playback, 1));
    }

    /// <summary>Under Ultimate Soundtracker rules a sample starts at its loop start; with its loop now at the start, it ends at the loop end (OpenMPT).</summary>
    [Fact]
    public void Play_WithUltimateSoundtrackerRules_StartsSampleAtLoopStart()
    {
        // Act
        Playback playback = Play(UltimateSoundtrackerSong());

        // Assert
        Assert.Equal(new SampleBlock(0, 0, 512), At(playback, 0).Started);
        Assert.Equal(new SampleBlock(0, 0, 512), At(playback, 0).Next);
    }

    /// <summary>A clock or tick rate of 0 is a caller bug and throws.</summary>
    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(AmigaPitch.PalClockHz, 0.0)]
    public void Play_WithZeroClockOrTickRate_Throws(double clockHz, double tickRateScale)
    {
        // Arrange
        ModModule module = ModReader.Read(Song().Build(), new ConversionNotes(Logger.None));

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => ModTickPlayer.Play(module, clockHz, tickRateScale, _notes));
    }

    /// <summary>A builder with sample 1 ("lead", 1024 frames, full volume).</summary>
    private static ModBuilder Song() => new ModBuilder().WithSample(1, "lead", 1024);

    /// <summary>A 15-sample song with a looped pad, a 1xy on its first note and an Fxx that Ultimate Soundtracker doesn't have.</summary>
    private static ModBuilder UltimateSoundtrackerSong() => new ModBuilder { Soundtracker = true, RestartPosition = 100 }
        .WithSample(1, "pad", 1024, loopStartWords: 256, loopLength: 512)
        .WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0x1, parameter: 0x37)
        .WithCell(0, row: 1, channel: 0, sample: 0, period: 0, effect: 0xF, parameter: 0x03);

    /// <summary>Reads the built module and plays it on a PAL Amiga; the player's own notes go to this test's notes.</summary>
    private Playback Play(ModBuilder builder, ModPlayer? player = null, double tickRateScale = 1.0)
    {
        ModModule module = ModReader.Read(builder.Build(), new ConversionNotes(Logger.None), player);
        return ModTickPlayer.Play(module, AmigaPitch.PalClockHz, tickRateScale, _notes);
    }

    /// <summary>Channel <paramref name="channel"/> on tick <paramref name="tick"/>.</summary>
    private static ChannelTick At(Playback playback, int tick, int channel = 0) => playback.Ticks[tick].Channels[channel];

    /// <summary>The Amiga period a channel plays on a tick (the inverse of its pitch).</summary>
    private static int PeriodAt(Playback playback, int tick, int channel = 0) =>
        (int)Math.Round(AmigaPitch.PeriodOf(At(playback, tick, channel).Pitch, AmigaPitch.PalClockHz));

    /// <summary>The period a channel plays on a tick to the quarter, the unit OpenMPT keeps periods in (ADR 0060).</summary>
    private static double QuarterPeriodAt(Playback playback, int tick, int channel = 0) =>
        Math.Round(AmigaPitch.PeriodOf(At(playback, tick, channel).Pitch, AmigaPitch.PalClockHz) * 4) / 4;

    /// <summary>Periods to the quarter on ticks <paramref name="first"/> to <paramref name="first"/> + <paramref name="count"/> − 1.</summary>
    private static double[] QuarterPeriods(Playback playback, int first, int count, int channel = 0) =>
        [.. Enumerable.Range(first, count).Select(t => QuarterPeriodAt(playback, t, channel))];

    /// <summary>The period a written note is kept at in <see cref="Playback.NotePitches"/>, to the quarter, or null when it is missing.</summary>
    private static double? NotePeriodAt(Playback playback, int order, int row, int channel = 0) =>
        playback.NotePitches.TryGetValue(new NotePosition(order, row, channel), out NotePitch pitch)
            ? Math.Round(AmigaPitch.PeriodOf(pitch, AmigaPitch.PalClockHz) * 4) / 4
            : null;
}
