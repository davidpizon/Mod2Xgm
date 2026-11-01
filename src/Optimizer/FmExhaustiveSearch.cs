using Mod2Xgm.Mapping;

namespace Mod2Xgm.Optimizer;

/// <summary>
/// The pruned exhaustive structure search (docs/references/fm-matching.md, step 2; docs/adr/0069-fm-patch-fitting.md):
/// every algorithm, with carriers on the target's strongest harmonics and modulators on small multiples, judged by
/// <see cref="FmEvaluator.Quick"/>. Pruned three ways: carriers only take the target's strong harmonics, every structure
/// must keep the note's pitch (the multiples share no factor), and operators that play the same part in an algorithm
/// are tried in one order only. Detune is left to the level fit. The likeliest structures go first, so a limited
/// budget still tries the best guesses.
/// </summary>
internal static class FmExhaustiveSearch
{
    /// <summary>Carriers take at most this many multiples: the strongest harmonics.</summary>
    private const int CarrierChoices = 5;

    /// <summary>The highest multiple a carrier can take.</summary>
    private const int MaxCarrierMult = 15;

    /// <summary>The multiples a modulator can take: the small ones, which keep the sidebands on the harmonics (Horner 1993).</summary>
    private static readonly int[] ModulatorMults = [1, 2, 3, 4, 5, 7];

    /// <summary>
    /// Operators that play the same part in each algorithm (Yamaha order, 0 = OP1), whose multiples are tried in rising
    /// order only. OP1 is never among them: its feedback sets it apart.
    /// </summary>
    private static readonly int[][][] Interchangeable =
    [
        [], [], [], [], [],
        [[1, 2, 3]],
        [[2, 3]],
        [[1, 2, 3]],
    ];

    /// <summary>
    /// The <paramref name="keep"/> best structures by the quick score, best first, after trying at most
    /// <paramref name="maxRenders"/> new renders (<c>null</c>: all of them). Feedback is <paramref name="seed"/>'s.
    /// </summary>
    public static IReadOnlyList<FmStructure> Search(FmEvaluator evaluator, FmPatch seed, int keep, int? maxRenders)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(seed);
        int[] carrierMults = CarrierMults(evaluator.Target);
        List<(FmStructure Structure, int Prior)> candidates = [];
        for (int algorithm = 0; algorithm < 8; algorithm++)
        {
            Add(candidates, algorithm, seed.Feedback, carrierMults, new int[4], 0, 0);
        }

        // A stable sort: equal priors keep the algorithm order.
        FmStructure[] ordered = [.. candidates.OrderBy(c => c.Prior).Select(c => c.Structure)];
        int start = evaluator.Renders;
        List<(FmStructure Structure, double Score)> scored = [];
        foreach (FmStructure structure in ordered)
        {
            if (maxRenders is { } max && evaluator.Renders - start >= max)
            {
                break;
            }

            scored.Add((structure, evaluator.Quick(structure.Patch(seed, "Fit"), null)));
        }

        return [.. scored.OrderBy(s => s.Score).Take(keep).Select(s => s.Structure)];
    }

    /// <summary>
    /// The multiples carriers try: 1, then the target's strongest other harmonics up to <see cref="MaxCarrierMult"/>, by
    /// their power over the whole region; 1–4 without a pitch.
    /// </summary>
    internal static int[] CarrierMults(NoteTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Spectrum.HarmonicsDb is not { Count: > 0 } frames)
        {
            return [1, 2, 3, 4];
        }

        int count = Math.Min(MaxCarrierMult, frames[0].Length);
        double[] power = new double[count];
        foreach (float[] frame in frames)
        {
            for (int k = 0; k < count; k++)
            {
                power[k] += Math.Pow(10, frame[k] / 10.0);
            }
        }

        return [1, .. Enumerable.Range(2, count - 1).OrderByDescending(k => power[k - 1]).Take(CarrierChoices - 1)];
    }

    /// <summary>Adds every structure of <paramref name="algorithm"/> from operator <paramref name="op"/> on, with the multiples so far in <paramref name="mults"/>.</summary>
    private static void Add(List<(FmStructure, int)> candidates, int algorithm, int feedback, int[] carrierMults, int[] mults, int op, int prior)
    {
        IReadOnlyList<int> carriers = VoiceLevel.Carriers(algorithm);
        if (op == 4)
        {
            if (Gcd(mults) == 1)
            {
                candidates.Add((new FmStructure(algorithm, feedback, mults[0], mults[1], mults[2], mults[3], FmStructure.DefaultModulatorTl), prior));
            }

            return;
        }

        int[] choices = carriers.Contains(op) ? carrierMults : ModulatorMults;
        int floor = Interchangeable[algorithm].FirstOrDefault(g => g.Contains(op) && g[0] != op) is { } group
            ? mults[group[Array.IndexOf(group, op) - 1]]
            : 0;
        for (int i = 0; i < choices.Length; i++)
        {
            if (choices[i] < floor)
            {
                continue;
            }

            mults[op] = choices[i];
            Add(candidates, algorithm, feedback, carrierMults, mults, op + 1, prior + i);
        }
    }

    private static int Gcd(int[] values) => values.Aggregate(0, Gcd);

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
}
