using Mod2Xgm.Mapping;

namespace Mod2Xgm.Optimizer;

/// <summary>
/// The pitch guard on fitted patches (docs/adr/0075-fm-pitch-guard.md): a patch must repeat at the note's own pitch. Its
/// sound is made of the operators' frequencies, multiple × the note, and repeats at their greatest common divisor. An
/// operator sounds when it isn't silent and reaches an output through operators that aren't either: a modulator behind a
/// silent carrier adds nothing. So a multiple of ½ (register value 0) on any sounding operator, or a factor every sounding multiple shares (2 and 2, 3 and
/// 6), moves the note an octave or more however the levels are set. The harmonic score can't see it: it reads the
/// target's harmonics, which the shifted patch also has, or misses the sub-octave between them.
/// </summary>
internal static class FmPitch
{
    /// <summary>A silent operator (TL 127) adds no frequency, and passes on none from the operators that feed it.</summary>
    private const int SilentTl = 127;

    /// <summary>Register value of the ½ multiple.</summary>
    private const int HalfMult = 0;

    /// <summary>The largest multiple the chip has.</summary>
    private const int MaxMult = 15;

    /// <summary>
    /// Per algorithm, the operators that modulate each operator directly (0 = OP1), OP1 to OP4 (docs/genesis-audio.md).
    /// </summary>
    private static readonly int[][][] Inputs =
    [
        [[], [0], [1], [2]],
        [[], [], [0, 1], [2]],
        [[], [], [1], [0, 2]],
        [[], [0], [], [1, 2]],
        [[], [0], [], [2]],
        [[], [0], [0], [0]],
        [[], [0], [], []],
        [[], [], [], []],
    ];

    /// <summary>True when <paramref name="patch"/>'s sounding operators repeat exactly at the note's pitch.</summary>
    public static bool Keeps(FmPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        // In half steps (½ = 1, 1 = 2, 2 = 4, …) the common divisor is 2 exactly when the sound repeats at the note.
        int[] halves = [.. Sounding(patch).Select(o => Halves(o.Mult))];
        return halves.Length == 0 || halves.Aggregate(0, FmExhaustiveSearch.Gcd) == 2;
    }

    /// <summary>
    /// <paramref name="patch"/> with its sounding multiples divided by what they share, so it plays at the note with the
    /// same tone (2 and 2 become 1 and 1; ½ and 1 become 1 and 2); itself when it already keeps the pitch, or <c>null</c>
    /// when the change would need a multiple above 15.
    /// </summary>
    public static FmPatch? Normalized(FmPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (Keeps(patch))
        {
            return patch;
        }

        bool[] sounding = SoundingMask(patch);
        int divisor = Sounding(patch).Select(o => Halves(o.Mult)).Aggregate(0, FmExhaustiveSearch.Gcd);
        FmOperator[] ops = [.. patch.Operators];
        for (int i = 0; i < ops.Length; i++)
        {
            if (!sounding[i])
            {
                continue;
            }

            // Half steps over the divisor give whole multiples: the lowest common partial lands on the note.
            int mult = Halves(ops[i].Mult) / divisor;
            if (mult > MaxMult)
            {
                return null;
            }

            ops[i] = ops[i] with { Mult = mult };
        }

        return patch with { Operators = ops };
    }

    /// <summary>The multiples of a structure (all four operators sound) divided by what they share, as <see cref="Normalized(FmPatch)"/> does; <c>null</c> when that needs a multiple above 15.</summary>
    public static FmStructure? Normalized(FmStructure structure)
    {
        int[] halves = [Halves(structure.Mult1), Halves(structure.Mult2), Halves(structure.Mult3), Halves(structure.Mult4)];
        int divisor = halves.Aggregate(0, FmExhaustiveSearch.Gcd);
        if (divisor == 2)
        {
            return structure;
        }

        int[] mults = [.. halves.Select(h => h / divisor)];
        return mults.Any(m => m > MaxMult)
            ? null
            : structure with { Mult1 = mults[0], Mult2 = mults[1], Mult3 = mults[2], Mult4 = mults[3] };
    }

    private static IEnumerable<FmOperator> Sounding(FmPatch patch)
    {
        bool[] sounding = SoundingMask(patch);
        return patch.Operators.Where((_, i) => sounding[i]);
    }

    /// <summary>
    /// Which operators of <paramref name="patch"/> are heard: traced back from each carrier that isn't silent through the
    /// operators feeding it, stopping at a silent one.
    /// </summary>
    private static bool[] SoundingMask(FmPatch patch)
    {
        bool[] sounding = new bool[patch.Operators.Count];
        Stack<int> pending = new(VoiceLevel.Carriers(patch.Algorithm));
        while (pending.TryPop(out int op))
        {
            if (sounding[op] || patch.Operators[op].Tl >= SilentTl)
            {
                continue;
            }

            sounding[op] = true;
            foreach (int input in Inputs[patch.Algorithm][op])
            {
                pending.Push(input);
            }
        }

        return sounding;
    }

    /// <summary>A multiple's register value in half steps of the note: ½ is 1, 1 is 2, 15 is 30.</summary>
    private static int Halves(int mult) => mult == HalfMult ? 1 : 2 * mult;
}
