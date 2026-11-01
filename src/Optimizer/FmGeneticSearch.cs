using Mod2Xgm.Mapping;

namespace Mod2Xgm.Optimizer;

/// <summary>
/// The genetic structure search (Horner, Beauchamp &amp; Haken 1993, docs/references/horner-1993-ga-fm-matching.md;
/// docs/adr/0069-fm-patch-fitting.md): a population of <see cref="FmStructure"/>s, each judged by
/// <see cref="FmEvaluator.Quick"/>, bred by tournament selection, uniform crossover and mutation, with the best kept
/// from one generation to the next. Unlike the exhaustive search it also chooses the feedback and how hard the
/// modulators drive. Its random choices come from one seeded generator, so a seed always gives the same structures.
/// </summary>
internal static class FmGeneticSearch
{
    private const int Population = 32;

    /// <summary>The best of a generation, carried over unchanged.</summary>
    private const int Elites = 2;

    private const int TournamentSize = 3;

    /// <summary>Each gene of a child mutates with this chance.</summary>
    private const double MutationRate = 1.0 / 6;

    /// <summary>The search stops after this many generations without a better structure.</summary>
    private const int StallGenerations = 12;

    /// <summary>The search stops after this many generations in any case.</summary>
    private const int MaxGenerations = 150;

    /// <summary>The modulator levels the search chooses from (TL): strong to gentle modulation.</summary>
    private static readonly int[] ModulatorTls = [16, 22, 28, 34, 40];

    /// <summary>
    /// The <paramref name="keep"/> best structures found, best first. The first generation holds <paramref name="seed"/>'s
    /// structure, one structure per algorithm with every multiple 1, and random ones. It stops after
    /// <see cref="StallGenerations"/> generations without improvement, <see cref="MaxGenerations"/> in all, or
    /// <paramref name="maxRenders"/> new renders (<c>null</c>: no limit).
    /// </summary>
    public static IReadOnlyList<FmStructure> Search(FmEvaluator evaluator, FmPatch seed, int keep, int? maxRenders, int randomSeed)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(seed);
        Random random = new(randomSeed);
        Dictionary<FmStructure, double> scores = [];
        int start = evaluator.Renders;
        bool Spent() => maxRenders is { } max && evaluator.Renders - start >= max;
        double Fitness(FmStructure s)
        {
            if (!scores.TryGetValue(s, out double score))
            {
                score = evaluator.Quick(s.Patch(seed, "Fit"), null);
                scores[s] = score;
            }

            return score;
        }

        FmStructure seeded = FmStructure.Of(seed);
        List<FmStructure> population = [seeded with { ModulatorTl = Nearest(seeded.ModulatorTl) }];
        for (int algorithm = 0; algorithm < 8 && population.Count < Population; algorithm++)
        {
            population.Add(new FmStructure(algorithm, seed.Feedback, 1, 1, 1, 1, FmStructure.DefaultModulatorTl));
        }

        while (population.Count < Population)
        {
            population.Add(RandomStructure(random));
        }

        double best = double.MaxValue;
        int stall = 0;
        for (int generation = 0; generation < MaxGenerations && stall < StallGenerations; generation++)
        {
            List<(FmStructure Structure, double Score)> ranked = [];
            foreach (FmStructure s in population)
            {
                if (Spent() && !scores.ContainsKey(s))
                {
                    continue;
                }

                ranked.Add((s, Fitness(s)));
            }

            if (ranked.Count == 0)
            {
                break;
            }

            ranked.Sort((a, b) => a.Score.CompareTo(b.Score));
            if (ranked[0].Score < best - 1e-9)
            {
                best = ranked[0].Score;
                stall = 0;
            }
            else
            {
                stall++;
            }

            if (Spent())
            {
                break;
            }

            List<FmStructure> next = [.. ranked.Take(Elites).Select(r => r.Structure)];
            while (next.Count < Population)
            {
                FmStructure a = Tournament(ranked, random);
                FmStructure b = Tournament(ranked, random);
                next.Add(Mutate(Cross(a, b, random), random));
            }

            population = next;
        }

        // A stable sort over the dictionary's insertion order (nothing is removed), so ties break the same way every run.
        return [.. scores.OrderBy(s => s.Value).Take(keep).Select(s => s.Key)];
    }

    private static FmStructure Tournament(List<(FmStructure Structure, double Score)> ranked, Random random)
    {
        int best = random.Next(ranked.Count);
        for (int i = 1; i < TournamentSize; i++)
        {
            best = Math.Min(best, random.Next(ranked.Count));
        }

        // The list is sorted best first, so the lowest index wins.
        return ranked[best].Structure;
    }

    /// <summary>Each gene from either parent, evenly.</summary>
    private static FmStructure Cross(FmStructure a, FmStructure b, Random random) => new(
        random.Next(2) == 0 ? a.Algorithm : b.Algorithm,
        random.Next(2) == 0 ? a.Feedback : b.Feedback,
        random.Next(2) == 0 ? a.Mult1 : b.Mult1,
        random.Next(2) == 0 ? a.Mult2 : b.Mult2,
        random.Next(2) == 0 ? a.Mult3 : b.Mult3,
        random.Next(2) == 0 ? a.Mult4 : b.Mult4,
        random.Next(2) == 0 ? a.ModulatorTl : b.ModulatorTl);

    private static FmStructure Mutate(FmStructure s, Random random) => new(
        random.NextDouble() < MutationRate ? random.Next(8) : s.Algorithm,
        random.NextDouble() < MutationRate ? Math.Clamp(s.Feedback + (random.Next(2) == 0 ? -1 : 1), 0, 7) : s.Feedback,
        MutateMult(s.Mult1, random),
        MutateMult(s.Mult2, random),
        MutateMult(s.Mult3, random),
        MutateMult(s.Mult4, random),
        random.NextDouble() < MutationRate ? ModulatorTls[random.Next(ModulatorTls.Length)] : s.ModulatorTl);

    /// <summary>A multiple that mutates moves one step, or now and then jumps anywhere from ½ to 15.</summary>
    private static int MutateMult(int mult, Random random)
    {
        if (random.NextDouble() >= MutationRate)
        {
            return mult;
        }

        return random.NextDouble() < 0.3 ? random.Next(16) : Math.Clamp(mult + (random.Next(2) == 0 ? -1 : 1), 0, 15);
    }

    /// <summary>Any algorithm and feedback, multiples mostly small (1–8), as most instruments use.</summary>
    private static FmStructure RandomStructure(Random random) => new(
        random.Next(8),
        random.Next(8),
        random.Next(1, 9),
        random.Next(1, 9),
        random.Next(1, 9),
        random.Next(1, 9),
        ModulatorTls[random.Next(ModulatorTls.Length)]);

    private static int Nearest(int tl) => ModulatorTls.MinBy(t => Math.Abs(t - tl));
}
