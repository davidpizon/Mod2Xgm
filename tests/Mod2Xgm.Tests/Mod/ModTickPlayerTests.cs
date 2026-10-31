using Mod2Xgm.Mod;
using Mod2Xgm.Model;
using Serilog.Core;

namespace Mod2Xgm.Tests.Mod;

/// <summary>
/// Tests for the tick player (plan T3.4), a port of ProTracker 2's replayer: song length and flow, the loop point, and
/// tick-by-tick channel state, on small songs whose results are worked out by hand from ProTracker's rules.
/// </summary>
public sealed class ModTickPlayerTests
{
    private const int C2 = 428;
    private const int Ticks64Rows = 64 * 6;

    /// <summary>Reads the built module and plays it on a PAL Amiga, returning the playback and the player's own notes.</summary>
    private static (Playback Playback, ConversionNotes Notes) Play(ModBuilder builder, ModPlayer? player = null, double tickRateScale = 1.0)
    {
        ModModule module = ModReader.Read(builder.Build(), new ConversionNotes(Logger.None), player);
        ConversionNotes notes = new(Logger.None);
        return (ModTickPlayer.Play(module, AmigaPitch.PalClockHz, tickRateScale, notes), notes);
    }

    /// <summary>A builder with sample 1 ("lead", 1024 frames, full volume).</summary>
    private static ModBuilder Song() => new ModBuilder().WithSample(1, "lead", 1024);

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

    /// <summary>ProTracker's CIA tick rate: 709 379 Hz / (1 773 447 / BPM + 1), so 125 BPM is a little under 50 Hz.</summary>
    [Fact]
    public void CiaTickRate()
    {
        Assert.Equal(709_379.0 / 14_188, ModTickPlayer.CiaTickHz(125));
        Assert.Equal(709_379.0 / 7_094, ModTickPlayer.CiaTickHz(250));
    }

    /// <summary>One empty pattern: 64 rows of 6 ticks at 125 BPM, then it wraps to the start and loops from tick 0.</summary>
    [Fact]
    public void OnePatternLoopsToTheStart()
    {
        (Playback playback, _) = Play(Song());

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
    public void SpeedChangesTheTicksPerRow()
    {
        (Playback playback, _) = Play(Song().WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xF, parameter: 3));

        Assert.Equal(64 * 3, playback.Ticks.Count);
    }

    /// <summary>Fxx from 0x20 sets the BPM one tick late, as ProTracker's CIA timer does.</summary>
    [Fact]
    public void TempoChangesOneTickLate()
    {
        (Playback playback, _) = Play(Song().WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xF, parameter: 250));

        Assert.Equal(1 / ModTickPlayer.CiaTickHz(125), playback.Ticks[0].DurationSeconds, 1e-12);
        Assert.Equal(1 / ModTickPlayer.CiaTickHz(250), playback.Ticks[1].DurationSeconds, 1e-12);
        Assert.Equal((1 / ModTickPlayer.CiaTickHz(125)) + ((Ticks64Rows - 1) / ModTickPlayer.CiaTickHz(250)), playback.DurationSeconds, 1e-9);
    }

    /// <summary>An NTSC Amiga runs 60 ticks a second at 125 BPM: the same ticks, 6/5 as fast.</summary>
    [Fact]
    public void NtscTicksAreFaster()
    {
        (Playback pal, _) = Play(Song());
        (Playback ntsc, _) = Play(Song(), tickRateScale: 1.2);

        Assert.Equal(pal.Ticks.Count, ntsc.Ticks.Count);
        Assert.Equal(pal.DurationSeconds / 1.2, ntsc.DurationSeconds, 1e-9);
    }

    /// <summary>Bxx jumps after the row; jumping back to a played row ends one pass and marks the loop there.</summary>
    [Fact]
    public void PositionJumpBackLoops()
    {
        (Playback playback, _) = Play(Song()
            .WithOrders(0, 1, 2)
            .WithCell(1, row: 0, channel: 0, sample: 0, period: 0, effect: 0xB, parameter: 0));

        Assert.Equal(Ticks64Rows + 6, playback.Ticks.Count);
        Assert.Equal(0, playback.LoopTick);
        Assert.DoesNotContain(playback.Ticks, t => t.Order == 2);
    }

    /// <summary>A jump back to the second order loops from that order's first tick, not the song's start.</summary>
    [Fact]
    public void LoopPointCanBeMidSong()
    {
        (Playback playback, _) = Play(Song()
            .WithOrders(0, 1)
            .WithCell(1, row: 63, channel: 0, sample: 0, period: 0, effect: 0xB, parameter: 1));

        Assert.Equal(2 * Ticks64Rows, playback.Ticks.Count);
        Assert.Equal(Ticks64Rows, playback.LoopTick);
        Assert.Equal(Ticks64Rows / ModTickPlayer.CiaTickHz(125), playback.LoopStartSeconds ?? 0, 1e-9);
    }

    /// <summary>Dxx breaks to a row of the next order (written in decimal); a row above 63 means row 0.</summary>
    [Theory]
    [InlineData(0x10, 10)]
    [InlineData(0x70, 0)]
    public void PatternBreakSkipsRows(int parameter, int targetRow)
    {
        (Playback playback, _) = Play(Song()
            .WithOrders(0, 1)
            .WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xD, parameter: parameter));

        Assert.Equal((1 + 64 - targetRow) * 6, playback.Ticks.Count);
        Assert.Equal((1, targetRow), (playback.Ticks[6].Order, playback.Ticks[6].Row));
    }

    /// <summary>Bxx and Dxx on the same row go to that row of that order.</summary>
    [Fact]
    public void JumpAndBreakTogether()
    {
        (Playback playback, _) = Play(Song()
            .WithOrders(0, 1, 2)
            .WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xB, parameter: 2)
            .WithCell(0, row: 0, channel: 1, sample: 0, period: 0, effect: 0xD, parameter: 0x05));

        Assert.Equal((2, 5), (playback.Ticks[6].Order, playback.Ticks[6].Row));
    }

    /// <summary>F00 stops the song after its tick; there is no loop.</summary>
    [Fact]
    public void F00Stops()
    {
        (Playback playback, _) = Play(Song().WithCell(0, row: 2, channel: 0, sample: 0, period: 0, effect: 0xF, parameter: 0));

        Assert.Equal((2 * 6) + 1, playback.Ticks.Count);
        Assert.Equal(PlaybackEnd.Stops, playback.End);
        Assert.Null(playback.LoopTick);
        Assert.Null(playback.LoopStartSeconds);
    }

    /// <summary>E60 marks a loop start and E62 plays the rows twice more; the repeats don't count as a song loop.</summary>
    [Fact]
    public void PatternLoopRepeatsRows()
    {
        (Playback playback, _) = Play(Song()
            .WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xE, parameter: 0x60)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: 0, effect: 0xE, parameter: 0x62));

        Assert.Equal((6 + 62) * 6, playback.Ticks.Count);
        Assert.Equal(PlaybackEnd.Loops, playback.End);
        Assert.Equal(0, playback.LoopTick);
    }

    /// <summary>EEx holds the row for x more rows' worth of ticks.</summary>
    [Fact]
    public void PatternDelayHoldsTheRow()
    {
        (Playback playback, _) = Play(Song().WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xE, parameter: 0xE2));

        Assert.Equal((64 + 2) * 6, playback.Ticks.Count);
        Assert.All(playback.Ticks.Take(18), t => Assert.Equal(0, t.Row));
        Assert.Equal(1, playback.Ticks[18].Row);
    }

    /// <summary>Wrapping past the end to a row never played isn't a loop: playback ends there, with a note.</summary>
    [Fact]
    public void WrappingToAnUnplayedRowStops()
    {
        (Playback playback, ConversionNotes notes) = Play(Song()
            .WithOrders(0, 1)
            .WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xD, parameter: 0)
            .WithCell(1, row: 63, channel: 0, sample: 0, period: 0, effect: 0xD, parameter: 0x30));

        Assert.Equal(PlaybackEnd.Stops, playback.End);
        Assert.Null(playback.LoopTick);
        Assert.Contains(notes.Messages, m => m.Contains("wraps to order 0 row 30", StringComparison.Ordinal));
    }

    /// <summary>A note starts its sample on the row's first tick at the period's pitch, full volume, hard left on channel 1.</summary>
    [Fact]
    public void ANoteStartsItsSample()
    {
        (Playback playback, _) = Play(Song().WithCell(0, row: 0, channel: 0, sample: 1, period: C2));

        ChannelTick first = At(playback, 0);
        Assert.True(first.Playing);
        Assert.Equal(new SampleBlock(0, 0, 1024), first.Started);
        Assert.Equal(new SampleBlock(0, 0, 2), first.Next);
        Assert.Equal(AmigaPitch.FromPeriod(C2, AmigaPitch.PalClockHz), first.Pitch);
        Assert.Equal(1.0, first.Volume);
        Assert.Equal(-1.0, first.Pan);
        Assert.Null(At(playback, 1).Started);
        Assert.True(At(playback, 1).Playing);
        Assert.False(At(playback, 0, channel: 1).Playing);
        Assert.Equal(1.0, At(playback, 0, channel: 1).Pan);
    }

    /// <summary>A looping sample plays up to its loop end, then repeats the loop.</summary>
    [Fact]
    public void LoopingSamplesRepeatTheirLoop()
    {
        (Playback playback, _) = Play(new ModBuilder()
            .WithSample(1, "pad", 1024, loopStart: 256, loopLength: 512)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: C2));

        Assert.Equal(new SampleBlock(0, 0, 768), At(playback, 0).Started);
        Assert.Equal(new SampleBlock(0, 256, 512), At(playback, 0).Next);
    }

    /// <summary>A loop that starts at 0 ends the first pass at its end too, as in OpenMPT; ProTracker played the whole sample once.</summary>
    [Fact]
    public void ALoopAtTheStartEndsAtItsEndTheFirstTime()
    {
        (Playback playback, _) = Play(new ModBuilder()
            .WithSample(1, "pad", 1024, loopStart: 0, loopLength: 512)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: C2));

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
    public void FinetunedNotesPlayOpenMptsPeriod(int written, int fineTune, double expected)
    {
        (Playback playback, _) = Play(new ModBuilder()
            .WithSample(1, "lead", 1024, fineTune: fineTune)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: written));

        Assert.Equal(expected, QuarterPeriodAt(playback, 0));
    }

    /// <summary>The period a written note is kept at in <see cref="Playback.NotePitches"/>, to the quarter, or null when it is missing.</summary>
    private static double? NotePeriodAt(Playback playback, int order, int row, int channel = 0) =>
        playback.NotePitches.TryGetValue(new NotePosition(order, row, channel), out NotePitch pitch)
            ? Math.Round(AmigaPitch.PeriodOf(pitch, AmigaPitch.PalClockHz) * 4) / 4
            : null;

    /// <summary>
    /// Each written note's pitch is kept with the playing instrument's finetune (ADR 0061), also when the cell names no
    /// instrument; the mapper places the DMF's notes there.
    /// </summary>
    [Fact]
    public void NotePitchesHaveTheInstrumentsFinetune()
    {
        (Playback playback, _) = Play(new ModBuilder()
            .WithSample(1, "lead", 1024, fineTune: 1)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 214)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: C2)
            .WithCell(0, row: 2, channel: 1, sample: 1, period: 127));

        Assert.Equal(212.5, NotePeriodAt(playback, 0, 0));
        Assert.Equal(425, NotePeriodAt(playback, 0, 1));
        Assert.Equal(126.25, NotePeriodAt(playback, 0, 2, channel: 1));
        Assert.Null(NotePeriodAt(playback, 0, 3));
    }

    /// <summary>An E5x on the note's row tunes that note; a tone portamento's note is kept as its target.</summary>
    [Fact]
    public void NotePitchesFollowFineTuneEffectsAndPortamentoTargets()
    {
        (Playback playback, _) = Play(new ModBuilder()
            .WithSample(1, "lead", 1024)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 214, effect: 0xE, parameter: 0x51)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: C2)
            .WithCell(0, row: 2, channel: 0, sample: 0, period: 856, effect: 0x3, parameter: 0x01)
            .WithCell(0, row: 3, channel: 1, sample: 1, period: C2, effect: 0xE, parameter: 0x58));

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
    public void NotePitchesKeepTheFirstTimeARowPlays()
    {
        (Playback playback, _) = Play(new ModBuilder()
            .WithSample(1, "lead", 1024)
            .WithSample(2, "bass", 1024, fineTune: -8)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 0)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: C2, effect: 0xE, parameter: 0x60)
            .WithCell(0, row: 2, channel: 0, sample: 2, period: 0, effect: 0xE, parameter: 0x61)
            .WithCell(0, row: 3, channel: 0, sample: 0, period: 0, effect: 0xF, parameter: 0x00)
            .WithCell(1, row: 0, channel: 0, sample: 1, period: C2)
            .WithOrders(0, 1));

        Assert.Equal(453.5, QuarterPeriodAt(playback, 3 * 6));
        Assert.Equal(C2, NotePeriodAt(playback, 0, 1));
        Assert.Null(NotePeriodAt(playback, 1, 0));
    }

    /// <summary>Cxx sets the volume and Axy slides it on every tick after the first.</summary>
    [Fact]
    public void VolumeSlidesEveryTickAfterTheFirst()
    {
        (Playback playback, _) = Play(Song()
            .WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0xC, parameter: 0x30)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: 0, effect: 0xA, parameter: 0x04));

        double[] expected = [48 / 64.0, 44 / 64.0, 40 / 64.0, 36 / 64.0, 32 / 64.0, 28 / 64.0];
        Assert.Equal(expected, Enumerable.Range(6, 6).Select(t => At(playback, t).Volume));
    }

    /// <summary>0xy plays the note, then +x, then +y semitones from the period table, tick by tick.</summary>
    [Fact]
    public void ArpeggioCyclesThroughTheTable()
    {
        (Playback playback, _) = Play(Song().WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0x0, parameter: 0x37));

        int[] expected = [428, 360, 285, 428, 360, 285];
        Assert.Equal(expected, Enumerable.Range(0, 6).Select(t => PeriodAt(playback, t)));
    }

    /// <summary>An arpeggio on a finetuned note plays OpenMPT's periods for the finetune, to the quarter (ADR 0060).</summary>
    [Fact]
    public void ArpeggioPlaysTheFinetunesNotes()
    {
        (Playback playback, _) = Play(new ModBuilder()
            .WithSample(1, "lead", 1024, fineTune: 1)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 214, effect: 0x0, parameter: 0x37));

        Assert.Equal([212.5, 178.75, 141.75, 212.5], QuarterPeriods(playback, 0, 4));
    }

    /// <summary>
    /// Past B-3 an arpeggio reads the same finetune's notes again from C-1, 37 notes back, as OpenMPT does (A-3 + 12 is
    /// G#-1, + 7 is E-1, at finetune +1: 535 and 715); ProTracker read on into the next finetune's table.
    /// </summary>
    [Fact]
    public void ArpeggioPastTheTopReadsTheFinetuneAgain()
    {
        (Playback playback, _) = Play(new ModBuilder()
            .WithSample(1, "lead", 1024, fineTune: 1)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 127, effect: 0x0, parameter: 0xC7));

        Assert.Equal([126.25, 535, 715], QuarterPeriods(playback, 0, 3));
    }

    /// <summary>After a slide an arpeggio snaps its base to the next note up, on its first tick too; the slid period comes back after it.</summary>
    [Fact]
    public void ArpeggioSnapsASlidPeriodToANote()
    {
        (Playback playback, _) = Play(Song()
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 214, effect: 0x1, parameter: 0x01)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: 0, effect: 0x0, parameter: 0x37));

        Assert.Equal([202, 170, 135, 202, 170, 135, 209], QuarterPeriods(playback, 6, 7));
    }

    /// <summary>1xx slides the period down by xx on every tick after the first and stops at B-3 (113¼ at finetune 0, as in OpenMPT).</summary>
    [Fact]
    public void PortamentoUpStopsAtTheTopNote()
    {
        (Playback playback, _) = Play(Song().WithCell(0, row: 0, channel: 0, sample: 1, period: 120, effect: 0x1, parameter: 0x04));

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
    public void SlidesStopAtTheFinetunesLimits(int fineTune, int effect, int parameter, double expected)
    {
        (Playback playback, _) = Play(new ModBuilder()
            .WithSample(1, "lead", 1024, fineTune: fineTune)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 120, effect: effect, parameter: parameter)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: 0, effect: effect, parameter: parameter));

        Assert.Equal(expected, QuarterPeriodAt(playback, 11));
    }

    /// <summary>A slide keeps the quarter of a finetuned note: A-3 at +1 is 126¼, then 125¼, 124¼.</summary>
    [Fact]
    public void SlidesKeepTheQuarter()
    {
        (Playback playback, _) = Play(new ModBuilder()
            .WithSample(1, "lead", 1024, fineTune: 1)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 127, effect: 0x1, parameter: 0x01));

        Assert.Equal([126.25, 125.25, 124.25], QuarterPeriods(playback, 0, 3));
    }

    /// <summary>3xx slides toward the new note by xx a tick without restarting the sample, and stops on it.</summary>
    [Fact]
    public void TonePortamentoSlidesToTheNote()
    {
        (Playback playback, _) = Play(Song()
            .WithCell(0, row: 0, channel: 0, sample: 1, period: C2)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: 404, effect: 0x3, parameter: 0x10));

        int[] expected = [428, 412, 404, 404];
        Assert.Equal(expected, Enumerable.Range(6, 4).Select(t => PeriodAt(playback, t)));
        Assert.Null(At(playback, 6).Started);
    }

    /// <summary>
    /// 4xy moves the period by OpenMPT's 64-step sine × depth ÷ 16 in quarters, rounded toward 0, starting at 0 on the
    /// note: 12, 25, 37, 49 × 15 ÷ 16 → 11, 23, 34, 45 quarters (ADR 0060).
    /// </summary>
    [Fact]
    public void VibratoFollowsOpenMptsSine()
    {
        (Playback playback, _) = Play(Song().WithCell(0, row: 0, channel: 0, sample: 1, period: 214, effect: 0x4, parameter: 0x1F));

        Assert.Equal([214, 214, 216.75, 219.75, 222.5, 225.25], QuarterPeriods(playback, 0, 6));
    }

    /// <summary>Vibrato's ramp and square waves (E4x) use the same steps: the ramp rises by 4 a step, the square is ±127.</summary>
    [Theory]
    [InlineData(1, new[] { 214, 214, 215.75, 217.75, 219.5, 221.5 })]
    [InlineData(2, new[] { 214, 243.75, 243.75, 243.75, 243.75, 243.75 })]
    public void VibratoWavesUseOpenMptsSteps(int wave, double[] expected)
    {
        (Playback playback, _) = Play(Song()
            .WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xE, parameter: 0x40 | wave)
            .WithCell(0, row: 1, channel: 0, sample: 1, period: 214, effect: 0x4, parameter: 0x2F));

        Assert.Equal(expected, QuarterPeriods(playback, 6, 6));
    }

    /// <summary>Vibrato isn't held at 113 the way Paula's period is: on B-3 it dips to 84¾, as in OpenMPT.</summary>
    [Fact]
    public void VibratoGoesPastTheTopNote()
    {
        (Playback playback, _) = Play(Song()
            .WithCell(0, row: 0, channel: 0, sample: 0, period: 0, effect: 0xF, parameter: 0x0F)
            .WithCell(0, row: 1, channel: 0, sample: 1, period: 113, effect: 0x4, parameter: 0xFF));

        Assert.Equal(84.75, QuarterPeriodAt(playback, 15 + 4));
    }

    /// <summary>9xx starts the sample xx × 256 frames in.</summary>
    [Fact]
    public void SampleOffsetSkipsIn()
    {
        (Playback playback, _) = Play(Song().WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0x9, parameter: 0x02));

        SampleBlock started = Assert.IsType<SampleBlock>(At(playback, 0).Started);
        Assert.Equal((0, 512, 512), (started.Sample, started.StartFrame, started.Frames));
    }

    /// <summary>ECx silences the note on tick x.</summary>
    [Fact]
    public void NoteCutSilencesOnItsTick()
    {
        (Playback playback, _) = Play(Song().WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0xE, parameter: 0xC2));

        Assert.Equal(1.0, At(playback, 1).Volume);
        Assert.Equal(0.0, At(playback, 2).Volume);
    }

    /// <summary>EDx starts the note on tick x instead of tick 0.</summary>
    [Fact]
    public void NoteDelayStartsLater()
    {
        (Playback playback, _) = Play(Song().WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0xE, parameter: 0xD2));

        Assert.Null(At(playback, 0).Started);
        Assert.Null(At(playback, 1).Started);
        Assert.NotNull(At(playback, 2).Started);
    }

    /// <summary>E9x restarts the note every x ticks.</summary>
    [Fact]
    public void RetriggerRestartsTheNote()
    {
        (Playback playback, _) = Play(Song().WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0xE, parameter: 0x92));

        bool[] expected = [true, false, true, false, true, false];
        Assert.Equal(expected, Enumerable.Range(0, 6).Select(t => At(playback, t).Started is not null));
    }

    /// <summary>A sample number without a note swaps the sound when the playing part ends, without restarting it.</summary>
    [Fact]
    public void SampleWithoutNoteSwapsTheLoop()
    {
        (Playback playback, _) = Play(Song()
            .WithSample(2, "pad", 512, volume: 32, loopStart: 0, loopLength: 512)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: C2)
            .WithCell(0, row: 1, channel: 0, sample: 2, period: 0));

        Assert.Null(At(playback, 6).Started);
        Assert.Equal(new SampleBlock(1, 0, 512), At(playback, 6).Next);
        Assert.Equal(0.5, At(playback, 6).Volume);
    }

    /// <summary>E00 turns the LED filter on, E01 off.</summary>
    [Fact]
    public void FilterFollowsLedCommands()
    {
        (Playback playback, _) = Play(Song()
            .WithCell(0, row: 1, channel: 0, sample: 0, period: 0, effect: 0xE, parameter: 0x00)
            .WithCell(0, row: 2, channel: 0, sample: 0, period: 0, effect: 0xE, parameter: 0x01));

        Assert.False(playback.Ticks[0].LowPassFilter);
        Assert.True(playback.Ticks[6].LowPassFilter);
        Assert.False(playback.Ticks[12].LowPassFilter);
    }

    /// <summary>EFx (invert loop) isn't reproduced; one note says so.</summary>
    [Fact]
    public void InvertLoopIsNoted()
    {
        (_, ConversionNotes notes) = Play(Song().WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0xE, parameter: 0xF3));

        Assert.Contains(notes.Messages, m => m.Contains("EFx (invert loop)", StringComparison.Ordinal));
    }

    /// <summary>
    /// A note beyond ProTracker's range plays at its own pitch, snapped to OpenMPT's nearest note (57 → A#4's 56, 1700 →
    /// C-0's 1712), and one note says so (ADR 0055).
    /// </summary>
    [Fact]
    public void NotesBeyondTheRangeSnapToOpenMptsTable()
    {
        (Playback playback, ConversionNotes notes) = Play(Song()
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 57)
            .WithCell(0, row: 0, channel: 1, sample: 1, period: 1700)
            .WithCell(0, row: 1, channel: 0, sample: 1, period: C2));

        Assert.Equal(56, PeriodAt(playback, 0));
        Assert.Equal(1712, PeriodAt(playback, 0, channel: 1));
        Assert.Equal(C2, PeriodAt(playback, 6));
        Assert.Contains(notes.Messages, m => m.Contains("2 notes lie beyond ProTracker's range", StringComparison.Ordinal));
    }

    /// <summary>
    /// Beyond the range, finetune follows the same rule as within it (ADR 0060): C-0 at −8 is C-1's 907 doubled, 1814;
    /// A#-4 (56) at +1 is ProTracker's B-1 at +1, 450, ÷ 8 = 56.25.
    /// </summary>
    [Theory]
    [InlineData(1712, -8, 1814.0)]
    [InlineData(56, 1, 56.25)]
    public void NotesBeyondTheRangeTakeFinetune(int written, int fineTune, double expected)
    {
        (Playback playback, _) = Play(new ModBuilder()
            .WithSample(1, "lead", 1024, fineTune: fineTune)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: written));

        Assert.Equal(expected, QuarterPeriodAt(playback, 0));
    }

    /// <summary>
    /// In a song with notes beyond the range, slides run from period 14 to 3424 instead of 113 to 856, and a slide past
    /// 0 is clamped instead of wrapping as in ProTracker.
    /// </summary>
    [Fact]
    public void NotesBeyondTheRangeWidenTheSlides()
    {
        (Playback playback, _) = Play(Song()
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 30, effect: 0x1, parameter: 0x10)
            .WithCell(0, row: 0, channel: 1, sample: 1, period: 1712, effect: 0x2, parameter: 0xFF)
            .WithCell(0, row: 1, channel: 1, sample: 0, period: 0, effect: 0x2, parameter: 0xFF));

        Assert.Equal([30, 14, 14], Enumerable.Range(0, 3).Select(t => PeriodAt(playback, t)));
        Assert.Equal([1712, 1967, 3242, 3424, 3424], new[] { 0, 1, 7, 8, 9 }.Select(t => PeriodAt(playback, t, channel: 1)));
    }

    /// <summary>A song with every note in range keeps ProTracker's limits and no note is added.</summary>
    [Fact]
    public void NotesInTheRangeKeepProTrackersLimits()
    {
        (Playback playback, ConversionNotes notes) = Play(Song().WithCell(0, row: 0, channel: 0, sample: 1, period: 856, effect: 0x2, parameter: 0x10));

        Assert.Equal([856, 856], Enumerable.Range(0, 2).Select(t => PeriodAt(playback, t)));
        Assert.DoesNotContain(notes.Messages, m => m.Contains("beyond ProTracker's range", StringComparison.Ordinal));
    }

    /// <summary>Above ProTracker's top note an arpeggio moves along OpenMPT's six-octave table: 56 → 47 (+3) → 37 (+7).</summary>
    [Fact]
    public void ArpeggioBeyondTheRangeUsesOpenMptsTable()
    {
        (Playback playback, _) = Play(Song().WithCell(0, row: 0, channel: 0, sample: 1, period: 56, effect: 0x0, parameter: 0x37));

        Assert.Equal([56, 47, 37, 56], Enumerable.Range(0, 4).Select(t => PeriodAt(playback, t)));
    }

    /// <summary>Past B-5 an arpeggio goes on up by the same rule (G#-5 + 7 is 22.5), and stops at period 14.</summary>
    [Fact]
    public void ArpeggioPastTheSixOctavesGoesOn()
    {
        (Playback playback, _) = Play(Song()
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 33, effect: 0x0, parameter: 0x37)
            .WithCell(0, row: 0, channel: 1, sample: 1, period: 30, effect: 0x0, parameter: 0x1F));

        Assert.Equal([33, 28, 22.5], QuarterPeriods(playback, 0, 3));
        Assert.Equal([30, 28, 14], QuarterPeriods(playback, 0, 3, channel: 1));
    }

    /// <summary>A tone portamento reaches a note beyond the range, and glissando snaps it to OpenMPT's notes there (101, 85, 71).</summary>
    [Fact]
    public void TonePortamentoReachesNotesBeyondTheRange()
    {
        (Playback playback, _) = Play(Song()
            .WithCell(0, row: 0, channel: 0, sample: 1, period: 120, effect: 0xE, parameter: 0x31)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: 56, effect: 0x3, parameter: 0x10));

        Assert.Equal([120, 101, 85, 71, 56, 56], Enumerable.Range(6, 6).Select(t => PeriodAt(playback, t)));
    }

    /// <summary>A tone portamento on a finetuned note slides to OpenMPT's period for the note (C-3 at −3: 218.75).</summary>
    [Fact]
    public void TonePortamentoSlidesToTheFinetunesNote()
    {
        (Playback playback, _) = Play(new ModBuilder()
            .WithSample(1, "lead", 1024, fineTune: -3)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: C2)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: 214, effect: 0x3, parameter: 0xFF));

        Assert.Equal([437.5, 218.75, 218.75], QuarterPeriods(playback, 6, 3));
    }

    /// <summary>
    /// Ultimate Soundtracker rules: speed 6 always (Fxx doesn't exist), the tempo from byte 471, 1xy is arpeggio and
    /// samples start at their loop start.
    /// </summary>
    [Fact]
    public void UltimateSoundtrackerRules()
    {
        (Playback playback, _) = Play(new ModBuilder { Soundtracker = true, RestartPosition = 100 }
            .WithSample(1, "pad", 1024, loopStartWords: 256, loopLength: 512)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: C2, effect: 0x1, parameter: 0x37)
            .WithCell(0, row: 1, channel: 0, sample: 0, period: 0, effect: 0xF, parameter: 0x03));

        double bpm = ModPlayers.UltimateSoundtrackerBpm(100) ?? 0;
        Assert.Equal(Ticks64Rows, playback.Ticks.Count);
        Assert.Equal(Ticks64Rows / (bpm * 2 / 5), playback.DurationSeconds, 1e-9);
        Assert.Equal(360, PeriodAt(playback, 1));
        // The sound before the loop is skipped; with its loop now at the start, the sample ends at the loop end (OpenMPT).
        Assert.Equal(new SampleBlock(0, 0, 512), At(playback, 0).Started);
        Assert.Equal(new SampleBlock(0, 0, 512), At(playback, 0).Next);
    }

    /// <summary>Bad arguments are caller bugs and throw.</summary>
    [Fact]
    public void BadArgumentsThrow()
    {
        ModModule module = ModReader.Read(Song().Build(), new ConversionNotes(Logger.None));
        ConversionNotes notes = new(Logger.None);

        Assert.Throws<ArgumentOutOfRangeException>(() => ModTickPlayer.Play(module, 0, 1, notes));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModTickPlayer.Play(module, AmigaPitch.PalClockHz, 0, notes));
    }
}
