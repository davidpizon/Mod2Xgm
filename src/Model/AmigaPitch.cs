namespace Mod2Xgm.Model;

/// <summary>
/// Amiga (Paula) pitch, the scale behind <see cref="PitchScale.AmigaPeriod"/> (plan T3.2, docs/formats/mod.md#pitch).
/// Turns a period into a <see cref="NotePitch"/> for a given Amiga clock, and applies finetune exactly the way
/// ProTracker 2 does: by reading the same note from one of its 16 hand-rounded period tables.
/// <para>A higher period means a lower note: Paula plays a sample at <c>clock / period</c> samples per second.</para>
/// </summary>
internal static class AmigaPitch
{
    /// <summary>Paula clock of a PAL Amiga: the 28 375 160 Hz crystal divided by 8 (period 428 plays at about 8287 Hz).</summary>
    public const double PalClockHz = 28_375_160 / 8.0;

    /// <summary>Paula clock of an NTSC Amiga: the 28 636 360 Hz crystal divided by 8, about 15.9 cents higher than PAL.</summary>
    public const double NtscClockHz = 28_636_360 / 8.0;

    /// <summary>Notes in one period table: C-1 to B-3.</summary>
    public const int NotesPerTable = 36;

    /// <summary>Semitone of the first table entry (C-1), counted from C-0 like <see cref="NotePitch.Semitone"/>.</summary>
    public const int FirstSemitone = 12;

    /// <summary>Lowest finetune, in eighths of a semitone.</summary>
    public const int MinFineTune = -8;

    /// <summary>Highest finetune, in eighths of a semitone.</summary>
    public const int MaxFineTune = 7;

    /// <summary>Cents in one finetune step (an eighth of a semitone).</summary>
    public const double CentsPerFineTune = 12.5;

    /// <summary>
    /// The period that is exactly C-2 at 0 cents: finetune 0 on a PAL Amiga, playing at <see cref="PalClockHz"/> / 428.
    /// Every other pitch is measured from it, so an NTSC clock reads 15.9 cents sharp.
    /// </summary>
    private const int ReferencePeriod = 428;

    /// <summary>Semitone of <see cref="ReferencePeriod"/> (C-2).</summary>
    private const int ReferenceSemitone = 24;

    /// <summary>
    /// ProTracker 2.3's period tables: 16 tables of <see cref="NotesPerTable"/> periods, in ProTracker's own order
    /// (finetune 0 to +7, then −8 to −1), so a table's index is the finetune's low 4 bits. Data from pt2-clone's
    /// <c>pt2_tables.c</c> (BSD-3-Clause, THIRD-PARTY-NOTICES.md), without its zero padding.
    /// </summary>
    private static ReadOnlySpan<short> Periods =>
    [
        // finetune 0
        856, 808, 762, 720, 678, 640, 604, 570, 538, 508, 480, 453,
        428, 404, 381, 360, 339, 320, 302, 285, 269, 254, 240, 226,
        214, 202, 190, 180, 170, 160, 151, 143, 135, 127, 120, 113,
        // finetune +1
        850, 802, 757, 715, 674, 637, 601, 567, 535, 505, 477, 450,
        425, 401, 379, 357, 337, 318, 300, 284, 268, 253, 239, 225,
        213, 201, 189, 179, 169, 159, 150, 142, 134, 126, 119, 113,
        // finetune +2
        844, 796, 752, 709, 670, 632, 597, 563, 532, 502, 474, 447,
        422, 398, 376, 355, 335, 316, 298, 282, 266, 251, 237, 224,
        211, 199, 188, 177, 167, 158, 149, 141, 133, 125, 118, 112,
        // finetune +3
        838, 791, 746, 704, 665, 628, 592, 559, 528, 498, 470, 444,
        419, 395, 373, 352, 332, 314, 296, 280, 264, 249, 235, 222,
        209, 198, 187, 176, 166, 157, 148, 140, 132, 125, 118, 111,
        // finetune +4
        832, 785, 741, 699, 660, 623, 588, 555, 524, 495, 467, 441,
        416, 392, 370, 350, 330, 312, 294, 278, 262, 247, 233, 220,
        208, 196, 185, 175, 165, 156, 147, 139, 131, 124, 117, 110,
        // finetune +5
        826, 779, 736, 694, 655, 619, 584, 551, 520, 491, 463, 437,
        413, 390, 368, 347, 328, 309, 292, 276, 260, 245, 232, 219,
        206, 195, 184, 174, 164, 155, 146, 138, 130, 123, 116, 109,
        // finetune +6
        820, 774, 730, 689, 651, 614, 580, 547, 516, 487, 460, 434,
        410, 387, 365, 345, 325, 307, 290, 274, 258, 244, 230, 217,
        205, 193, 183, 172, 163, 154, 145, 137, 129, 122, 115, 109,
        // finetune +7
        814, 768, 725, 684, 646, 610, 575, 543, 513, 484, 457, 431,
        407, 384, 363, 342, 323, 305, 288, 272, 256, 242, 228, 216,
        204, 192, 181, 171, 161, 152, 144, 136, 128, 121, 114, 108,
        // finetune -8
        907, 856, 808, 762, 720, 678, 640, 604, 570, 538, 508, 480,
        453, 428, 404, 381, 360, 339, 320, 302, 285, 269, 254, 240,
        226, 214, 202, 190, 180, 170, 160, 151, 143, 135, 127, 120,
        // finetune -7
        900, 850, 802, 757, 715, 675, 636, 601, 567, 535, 505, 477,
        450, 425, 401, 379, 357, 337, 318, 300, 284, 268, 253, 238,
        225, 212, 200, 189, 179, 169, 159, 150, 142, 134, 126, 119,
        // finetune -6
        894, 844, 796, 752, 709, 670, 632, 597, 563, 532, 502, 474,
        447, 422, 398, 376, 355, 335, 316, 298, 282, 266, 251, 237,
        223, 211, 199, 188, 177, 167, 158, 149, 141, 133, 125, 118,
        // finetune -5
        887, 838, 791, 746, 704, 665, 628, 592, 559, 528, 498, 470,
        444, 419, 395, 373, 352, 332, 314, 296, 280, 264, 249, 235,
        222, 209, 198, 187, 176, 166, 157, 148, 140, 132, 125, 118,
        // finetune -4
        881, 832, 785, 741, 699, 660, 623, 588, 555, 524, 494, 467,
        441, 416, 392, 370, 350, 330, 312, 294, 278, 262, 247, 233,
        220, 208, 196, 185, 175, 165, 156, 147, 139, 131, 123, 117,
        // finetune -3
        875, 826, 779, 736, 694, 655, 619, 584, 551, 520, 491, 463,
        437, 413, 390, 368, 347, 328, 309, 292, 276, 260, 245, 232,
        219, 206, 195, 184, 174, 164, 155, 146, 138, 130, 123, 116,
        // finetune -2
        868, 820, 774, 730, 689, 651, 614, 580, 547, 516, 487, 460,
        434, 410, 387, 365, 345, 325, 307, 290, 274, 258, 244, 230,
        217, 205, 193, 183, 172, 163, 154, 145, 137, 129, 122, 115,
        // finetune -1
        862, 814, 768, 725, 684, 646, 610, 575, 543, 513, 484, 457,
        431, 407, 384, 363, 342, 323, 305, 288, 272, 256, 242, 228,
        216, 203, 192, 181, 171, 161, 152, 144, 136, 128, 121, 114,
    ];

    /// <summary>
    /// The pitch of <paramref name="period"/> on an Amiga with the given Paula clock: the exact playback rate, and the
    /// nearest semitone plus unrounded cents (−50 to just under +50), measured from <see cref="ReferencePeriod"/>.
    /// </summary>
    /// <param name="period">Amiga period; any positive value, including ones between table entries.</param>
    /// <param name="clockHz">Paula clock, <see cref="PalClockHz"/> or <see cref="NtscClockHz"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="period"/> or <paramref name="clockHz"/> is not positive.</exception>
    public static NotePitch FromPeriod(double period, double clockHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clockHz);

        // Ordered so that 428 on the PAL clock gives exactly 1.0, hence exactly C-2 at 0 cents.
        double semitones = ReferenceSemitone + (12.0 * Math.Log2(clockHz / PalClockHz * ReferencePeriod / period));
        int semitone = (int)Math.Floor(semitones + 0.5);
        return new NotePitch(semitone, (semitones - semitone) * 100.0, clockHz / period);
    }

    /// <summary>
    /// The period a note plays at, recovered from its playback rate: the inverse of <see cref="FromPeriod"/>, so the
    /// tick player (plan T3.4) can slide and finetune notes in period units.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="clockHz"/> or the note's rate is not positive.</exception>
    public static double PeriodOf(NotePitch note, double clockHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(note.PlaybackRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clockHz);
        return clockHz / note.PlaybackRateHz;
    }

    /// <summary>The 36 periods (C-1 to B-3) ProTracker plays for <paramref name="fineTune"/>.</summary>
    /// <param name="fineTune">Finetune, −8 to +7 eighths of a semitone.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fineTune"/> is outside −8…+7.</exception>
    public static ReadOnlySpan<short> Table(int fineTune)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fineTune, MinFineTune);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fineTune, MaxFineTune);
        return Periods.Slice((fineTune & 0xF) * NotesPerTable, NotesPerTable);
    }

    /// <summary>
    /// The period ProTracker 2 plays when a note with <paramref name="period"/> starts on an instrument with
    /// <paramref name="fineTune"/> (its <c>setPeriod</c>): the first finetune-0 entry that is not above the period
    /// picks the note, and the finetune's table gives the period. A period between two entries therefore snaps to
    /// the higher note, even at finetune 0.
    /// </summary>
    /// <param name="period">The note's period as written in the pattern.</param>
    /// <param name="fineTune">The instrument's finetune, −8 to +7.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="period"/> is not positive, or <paramref name="fineTune"/> is outside −8…+7.</exception>
    public static FineTunedPeriod ApplyFineTune(int period, int fineTune)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);
        ReadOnlySpan<short> tuned = Table(fineTune);
        ReadOnlySpan<short> plain = Table(0);
        for (int note = 0; note < NotesPerTable; note++)
        {
            if (period >= plain[note])
            {
                return new FineTunedPeriod(tuned[note], note);
            }
        }

        // Below B-3 at finetune 0 (113). ProTracker 2 reads its tables' zero padding here and plays period 0; that
        // can't be rendered, so the period is kept as written (docs/adr/0030-amiga-pitch-regions-finetune.md, T3.2) and the caller adds a conversion note.
        return new FineTunedPeriod(period, null);
    }

    /// <summary>The finetune step of a tuning in cents (<see cref="Instrument.FineTuneCents"/>), −8 to +7.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cents"/> isn't a whole number of steps from −100 to +87.5.</exception>
    public static int FineTuneFromCents(double cents)
    {
        double steps = cents / CentsPerFineTune;
        if (!double.IsInteger(steps) || steps < MinFineTune || steps > MaxFineTune)
        {
            throw new ArgumentOutOfRangeException(nameof(cents), cents, "Not a ProTracker finetune (a multiple of 12.5 cents from -100 to +87.5).");
        }

        return (int)steps;
    }
}

/// <summary>The result of <see cref="AmigaPitch.ApplyFineTune"/>.</summary>
/// <param name="Period">The period to play.</param>
/// <param name="NoteIndex">The note found in the tables (0 = C-1 … 35 = B-3), or <c>null</c> when the written period is
/// below the tables and was kept as written.</param>
internal readonly record struct FineTunedPeriod(int Period, int? NoteIndex);
