namespace Mod2Xgm.Tests.Dmf;

/// <summary>
/// The probe DMF files (plan T4.3, tests/golden/dmf-probes/README.md): tiny songs that check, by ear in DefleMask
/// and through VGM export → rescomp, the parts of docs/formats/dmf.md that came from reading other programs rather
/// than from DefleMask itself. Each is built so a wrong guess sounds wrong (or silent), not just slightly off.
/// </summary>
internal static class DmfProbes
{
    /// <summary>FM channels' maximum volume.</summary>
    private const int FmVolume = 0x7F;

    /// <summary>PSG channels' maximum volume.</summary>
    private const int PsgVolume = 0xF;

    /// <summary>Note value for "note off".</summary>
    private const int Off = 100;

    /// <summary>Every probe, by file name (without <c>.dmf</c>).</summary>
    public static IReadOnlyDictionary<string, Func<DmfBuilder>> All { get; } = new Dictionary<string, Func<DmfBuilder>>
    {
        ["fm-note"] = FmNote,
        ["psg-note"] = PsgNote,
        ["dac-sample"] = DacSample,
        ["custom-hz"] = CustomHz,
        ["ext-ch3"] = ExtendedChannel3,
    };

    /// <summary>
    /// FM1 plays A-4, then C-5, with a patch whose only sounding operator is <b>file operator 2</b> under algorithm 4.
    /// If file order is register order (OP1, OP3, OP2, OP4), that is OP2, a carrier: a pure tone. If file order were
    /// OP1–OP4, it would be OP3, a modulator: silence. C-5 is stored as note 12 of octave 4, so a wrong C rule plays
    /// the wrong octave.
    /// </summary>
    private static DmfBuilder FmNote() =>
        Song("FM note", rows: 16)
            .WithFmInstrument("sine (file op 2)", FmPatch(algorithm: 4, soundingOperators: [2]))
            .WithCell(0, 0, 0, note: 9, octave: 4, volume: FmVolume, instrument: 0, effects: new DmfEffect(0x08, 0x11))
            .WithCell(0, 0, 6, note: 12, octave: 4, volume: FmVolume, instrument: 0)
            .WithCell(0, 0, 12, note: Off);

    /// <summary>
    /// PSG1 (DefleMask's SN1) plays a square <c>A-4</c>, which DefleMask sounds at A-6 (its PSG notes are two octaves
    /// above their name, docs/formats/dmf.md); then PSG4 plays white noise (<c>2001</c>: fixed rate, white).
    /// </summary>
    private static DmfBuilder PsgNote() =>
        Song("PSG note", rows: 16)
            .WithStdInstrument("full volume", new DmfStdInstrument(new DmfMacro([PsgVolume], -1), Empty(), 0, Empty(), Empty()))
            .WithCell(6, 0, 0, note: 9, octave: 4, volume: PsgVolume, instrument: 0)
            .WithCell(6, 0, 6, note: Off)
            .WithCell(9, 0, 8, note: 12, octave: 3, volume: PsgVolume, instrument: 0, effects: new DmfEffect(0x20, 0x01))
            .WithCell(9, 0, 14, note: Off);

    /// <summary>
    /// FM6 switches to the DAC (<c>1701</c>) and plays: C-3 → sample 0 (440 Hz at rate code 5, 32 kHz); C-5 → sample 0
    /// again at the <b>same</b> pitch (the octave is ignored); C#-3 → sample 1 (660 Hz at rate code 1, 8 kHz). Right
    /// rate codes make the first two sound A-4 and the third E-5.
    /// </summary>
    private static DmfBuilder DacSample() =>
        Song("DAC sample", rows: 16)
            .WithFmInstrument("sine (file op 2)", FmPatch(algorithm: 4, soundingOperators: [2]))
            .WithSample(Sine("a440 @ 32 kHz", rateCode: 5, rateHz: 32000, toneHz: 440))
            .WithSample(Sine("e660 @ 8 kHz", rateCode: 1, rateHz: 8000, toneHz: 660))
            .WithCell(5, 0, 0, note: 12, octave: 2, instrument: 0, effects: new DmfEffect(0x17, 0x01))
            .WithCell(5, 0, 4, note: 12, octave: 4, instrument: 0)
            .WithCell(5, 0, 8, note: 1, octave: 3, instrument: 0)
            .WithCell(5, 0, 12, note: Off);

    /// <summary>
    /// Custom Hz <c>"25"</c> with 5 ticks per row: a row every 0.2 s, so the A-4 beeps on rows 0, 5, 10 and 15 come
    /// exactly one second apart (and keep that beat when the 20-row song loops).
    /// </summary>
    private static DmfBuilder CustomHz()
    {
        DmfBuilder builder = Song("custom Hz 25", rows: 20, ticks: 5, customHz: "25\0"u8.ToArray())
            .WithFmInstrument("sine (file op 2)", FmPatch(algorithm: 4, soundingOperators: [2]));
        for (int row = 0; row < 20; row += 5)
        {
            builder.WithCell(0, 0, row, note: 9, octave: 4, volume: FmVolume, instrument: 0).WithCell(0, 0, row + 1, note: Off);
        }

        return builder;
    }

    /// <summary>
    /// System 0x42: the FM3 OP1–OP4 channels (file channels 3–6) enter one by one with C-4, E-4, G-4, C-5, building a
    /// C major chord from FM3 alone. The notes must show in DefleMask's FM3 OP1–OP4 columns.
    /// </summary>
    private static DmfBuilder ExtendedChannel3() =>
        Song("ext CH3 chord", rows: 16, system: DmfSystem.GenesisExtendedChannel3)
            .WithFmInstrument("4 sines", FmPatch(algorithm: 7, soundingOperators: [0, 1, 2, 3], level: 12))
            .WithCell(2, 0, 0, note: 12, octave: 3, volume: FmVolume, instrument: 0)
            .WithCell(3, 0, 2, note: 4, octave: 4, volume: FmVolume, instrument: 0)
            .WithCell(4, 0, 4, note: 7, octave: 4, volume: FmVolume, instrument: 0)
            .WithCell(5, 0, 6, note: 12, octave: 4, volume: FmVolume, instrument: 0)
            .WithCell(2, 0, 12, note: Off)
            .WithCell(3, 0, 12, note: Off)
            .WithCell(4, 0, 12, note: Off)
            .WithCell(5, 0, 12, note: Off);

    /// <summary>A one-pattern song: PAL (unless a custom Hz is given), version 24, 1 effect column.</summary>
    private static DmfBuilder Song(string title, int rows, int ticks = 6, byte[]? customHz = null, DmfSystem system = DmfSystem.Genesis) =>
        new()
        {
            Name = "Mod2Xgm probe: " + title,
            System = (int)system,
            RowsPerPattern = rows,
            TickTime1 = ticks,
            TickTime2 = ticks,
            UsingCustomHz = customHz is null ? 0 : 1,
            CustomHzBytes = customHz ?? "50\0"u8.ToArray(),
        };

    /// <summary>
    /// An FM patch of plain sines: the operators at the given <b>file</b> positions play at <paramref name="level"/>
    /// (TL), the others are silent (TL 127). Instant attack, full sustain, MULT 1, no detune (DT 3), no SSG-EG.
    /// </summary>
    private static DmfFmInstrument FmPatch(int algorithm, int[] soundingOperators, int level = 0) =>
        new(algorithm, 0, 0, 0, [.. Enumerable.Range(0, 4).Select(op => new DmfOperator(
            Am: 0, Ar: 31, Dr: 0, Mult: 1, Rr: 8, Sl: 0, Tl: soundingOperators.Contains(op) ? level : 127, Dt2: 0, Rs: 0, Dt: 3, D2R: 0, SsgMode: 0))]);

    /// <summary>An empty macro.</summary>
    private static DmfMacro Empty() => new([], -1);

    /// <summary>0.4 s of a sine tone at half of full scale, as signed 16-bit samples at <paramref name="rateHz"/>.</summary>
    private static DmfSample Sine(string name, int rateCode, int rateHz, double toneHz)
    {
        short[] data = [.. Enumerable.Range(0, rateHz * 2 / 5).Select(i => (short)Math.Round(16000 * Math.Sin(2 * Math.PI * toneHz * i / rateHz)))];
        return new DmfSample(name, rateCode, 5, 50, 16, null, null, data);
    }
}
