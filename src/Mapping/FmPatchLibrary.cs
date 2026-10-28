namespace Mod2Xgm.Mapping;

/// <summary>
/// The built-in FM patches of the simple mapper (docs/channel-mapping.md): five hand-made sounds, picked by how bright
/// the sample is. They are <b>placeholders for the fitted patches of plan M8</b> — plausible YM2612 settings written
/// from the chip's documented behavior, to be judged by ear in plan T5.7 and replaced by fitting.
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
        [Op(mult: 2, tl: 34, dr: 14, sl: 8, rr: 10), Op(mult: 1, tl: 0, dr: 12, d2r: 4, sl: 6, rr: 10), Op(mult: 3, tl: 40, dr: 16, sl: 8, rr: 10), Op(mult: 1, tl: 10, dr: 12, d2r: 4, sl: 6, rr: 10)]);

    /// <summary>Every patch, for tests.</summary>
    public static IReadOnlyList<FmPatch> All { get; } = [Sine, Organ, Lead, Brass, Pluck];

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
    private static FmOperator Op(int mult, int tl, int detune = 0, int ar = 31, int dr = 0, int d2r = 0, int sl = 0, int rr = 8) =>
        new(mult, detune, tl, 0, ar, false, dr, d2r, sl, rr);

    /// <summary>An operator turned all the way down (TL 127).</summary>
    private static FmOperator Mute() => Op(mult: 1, tl: 127);
}
