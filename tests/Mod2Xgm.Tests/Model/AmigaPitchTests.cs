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

    /// <summary>Every finetune.</summary>
    public static TheoryData<int> EveryFineTune => new(Enumerable.Range(AmigaPitch.MinFineTune, AmigaPitch.MaxFineTune - AmigaPitch.MinFineTune + 1));

    /// <summary>Period 428 on a PAL Amiga is the reference: exactly C-2 at 0 cents, playing at clock / 428.</summary>
    [Fact]
    public void FromPeriod_WithReferencePeriod_ReturnsExactlyC2()
    {
        // Act
        NotePitch pitch = AmigaPitch.FromPeriod(428, AmigaPitch.PalClockHz);

        // Assert
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
    public void FromPeriod_WithOctaveOfReference_IsExact(int period, int semitone)
    {
        // Act
        NotePitch pitch = AmigaPitch.FromPeriod(period, AmigaPitch.PalClockHz);

        // Assert
        Assert.Equal(semitone, pitch.Semitone);
        Assert.Equal(0.0, pitch.Cents, 1e-9);
    }

    /// <summary>
    /// Each entry of each finetune table is its note (C-1 + index) shifted by finetune × 12.5 cents, within ProTracker's
    /// rounding: the table data matches what the tables are meant to be.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryTableEntry))]
    public void FromPeriod_WithTableEntry_IsItsNoteWithinRounding(int fineTune, int note)
    {
        // Arrange
        double expectedCents = ((AmigaPitch.FirstSemitone + note) * 100.0) + (fineTune * AmigaPitch.CentsPerFineTune);

        // Act
        NotePitch pitch = AmigaPitch.FromPeriod(AmigaPitch.Table(fineTune)[note], AmigaPitch.PalClockHz);

        // Assert
        Assert.Equal(expectedCents, (pitch.Semitone * 100.0) + pitch.Cents, TableRoundingCents);
    }

    /// <summary>The same period plays 15.9 cents sharper on an NTSC Amiga, at the NTSC clock / period.</summary>
    [Fact]
    public void FromPeriod_WithNtscClock_PlaysSharper()
    {
        // Act
        NotePitch pitch = AmigaPitch.FromPeriod(428, AmigaPitch.NtscClockHz);

        // Assert
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
    public void FromPeriod_WithPeriodBetweenEntries_KeepsExactPitch(double period)
    {
        // Arrange
        double expected = 2400 + (1200 * Math.Log2(428 / period));

        // Act
        NotePitch pitch = AmigaPitch.FromPeriod(period, AmigaPitch.PalClockHz);

        // Assert
        Assert.Equal(expected, (pitch.Semitone * 100.0) + pitch.Cents, 1e-9);
        Assert.Equal(AmigaPitch.PalClockHz / period, pitch.PlaybackRateHz);
    }

    /// <summary>Cents always lie in −50…+50 (−50 included, +50 not), on both clocks, for every whole period 1–4095.</summary>
    [Theory]
    [InlineData(AmigaPitch.PalClockHz)]
    [InlineData(AmigaPitch.NtscClockHz)]
    public void FromPeriod_WithEveryWholePeriod_KeepsCentsWithinHalfASemitone(double clock)
    {
        // Act
        double[] cents = [.. Enumerable.Range(1, 4095).Select(period => AmigaPitch.FromPeriod(period, clock).Cents)];

        // Assert
        Assert.All(cents, c => Assert.True(c is >= -50.0 and < 50.0, $"{c} cents"));
    }

    /// <summary><see cref="AmigaPitch.PeriodOf"/> gives back the period a pitch was made from, on both clocks.</summary>
    [Theory]
    [InlineData(113.0, AmigaPitch.PalClockHz)]
    [InlineData(428.0, AmigaPitch.PalClockHz)]
    [InlineData(907.0, AmigaPitch.PalClockHz)]
    [InlineData(1234.5, AmigaPitch.PalClockHz)]
    [InlineData(113.0, AmigaPitch.NtscClockHz)]
    [InlineData(428.0, AmigaPitch.NtscClockHz)]
    [InlineData(907.0, AmigaPitch.NtscClockHz)]
    [InlineData(1234.5, AmigaPitch.NtscClockHz)]
    public void PeriodOf_WithPitchFromPeriod_ReturnsThatPeriod(double period, double clock)
    {
        // Arrange
        NotePitch pitch = AmigaPitch.FromPeriod(period, clock);

        // Act
        double result = AmigaPitch.PeriodOf(pitch, clock);

        // Assert
        Assert.Equal(period, result, 1e-9);
    }

    /// <summary>The finetune-0 table is ProTracker's familiar C-1…B-3 row.</summary>
    [Fact]
    public void Table_ForFineTuneZero_HoldsProTrackerPeriods()
    {
        // Arrange
        short[] expected =
        [
            856, 808, 762, 720, 678, 640, 604, 570, 538, 508, 480, 453,
            428, 404, 381, 360, 339, 320, 302, 285, 269, 254, 240, 226,
            214, 202, 190, 180, 170, 160, 151, 143, 135, 127, 120, 113,
        ];

        // Act
        short[] table = AmigaPitch.Table(0).ToArray();

        // Assert
        Assert.Equal(expected, table);
    }

    /// <summary>The other tables hold ProTracker's values at their extremes.</summary>
    [Theory]
    [InlineData(-8, 907, 120)]
    [InlineData(7, 814, 108)]
    [InlineData(-1, 862, 114)]
    public void Table_ForOtherFineTunes_HoldsProTrackerExtremes(int fineTune, int first, int last)
    {
        // Act
        short[] table = AmigaPitch.Table(fineTune).ToArray();

        // Assert
        Assert.Equal((first, last), (table[0], table[35]));
    }

    /// <summary>Every table has all its notes and rises in pitch note by note.</summary>
    [Theory]
    [MemberData(nameof(EveryFineTune))]
    public void Table_ForFineTune_RisesNoteByNote(int fineTune)
    {
        // Act
        short[] table = AmigaPitch.Table(fineTune).ToArray();

        // Assert
        Assert.Equal(AmigaPitch.NotesPerTable, table.Length);
        Assert.Equal(table.OrderDescending().Distinct(), table);
    }

    /// <summary>A higher finetune never gives a longer period than the finetune below it.</summary>
    [Theory]
    [MemberData(nameof(EveryFineTune))]
    public void Table_ForFineTune_NeverExceedsTableBelow(int fineTune)
    {
        // Arrange
        short[] below = AmigaPitch.Table(Math.Max(fineTune - 1, AmigaPitch.MinFineTune)).ToArray();

        // Act
        short[] table = AmigaPitch.Table(fineTune).ToArray();

        // Assert
        Assert.All(table.Zip(below), pair => Assert.True(pair.First <= pair.Second, $"{pair.First} > {pair.Second}"));
    }

    /// <summary>An instrument's tuning in cents converts back to its finetune step.</summary>
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(12.5, 1)]
    [InlineData(87.5, 7)]
    [InlineData(-12.5, -1)]
    [InlineData(-100.0, -8)]
    public void FineTuneFromCents_WithFinetuneStep_ReturnsStep(double cents, int expected)
    {
        // Act
        int fineTune = AmigaPitch.FineTuneFromCents(cents);

        // Assert
        Assert.Equal(expected, fineTune);
    }

    /// <summary>A tuning that isn't a ProTracker finetune is a caller bug and throws.</summary>
    [Theory]
    [InlineData(3.0)]
    [InlineData(100.0)]
    [InlineData(-112.5)]
    [InlineData(double.NaN)]
    public void FineTuneFromCents_WithOtherTuning_Throws(double cents)
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => AmigaPitch.FineTuneFromCents(cents));
    }

    /// <summary>A period or clock outside its range is a caller bug and throws.</summary>
    [Theory]
    [InlineData(0.0, AmigaPitch.PalClockHz)]
    [InlineData(-1.0, AmigaPitch.PalClockHz)]
    [InlineData(428.0, 0.0)]
    public void FromPeriod_WithArgumentOutOfRange_Throws(double period, double clock)
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => AmigaPitch.FromPeriod(period, clock));
    }

    /// <summary>A playback rate or clock of 0 is a caller bug and throws.</summary>
    [Theory]
    [InlineData(0.0, AmigaPitch.PalClockHz)]
    [InlineData(8287.0, 0.0)]
    public void PeriodOf_WithArgumentOutOfRange_Throws(double rateHz, double clock)
    {
        // Arrange
        NotePitch pitch = new(24, 0, rateHz);

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => AmigaPitch.PeriodOf(pitch, clock));
    }

    /// <summary>A finetune outside ProTracker's −8…+7 is a caller bug and throws.</summary>
    [Theory]
    [InlineData(-9)]
    [InlineData(8)]
    public void Table_WithFineTuneOutOfRange_Throws(int fineTune)
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => AmigaPitch.Table(fineTune).Length);
    }
}
