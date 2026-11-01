namespace Mod2Xgm.Mapping;

/// <summary>
/// The built-in FM patches: hand-made, plausible YM2612 settings written from the chip's documented behavior, at least
/// one per algorithm. The simple mapper (docs/channel-mapping.md) picks one of the first five by how bright the sample
/// is (<see cref="ForSustained"/>, <see cref="ForOneShot"/>). The FM fitter (plan T8.2,
/// docs/adr/0069-fm-patch-fitting.md) judges them all against each sample: the closest is where its search starts and
/// the bar a fitted patch has to beat.
/// </summary>
internal static class FmPatchLibrary
{
    /// <summary>A plain sine: only OP1 sounds (all four operators are carriers under algorithm 7).</summary>
    public static FmPatch Sine { get; } = new(
        "Sine",
        7,
        0,
        [Op(mult: 1, tl: 0), Mute(), Mute(), Mute()]);

    /// <summary>Four sines at 1×, 2×, 4× and 1× (slightly detuned), added: a flute / organ tone.</summary>
    public static FmPatch Organ { get; } = new(
        "Organ",
        7,
        0,
        [Op(mult: 1, tl: 22), Op(mult: 2, tl: 34), Op(mult: 4, tl: 44), Op(mult: 1, tl: 22, detune: 1)]);

    /// <summary>Two modulator → carrier pairs with feedback (algorithm 4): a bright, buzzy lead.</summary>
    public static FmPatch Lead { get; } = new(
        "Lead",
        4,
        5,
        [Op(mult: 1, tl: 30, dr: 4), Op(mult: 1, tl: 0), Op(mult: 2, tl: 38, dr: 4), Op(mult: 2, tl: 8)]);

    /// <summary>One modulator driving three carriers (algorithm 5) with feedback: a brassy, harmonically rich tone.</summary>
    public static FmPatch Brass { get; } = new(
        "Brass",
        5,
        6,
        [Op(mult: 1, tl: 26, ar: 24), Op(mult: 1, tl: 0, ar: 22, dr: 6, sl: 4), Op(mult: 1, tl: 6, ar: 22, dr: 6, sl: 4), Op(mult: 2, tl: 12, ar: 22, dr: 6, sl: 4)]);

    /// <summary>Fast-decaying modulator and carriers (algorithm 4): a plucked or struck note for samples that don't loop.</summary>
    public static FmPatch Pluck { get; } = new(
        "Pluck",
        4,
        4,
        [Op(mult: 2, tl: 34, dr: 14, sl: 8, rr: 10), Op(mult: 1, tl: 0, dr: 12, d2R: 4, sl: 6, rr: 10), Op(mult: 3, tl: 40, dr: 16, sl: 8, rr: 10), Op(mult: 1, tl: 10, dr: 12, d2R: 4, sl: 6, rr: 10)]);

    /// <summary>Four operators in a chain (algorithm 0), decaying to a held level: a round, punchy bass.</summary>
    private static FmPatch Bass { get; } = new(
        "Bass",
        0,
        4,
        [Op(mult: 1, tl: 40, dr: 8), Op(mult: 1, tl: 36, dr: 6), Op(mult: 1, tl: 30, dr: 6, sl: 4), Op(mult: 1, tl: 0, dr: 4, d2R: 2, sl: 4, rr: 10)]);

    /// <summary>Two modulators into one (algorithm 1) with a slow attack: a bowed, string-like swell.</summary>
    private static FmPatch Strings { get; } = new(
        "Strings",
        1,
        3,
        [Op(mult: 1, tl: 38, ar: 14), Op(mult: 3, tl: 44, ar: 14), Op(mult: 1, tl: 32, ar: 14), Op(mult: 1, tl: 0, ar: 14, rr: 6)]);

    /// <summary>A bright modulator pair and OP1 into the carrier (algorithm 2), all decaying: a struck, piano-like note.</summary>
    private static FmPatch Piano { get; } = new(
        "Piano",
        2,
        3,
        [Op(mult: 1, tl: 36, dr: 10, sl: 6), Op(mult: 7, tl: 48, dr: 14, sl: 10), Op(mult: 2, tl: 34, dr: 10, sl: 6), Op(mult: 1, tl: 0, dr: 8, d2R: 3, sl: 5, rr: 8)]);

    /// <summary>A modulator pair and a high modulator into the carrier (algorithm 3), quick decays: a hard, plucked clavinet.</summary>
    private static FmPatch Clav { get; } = new(
        "Clav",
        3,
        6,
        [Op(mult: 3, tl: 30, dr: 12), Op(mult: 1, tl: 34, dr: 12), Op(mult: 5, tl: 40, dr: 14), Op(mult: 1, tl: 0, dr: 12, d2R: 6, sl: 8, rr: 12)]);

    /// <summary>A high modulator on the main carrier and a quiet fourth harmonic beside it (algorithm 4), ringing on: a bell.</summary>
    private static FmPatch Bell { get; } = new(
        "Bell",
        4,
        0,
        [Op(mult: 7, tl: 36, dr: 6, d2R: 2, sl: 4), Op(mult: 1, tl: 4, dr: 4, d2R: 2, sl: 4, rr: 6), Op(mult: 1, tl: 38, dr: 8), Op(mult: 4, tl: 18, dr: 3, d2R: 2, sl: 3, rr: 6)]);

    /// <summary>One modulated carrier and two detuned plain ones (algorithm 6): a wide, chorused synth.</summary>
    private static FmPatch Synth { get; } = new(
        "Synth",
        6,
        5,
        [Op(mult: 1, tl: 24), Op(mult: 1, tl: 10), Op(mult: 2, tl: 16, detune: 1), Op(mult: 1, tl: 14, detune: -1)]);

    /// <summary>Every patch: the simple mapper's five first, then the rest.</summary>
    public static IReadOnlyList<FmPatch> All { get; } = [Sine, Organ, Lead, Brass, Pluck, Bass, Strings, Piano, Clav, Bell, Synth];

    /// <summary>The patch for a sample that loops (a sustained tone), chosen by brightness.</summary>
    public static FmPatch ForSustained(double brightness) => brightness switch
    {
        < 0.10 => Sine,
        < 0.25 => Organ,
        < 0.45 => Lead,
        _ => Brass,
    };

    /// <summary>The patch for a sample that plays once, played by FM because the DAC isn't used: a struck note.</summary>
    public static FmPatch ForOneShot(double brightness) => brightness < 0.45 ? Pluck : Brass;

    /// <summary>A sounding operator: instant-ish attack, held at full sustain unless a decay is given.</summary>
    private static FmOperator Op(int mult, int tl, int detune = 0, int ar = 31, int dr = 0, int d2R = 0, int sl = 0, int rr = 8) =>
        new(mult, detune, tl, 0, ar, false, dr, d2R, sl, rr);

    /// <summary>An operator turned all the way down (TL 127).</summary>
    private static FmOperator Mute() => Op(mult: 1, tl: 127);
}
