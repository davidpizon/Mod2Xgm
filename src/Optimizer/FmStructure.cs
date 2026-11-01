using Mod2Xgm.Mapping;

namespace Mod2Xgm.Optimizer;

/// <summary>
/// The discrete part of an FM patch that the structure searches choose (docs/references/fm-matching.md, step 2): how the
/// operators connect, OP1's feedback, each operator's frequency multiple, and how hard the modulators drive. The level
/// fit (<see cref="FmLocalSearch"/>) then tunes everything else.
/// </summary>
/// <param name="Algorithm">Connection of the operators, 0–7.</param>
/// <param name="Feedback">OP1 feedback, 0–7.</param>
/// <param name="Mult1">OP1's multiple, 0–15 (0 = ½).</param>
/// <param name="Mult2">OP2's multiple.</param>
/// <param name="Mult3">OP3's multiple.</param>
/// <param name="Mult4">OP4's multiple.</param>
/// <param name="ModulatorTl">Every modulator's TL, 0–127: lower drives harder, so more harmonics.</param>
internal readonly record struct FmStructure(int Algorithm, int Feedback, int Mult1, int Mult2, int Mult3, int Mult4, int ModulatorTl)
{
    /// <summary>The modulators' TL when a search doesn't choose one: about −21 dB, a moderate modulation index.</summary>
    public const int DefaultModulatorTl = 28;

    /// <summary>TL of one step, in dB (docs/genesis-audio.md).</summary>
    private const double TlStepDb = 0.75;

    /// <summary>The multiple of operator <paramref name="op"/> (0–3, Yamaha order).</summary>
    private int Mult(int op) => op switch
    {
        0 => Mult1,
        1 => Mult2,
        2 => Mult3,
        _ => Mult4,
    };

    /// <summary>The structure of <paramref name="patch"/>: its modulators' TL is their mean, or the default when it has none.</summary>
    public static FmStructure Of(FmPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        IReadOnlyList<int> carriers = VoiceLevel.Carriers(patch.Algorithm);
        int[] modulators = [.. Enumerable.Range(0, 4).Where(op => !carriers.Contains(op)).Select(op => patch.Operators[op].Tl)];
        int tl = modulators.Length == 0 ? DefaultModulatorTl : (int)Math.Round(modulators.Average());
        IReadOnlyList<FmOperator> ops = patch.Operators;
        return new FmStructure(patch.Algorithm, patch.Feedback, ops[0].Mult, ops[1].Mult, ops[2].Mult, ops[3].Mult, tl);
    }

    /// <summary>
    /// The patch to judge this structure by: carriers take <paramref name="seed"/>'s first carrier's envelope, modulators
    /// its first modulator's (or the carrier's, when it has none), with no detune, SSG-EG or LFO. The carriers share the
    /// channel's full level, so several don't clip: each is 20·log10(n) dB down.
    /// </summary>
    public FmPatch Patch(FmPatch seed, string name)
    {
        ArgumentNullException.ThrowIfNull(seed);
        IReadOnlyList<int> seedCarriers = VoiceLevel.Carriers(seed.Algorithm);
        FmOperator seedCarrier = seed.Operators[seedCarriers[0]];
        FmOperator seedModulator = Enumerable.Range(0, 4).Where(op => !seedCarriers.Contains(op)).Select(op => seed.Operators[op]).FirstOrDefault() ?? seedCarrier;
        IReadOnlyList<int> carriers = VoiceLevel.Carriers(Algorithm);
        int carrierTl = (int)Math.Round(20 * Math.Log10(carriers.Count) / TlStepDb);
        FmOperator[] ops = new FmOperator[4];
        for (int op = 0; op < 4; op++)
        {
            bool carrier = carriers.Contains(op);
            FmOperator envelope = carrier ? seedCarrier : seedModulator;
            ops[op] = envelope with { Mult = Mult(op), Detune = 0, Tl = carrier ? carrierTl : ModulatorTl, Am = false, SsgEg = 0 };
        }

        return new FmPatch(name, Algorithm, Feedback, ops);
    }
}
