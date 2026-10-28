using Mod2Xgm.Model;

namespace Mod2Xgm.Tests.Model;

/// <summary>Tests for Amiga period → note + cents, finetune and the PAL/NTSC clock (plan T3.2).</summary>
public sealed class AmigaPitchTests
{
    /// <summary>ProTracker's rounding to whole periods leaves every table entry within 8.75 cents of perfect tuning.</summary>
    private const double TableRoundingCents = 8.75;

    /// <summary>How much higher an NTSC Amiga plays the same period: 1200 × log2(28 636 360 / 28 375 160).</summary>
    private const double NtscShiftCents = 15.863513114550358;

    /// <summary>Every finetune with every note index of its table (16 × 36 cases).</summary>
    public static TheoryData<int, int> EveryTableEntry
    {
        get
        {
            TheoryData<int, int> data = [];
            for (int fineTune = AmigaPitch.MinFineTune; fineTune <= AmigaPitch.MaxFineTune; fineTune++)
            {
                for (int note = 0; note < AmigaPitch.NotesPerTable; note++)
                {
                    data.Add(fineTune, note);
                }
            }

            return data;
        }
    }

    /// <summary>Period 428 on a PAL Amiga is the reference: exactly C-2 at 0 cents, playing at clock / 428.</summary>
    [Fact]
    public void ReferencePeriodIsExactlyC2()
    {
        NotePitch pitch = AmigaPitch.FromPeriod(428, AmigaPitch.PalClockHz);

        Assert.Equal(24, pitch.Semitone);
        Assert.Equal(0.0, pitch.Cents);
        Assert.Equal(3_546_895.0 / 428, pitch.PlaybackRateHz);
    }

    /// <summary>Doubling the period drops an octave and halving it raises one, with no cents error.</summary>
    [Theory]
    [InlineData(856, 12)]
    [InlineData(428, 24)]
    [InlineData(214, 36)]
    [InlineData(107, 48)]
    public void OctavesAreExact(int period, int semitone)
    {
        NotePitch pitch = AmigaPitch.FromPeriod(period, AmigaPitch.PalClockHz);

        Assert.Equal(semitone, pitch.Semitone);
        Assert.Equal(0.0, pitch.Cents, 1e-9);
    }

    /// <summary>
    /// Each entry of each finetune table is its note (C-1 + index) shifted by finetune × 12.5 cents, within ProTracker's
    /// rounding: the table data matches what the tables are meant to be.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryTableEntry))]
    public void EveryTableEntryIsItsNoteWithinRounding(int fineTune, int note)
    {
        NotePitch pitch = AmigaPitch.FromPeriod(AmigaPitch.Table(fineTune)[note], AmigaPitch.PalClockHz);

        double expectedCents = ((AmigaPitch.FirstSemitone + note) * 100.0) + (fineTune * AmigaPitch.CentsPerFineTune);
        Assert.Equal(expectedCents, (pitch.Semitone * 100.0) + pitch.Cents, TableRoundingCents);
    }

    /// <summary>The same period plays 15.9 cents sharper on an NTSC Amiga, at the NTSC clock / period.</summary>
    [Fact]
    public void NtscClockPlaysSharper()
    {
        NotePitch pitch = AmigaPitch.FromPeriod(428, AmigaPitch.NtscClockHz);

        Assert.Equal(24, pitch.Semitone);
        Assert.Equal(NtscShiftCents, pitch.Cents, 1e-9);
        Assert.Equal(3_579_545.0 / 428, pitch.PlaybackRateHz);
    }

    /// <summary>A period between table entries keeps its exact pitch: nearest semitone plus the unrounded rest.</summary>
    [Theory]
    [InlineData(440.0)]
    [InlineData(1000.0)]
    [InlineData(150.5)]
    [InlineData(4095.0)]
    public void InBetweenPeriodsKeepTheirExactPitch(double period)
    {
        NotePitch pitch = AmigaPitch.FromPeriod(period, AmigaPitch.PalClockHz);

        double expected = 2400 + (1200 * Math.Log2(428 / period));
        Assert.Equal(expected, (pitch.Semitone * 100.0) + pitch.Cents, 1e-9);
        Assert.Equal(AmigaPitch.PalClockHz / period, pitch.PlaybackRateHz);
    }

    /// <summary>Cents always lie in −50…+50 (−50 included, +50 not), on both clocks, for every whole period 1–4095.</summary>
    [Fact]
    public void CentsStayWithinHalfASemitone()
    {
        foreach (double clock in new[] { AmigaPitch.PalClockHz, AmigaPitch.NtscClockHz })
        {
            for (int period = 1; period <= 4095; period++)
            {
                double cents = AmigaPitch.FromPeriod(period, clock).Cents;
                Assert.InRange(cents, -50.0, 50.0);
                Assert.True(cents < 50.0, $"period {period}: {cents} cents");
            }
        }
    }

    /// <summary><see cref="AmigaPitch.PeriodOf"/> gives back the period a pitch was made from, on both clocks.</summary>
    [Theory]
    [InlineData(113.0)]
    [InlineData(428.0)]
    [InlineData(907.0)]
    [InlineData(1234.5)]
    public void PeriodOfInvertsFromPeriod(double period)
    {
        foreach (double clock in new[] { AmigaPitch.PalClockHz, AmigaPitch.NtscClockHz })
        {
            Assert.Equal(period, AmigaPitch.PeriodOf(AmigaPitch.FromPeriod(period, clock), clock), 1e-9);
        }
    }

    /// <summary>
    /// Finetune works like ProTracker 2's <c>setPeriod</c>: find the note in the finetune-0 table (a period between two
    /// entries snaps to the higher note), then play that note from the finetune's table.
    /// </summary>
    [Theory]
    [InlineData(428, 0, 428, 12)]
    [InlineData(428, 1, 425, 12)]
    [InlineData(428, 7, 407, 12)]
    [InlineData(428, -1, 431, 12)]
    [InlineData(428, -8, 453, 12)]
    [InlineData(856, -8, 907, 0)]
    [InlineData(850, 0, 808, 1)]
    [InlineData(907, 0, 856, 0)]
    [InlineData(4000, 7, 814, 0)]
    [InlineData(113, 7, 108, 35)]
    [InlineData(114, 0, 113, 35)]
    public void ApplyFineTuneMatchesProTracker(int period, int fineTune, int expectedPeriod, int expectedNote)
    {
        FineTunedPeriod played = AmigaPitch.ApplyFineTune(period, fineTune);

        Assert.Equal(expectedPeriod, played.Period);
        Assert.Equal(expectedNote, played.NoteIndex);
    }

    /// <summary>A period below the tables (under 113) is kept as written, with no note index, at any finetune.</summary>
    [Theory]
    [InlineData(112, 0)]
    [InlineData(100, 5)]
    [InlineData(1, -8)]
    public void PeriodsBelowTheTablesAreKept(int period, int fineTune)
    {
        FineTunedPeriod played = AmigaPitch.ApplyFineTune(period, fineTune);

        Assert.Equal(period, played.Period);
        Assert.Null(played.NoteIndex);
    }

    /// <summary>The finetune-0 table is ProTracker's familiar C-1…B-3 row, and the extremes of the other tables are right.</summary>
    [Fact]
    public void TablesHoldProTrackerPeriods()
    {
        short[] plain =
        [
            856, 808, 762, 720, 678, 640, 604, 570, 538, 508, 480, 453,
            428, 404, 381, 360, 339, 320, 302, 285, 269, 254, 240, 226,
            214, 202, 190, 180, 170, 160, 151, 143, 135, 127, 120, 113,
        ];
        Assert.Equal(plain, AmigaPitch.Table(0).ToArray());
        Assert.Equal(907, AmigaPitch.Table(-8)[0]);
        Assert.Equal(120, AmigaPitch.Table(-8)[35]);
        Assert.Equal(814, AmigaPitch.Table(7)[0]);
        Assert.Equal(108, AmigaPitch.Table(7)[35]);
        Assert.Equal(862, AmigaPitch.Table(-1)[0]);
        Assert.Equal(114, AmigaPitch.Table(-1)[35]);
    }

    /// <summary>Every table rises in pitch note by note, and a higher finetune never gives a longer period.</summary>
    [Fact]
    public void TablesAreOrdered()
    {
        for (int fineTune = AmigaPitch.MinFineTune; fineTune <= AmigaPitch.MaxFineTune; fineTune++)
        {
            ReadOnlySpan<short> table = AmigaPitch.Table(fineTune);
            Assert.Equal(AmigaPitch.NotesPerTable, table.Length);
            for (int note = 0; note < AmigaPitch.NotesPerTable; note++)
            {
                if (note > 0)
                {
                    Assert.True(table[note] < table[note - 1], $"finetune {fineTune}, note {note}");
                }

                if (fineTune > AmigaPitch.MinFineTune)
                {
                    Assert.True(table[note] <= AmigaPitch.Table(fineTune - 1)[note], $"finetune {fineTune}, note {note}");
                }
            }
        }
    }

    /// <summary>An instrument's tuning in cents converts back to its finetune step.</summary>
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(12.5, 1)]
    [InlineData(87.5, 7)]
    [InlineData(-12.5, -1)]
    [InlineData(-100.0, -8)]
    public void FineTuneFromCents(double cents, int expected) =>
        Assert.Equal(expected, AmigaPitch.FineTuneFromCents(cents));

    /// <summary>A tuning that isn't a ProTracker finetune is a caller bug and throws.</summary>
    [Theory]
    [InlineData(3.0)]
    [InlineData(100.0)]
    [InlineData(-112.5)]
    [InlineData(double.NaN)]
    public void FineTuneFromCentsRejectsOtherTunings(double cents) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AmigaPitch.FineTuneFromCents(cents));

    /// <summary>Periods, clocks and finetunes outside their ranges are caller bugs and throw.</summary>
    [Fact]
    public void OutOfRangeArgumentsThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AmigaPitch.FromPeriod(0, AmigaPitch.PalClockHz));
        Assert.Throws<ArgumentOutOfRangeException>(() => AmigaPitch.FromPeriod(-1, AmigaPitch.PalClockHz));
        Assert.Throws<ArgumentOutOfRangeException>(() => AmigaPitch.FromPeriod(428, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AmigaPitch.PeriodOf(new NotePitch(24, 0, 0), AmigaPitch.PalClockHz));
        Assert.Throws<ArgumentOutOfRangeException>(() => AmigaPitch.PeriodOf(new NotePitch(24, 0, 8287), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AmigaPitch.Table(-9).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => AmigaPitch.Table(8).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => AmigaPitch.ApplyFineTune(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AmigaPitch.ApplyFineTune(428, 8));
    }
}
