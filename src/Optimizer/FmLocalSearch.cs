using Mod2Xgm.Mapping;

namespace Mod2Xgm.Optimizer;

/// <summary>A patch and the LFO rate it plays with (0–7, or <c>null</c> for off).</summary>
/// <param name="Patch">The patch.</param>
/// <param name="LfoRate">The LFO rate; always <c>null</c> when the patch doesn't use the LFO.</param>
internal readonly record struct FmVoiceSetting(FmPatch Patch, int? LfoRate);

/// <summary>
/// The level and envelope fit (docs/references/fm-matching.md, step 3; docs/adr/0069-fm-patch-fitting.md): coordinate
/// descent on the YM2612's integer grid, scored by <see cref="FmEvaluator.Search"/>. Each pass tries every move in a fixed
/// order: TL, multiple, detune, attack, decay, sustain level, second decay, rate scaling and SSG-EG of each operator, the
/// feedback, and with the LFO its rate and depths and each operator's tremolo switch. A move that helps is kept at once,
/// and a step that helps is tried again in the same direction. It stops when a pass helps nowhere, or after
/// <c>maxRenders</c> new renders. The release rate isn't fitted: the key is held through the whole note.
/// </summary>
internal static class FmLocalSearch
{
    /// <summary>A move counts only if it gains more than this, so rounding noise can't make it loop.</summary>
    private const double MinGainDb = 1e-6;

    /// <summary>The steps of a two-operator level move: together or against each other, small and large.</summary>
    private static readonly (int, int)[] PairSteps = [(-1, -1), (-1, 1), (1, -1), (1, 1), (-3, -3), (-3, 3), (3, -3), (3, 3)];

    /// <summary>The modulators' TL stops here: lower would drive them past what the chip can sound cleanly (DDX7's bounded index).</summary>
    private const int MinModulatorTl = 8;

    /// <summary>
    /// Fits <paramref name="start"/>; with <paramref name="lfo"/> the LFO's settings are fitted too, otherwise the result
    /// doesn't use it. <paramref name="ssgEg"/> lets it try SSG-EG shapes.
    /// </summary>
    public static FmVoiceSetting Refine(FmEvaluator evaluator, FmVoiceSetting start, bool lfo, bool ssgEg, int? maxRenders)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        int begin = evaluator.Renders;
        FmVoiceSetting current = lfo ? start : WithoutLfo(start);
        double score = evaluator.Search(current.Patch, current.LfoRate);
        bool improved = true;
        while (improved)
        {
            improved = false;
            foreach (Func<FmVoiceSetting, FmVoiceSetting?> move in Moves(current.Patch, lfo, ssgEg))
            {
                // A step that helps is taken again, in the same direction, while it keeps helping.
                while (move(current) is { } next)
                {
                    if (maxRenders is { } max && evaluator.Renders - begin >= max)
                    {
                        return Canonical(current);
                    }

                    double s = evaluator.Search(next.Patch, next.LfoRate);
                    if (s >= score - MinGainDb)
                    {
                        break;
                    }

                    current = next;
                    score = s;
                    improved = true;
                }
            }
        }

        return Canonical(current);
    }

    /// <summary>The same sound with nothing unused left set: no LFO settings when the LFO is off or no operator takes it.</summary>
    private static FmVoiceSetting Canonical(FmVoiceSetting s) => s.LfoRate is null || !s.Patch.UsesLfo ? WithoutLfo(s) : s;

    /// <summary><paramref name="setting"/> with the LFO off and no depth or tremolo switch left on.</summary>
    public static FmVoiceSetting WithoutLfo(FmVoiceSetting setting)
    {
        FmPatch p = setting.Patch;
        return new FmVoiceSetting(p with { Fms = 0, Ams = 0, Operators = [.. p.Operators.Select(o => o with { Am = false })] }, null);
    }

    /// <summary>Every move from a patch like <paramref name="patch"/>: each returns the moved setting, or <c>null</c> when the move would leave the grid.</summary>
    private static IEnumerable<Func<FmVoiceSetting, FmVoiceSetting?>> Moves(FmPatch patch, bool lfo, bool ssgEg)
    {
        IReadOnlyList<int> carriers = VoiceLevel.Carriers(patch.Algorithm);
        for (int op = 0; op < 4; op++)
        {
            int o = op;
            int minTl = carriers.Contains(op) ? 0 : MinModulatorTl;
            foreach (int step in (int[])[-1, 1, -4, 4])
            {
                yield return Operator(o, x => Stepped(x.Tl, step, minTl, 127) is { } v ? x with { Tl = v } : null);
            }

            foreach (int step in (int[])[-1, 1])
            {
                yield return Operator(o, x => Stepped(x.Mult, step, 0, 15) is { } v ? x with { Mult = v } : null);
                yield return Operator(o, x => Stepped(x.Detune, step, -3, 3) is { } v ? x with { Detune = v } : null);
                yield return Operator(o, x => Stepped(x.Sl, step, 0, 15) is { } v ? x with { Sl = v } : null);
                yield return Operator(o, x => Stepped(x.Rs, step, 0, 3) is { } v ? x with { Rs = v } : null);
            }

            foreach (int step in (int[])[-1, 1, -4, 4])
            {
                yield return Operator(o, x => Stepped(x.Ar, step, 1, 31) is { } v ? x with { Ar = v } : null);
                yield return Operator(o, x => Stepped(x.Dr, step, 0, 31) is { } v ? x with { Dr = v } : null);
                yield return Operator(o, x => Stepped(x.D2R, step, 0, 31) is { } v ? x with { D2R = v } : null);
            }

            if (ssgEg)
            {
                // Off, or one of the eight shapes ($08–$0F); a move turns it on at a shape, steps the shape, or turns it off.
                for (int shape = 8; shape <= 15; shape++)
                {
                    int s = shape;
                    yield return Operator(o, x => x.SsgEg == s ? null : x with { SsgEg = s });
                }

                yield return Operator(o, x => x.SsgEg == 0 ? null : x with { SsgEg = 0 });
            }

            if (lfo)
            {
                yield return Operator(o, x => x with { Am = !x.Am });
            }
        }

        // Two operators' levels at once, every way: a modulator's level and its carrier's trade off, so one alone can make
        // things worse where both together help (where plain coordinate descent stalls).
        for (int a = 0; a < 4; a++)
        {
            for (int b = a + 1; b < 4; b++)
            {
                foreach ((int da, int db) in PairSteps)
                {
                    yield return Levels(a, da, b, db, carriers);
                }
            }
        }

        foreach (int step in (int[])[-1, 1])
        {
            yield return Patch(p => Stepped(p.Feedback, step, 0, 7) is { } v ? p with { Feedback = v } : null);
            if (lfo)
            {
                yield return Patch(p => Stepped(p.Fms, step, 0, 7) is { } v ? p with { Fms = v } : null);
                yield return Patch(p => Stepped(p.Ams, step, 0, 3) is { } v ? p with { Ams = v } : null);
                yield return s => s.LfoRate is { } r && Stepped(r, step, 0, 7) is { } v ? s with { LfoRate = v } : null;
            }
        }

        if (lfo)
        {
            // Turning the LFO on takes a rate and a depth together: either alone changes nothing. Vibrato at three
            // depths, or tremolo on the carriers at two, at each rate.
            for (int rate = 0; rate <= 7; rate++)
            {
                int r = rate;
                foreach (int fms in (int[])[1, 4, 7])
                {
                    int depth = fms;
                    yield return s => s.LfoRate is null ? new FmVoiceSetting(s.Patch with { Fms = depth }, r) : null;
                }

                foreach (int ams in (int[])[1, 3])
                {
                    int depth = ams;
                    yield return s => s.LfoRate is null
                        ? new FmVoiceSetting(s.Patch with { Ams = depth, Operators = [.. s.Patch.Operators.Select((o, i) => carriers.Contains(i) ? o with { Am = true } : o)] }, r)
                        : null;
                }
            }
        }
    }

    /// <summary>A move of two operators' TL at once, each kept within its range (modulators no lower than <see cref="MinModulatorTl"/>).</summary>
    private static Func<FmVoiceSetting, FmVoiceSetting?> Levels(int a, int da, int b, int db, IReadOnlyList<int> carriers) => s =>
    {
        FmOperator[] ops = [.. s.Patch.Operators];
        foreach ((int op, int step) in (ReadOnlySpan<(int, int)>)[(a, da), (b, db)])
        {
            int tl = ops[op].Tl + step;
            if (tl < (carriers.Contains(op) ? 0 : MinModulatorTl) || tl > 127)
            {
                return null;
            }

            ops[op] = ops[op] with { Tl = tl };
        }

        return s with { Patch = s.Patch with { Operators = ops } };
    };

    /// <summary><paramref name="value"/> moved by <paramref name="step"/>, or <c>null</c> outside <paramref name="min"/>–<paramref name="max"/>.</summary>
    private static int? Stepped(int value, int step, int min, int max)
    {
        int moved = value + step;
        return moved >= min && moved <= max ? moved : null;
    }

    /// <summary>A move of operator <paramref name="op"/> only.</summary>
    private static Func<FmVoiceSetting, FmVoiceSetting?> Operator(int op, Func<FmOperator, FmOperator?> change) => s =>
    {
        if (change(s.Patch.Operators[op]) is not { } moved)
        {
            return null;
        }

        FmOperator[] ops = [.. s.Patch.Operators];
        ops[op] = moved;
        return s with { Patch = s.Patch with { Operators = ops } };
    };

    /// <summary>A move of the patch's own settings.</summary>
    private static Func<FmVoiceSetting, FmVoiceSetting?> Patch(Func<FmPatch, FmPatch?> change) => s =>
        change(s.Patch) is { } moved ? s with { Patch = moved } : null;
}
