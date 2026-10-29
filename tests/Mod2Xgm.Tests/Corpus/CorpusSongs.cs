using Mod2Xgm.Tests.Mod;

namespace Mod2Xgm.Tests.Corpus;

/// <summary>
/// The corpus songs made in code (plan T6.1, tests/data/LICENSES.md). The Mod Archive lists no 15-sample Soundtracker
/// module under Public Domain or CC0, so the corpus's 15-sample song is built here and carries no one else's copyright.
/// </summary>
internal static class CorpusSongs
{
    /// <summary>The checked-in file name of <see cref="Soundtracker15"/>, in tests/data/corpus.</summary>
    public const string Soundtracker15FileName = "soundtracker15.mod";

    // ProTracker periods, finetune 0 (docs/formats/mod.md).
    private const int C1 = 856;
    private const int F1 = 640;
    private const int G1 = 570;
    private const int C2 = 428;
    private const int D2 = 381;
    private const int E2 = 339;
    private const int G2 = 285;
    private const int A2 = 254;
    private const int C3 = 214;

    /// <summary>Sample numbers.</summary>
    private const int Drum = 1;

    private const int Bass = 2;
    private const int Lead = 3;
    private const int Hat = 4;

    /// <summary>Ultimate Soundtracker's arpeggio (<c>1xy</c>) and pitch bend (<c>2xy</c>), docs/formats/mod.md#players.</summary>
    private const int Arpeggio = 0x1;

    private const int PitchBend = 0x2;

    /// <summary>Byte 471 under Ultimate Soundtracker: 0x78 is 125 BPM (docs/formats/mod.md#players).</summary>
    private const int Tempo125 = 0x78;

    private const int Rows = 64;

    /// <summary>White noise from a fixed seed.</summary>
    private static readonly sbyte[] HatData = [.. new Random(15).GetItems<sbyte>([-90, -45, 45, 90], 2000)];

    /// <summary>
    /// A short 15-sample tune in Ultimate Soundtracker's own terms: drums and a bass line, then a lead with <c>1xy</c>
    /// arpeggios, then the lead bending with <c>2xy</c>. Orders 0, 1, 2, 1. Sample 15 holds the credit, as real files do.
    /// </summary>
    public static ModBuilder Soundtracker15()
    {
        ModBuilder mod = new ModBuilder { Title = "mod2xgm ust tune", Soundtracker = true, RestartPosition = Tempo125 }
            .WithOrders(0, 1, 2, 1)
            .WithSample(Drum, "st-drum", 6000, data: i => (sbyte)Math.Round(115 * Math.Sin(2 * Math.PI * i / (30 + (i / 200.0))) * Math.Exp(-i / 1200.0)))
            .WithSample(Bass, "st-bass", 32, volume: 48, loopLength: 32, data: i => (sbyte)((i * 8) - 128))
            .WithSample(Lead, "st-lead", 32, volume: 40, loopLength: 32, data: i => (sbyte)(i < 16 ? 80 : -80))
            .WithSample(Hat, "st-hat", HatData.Length, volume: 32, data: i => (sbyte)Math.Round(HatData[i] * Math.Exp(-i / 400.0)))
            .WithSample(15, "made in code, CC0", 0);

        for (int pattern = 0; pattern < 3; pattern++)
        {
            Rhythm(mod, pattern);
        }

        // Pattern 1: the lead holds the bass line's chords as arpeggios (C, F from its A, G, then C with an octave).
        (int Row, int Period, int Chord)[] chords = [(0, C3, 0x47), (16, A2, 0x38), (32, G2, 0x47), (48, C3, 0x7C)];
        foreach ((int row, int period, int chord) in chords)
        {
            mod.WithCell(1, row, 2, Lead, period, Arpeggio, chord);
            for (int held = row + 1; held < row + 14; held++)
            {
                mod.WithCell(1, held, 2, 0, 0, Arpeggio, chord);
            }
        }

        // Pattern 2: a melody whose notes bend up into place (2 period units on each of the row's 5 later ticks), then a
        // long bend down.
        (int Row, int Period)[] melody = [(0, E2), (8, G2), (16, A2), (24, G2), (32, E2), (40, D2), (48, C2)];
        foreach ((int row, int period) in melody)
        {
            mod.WithCell(2, row, 2, Lead, period + 10, PitchBend, 0x02);
        }

        for (int row = 52; row < 60; row++)
        {
            mod.WithCell(2, row, 2, 0, 0, PitchBend, 0x10);
        }

        return mod;
    }

    /// <summary>Drum, hat and the C–F–G–C bass line on channels 1, 4 and 2 of <paramref name="pattern"/>.</summary>
    private static void Rhythm(ModBuilder mod, int pattern)
    {
        int[] bassLine = [C1, F1, G1, C1];
        for (int row = 0; row < Rows; row++)
        {
            if (row % 16 is 0 or 10)
            {
                mod.WithCell(pattern, row, 0, Drum, C2);
            }

            if (row % 8 == 4)
            {
                mod.WithCell(pattern, row, 3, Hat, C3);
            }

            if (row % 4 == 0)
            {
                mod.WithCell(pattern, row, 1, Bass, bassLine[row / 16]);
            }
        }
    }
}
