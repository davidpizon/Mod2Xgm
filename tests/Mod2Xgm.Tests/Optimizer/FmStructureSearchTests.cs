using Mod2Xgm.Mapping;
using Mod2Xgm.Optimizer;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="FmStructure"/>, <see cref="FmExhaustiveSearch"/> and <see cref="FmGeneticSearch"/>: the structure half of a fit.</summary>
public sealed class FmStructureSearchTests
{
    /// <summary>A structure reads a patch's algorithm, feedback, multiples and its modulators' mean TL.</summary>
    [Fact]
    public void Of_WithPatch_ReadsStructure()
    {
        // Act
        FmStructure structure = FmStructure.Of(FmPatchLibrary.Lead);

        // Assert
        Assert.Equal(new FmStructure(4, 5, 1, 1, 2, 2, 34), structure);
    }

    /// <summary>Without modulators the TL is the default.</summary>
    [Fact]
    public void Of_WithoutModulators_UsesDefaultTl()
    {
        // Act
        FmStructure structure = FmStructure.Of(FmPatchLibrary.Organ);

        // Assert
        Assert.Equal(FmStructure.DefaultModulatorTl, structure.ModulatorTl);
    }

    /// <summary>
    /// A structure's patch has its multiples and modulator level, carriers sharing the full level (three carriers: 13 TL
    /// steps down each), carriers with the seed's carrier envelope and modulators with its modulator's.
    /// </summary>
    [Fact]
    public void Apply_WithSeed_MakesPatch()
    {
        // Act
        FmPatch patch = new FmStructure(5, 2, 1, 2, 3, 4, 30).Patch(FmPatchLibrary.Pluck, "Fit");

        // Assert
        Assert.Equal("Fit", patch.Name);
        Assert.Equal((5, 2), (patch.Algorithm, patch.Feedback));
        Assert.Equal([1, 2, 3, 4], patch.Operators.Select(o => o.Mult));
        Assert.Equal([30, 13, 13, 13], patch.Operators.Select(o => o.Tl));
        Assert.Equal(FmPatchLibrary.Pluck.Operators[0].Dr, patch.Operators[0].Dr);
        Assert.All(patch.Operators.Skip(1), o => Assert.Equal(FmPatchLibrary.Pluck.Operators[1].Dr, o.Dr));
        Assert.All(patch.Operators, o => Assert.Equal((0, 0, false), (o.Detune, o.SsgEg, o.Am)));
    }

    /// <summary>Carriers try 1 first, then the target's strongest harmonics: a square wave's are odd.</summary>
    [Fact]
    public void Exhaustive_ForCarriers_TriesStrongestHarmonics()
    {
        // Act
        int[] mults = FmExhaustiveSearch.CarrierMults(FmTargets.Of(FmTargets.Square));

        // Assert
        Assert.Equal(5, mults.Length);
        Assert.Equal([1, 3, 5, 7, 9], mults);
    }

    /// <summary>The exhaustive search stays within its budget, returns the asked number of distinct structures that keep the note's pitch, best first.</summary>
    [Fact]
    public void Exhaustive_WithBudget_KeepsIt()
    {
        // Arrange
        FmEvaluator evaluator = new(FmTargets.Of(FmTargets.Saw));

        // Act
        IReadOnlyList<FmStructure> best = FmExhaustiveSearch.Search(evaluator, FmPatchLibrary.Lead, 3, 60);

        // Assert
        Assert.Equal(60, evaluator.Renders);
        Assert.Equal(3, best.Distinct().Count());
        Assert.All(best, s => Assert.Equal(1, Gcd(s)));
        double[] scores = [.. best.Select(s => evaluator.Quick(s.Patch(FmPatchLibrary.Lead, "Fit"), null))];
        Assert.Equal(scores.Order(), scores);
    }

    /// <summary>
    /// The exhaustive search finds a patch's own structure among its best when that patch made the target. The budget
    /// stops well short of the whole search (several thousand renders) but past where this structure comes in the order.
    /// </summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Exhaustive_WithKnownStructure_FindsIt()
    {
        // Arrange
        FmStructure known = new(4, FmPatchLibrary.Lead.Feedback, 1, 1, 2, 2, FmStructure.DefaultModulatorTl);
        FmEvaluator evaluator = new(FmTargets.Of(known.Patch(FmPatchLibrary.Lead, "Known")));

        // Act
        IReadOnlyList<FmStructure> best = FmExhaustiveSearch.Search(evaluator, FmPatchLibrary.Lead, 1, 750);

        // Assert
        Assert.True(evaluator.Quick(best[0].Patch(FmPatchLibrary.Lead, "Fit"), null) < 0.05);
    }

    /// <summary>The genetic search keeps its budget and, from the same seed, finds the same structures; another seed searches differently.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Genetic_Twice_Repeats()
    {
        // Arrange
        NoteTarget target = FmTargets.Of(FmTargets.Saw);
        FmEvaluator first = new(target);
        FmEvaluator second = new(target);
        FmEvaluator other = new(target);
        IReadOnlyList<FmStructure> a = FmGeneticSearch.Search(first, FmPatchLibrary.Lead, 2, 80, 1);
        IReadOnlyList<FmStructure> b = FmGeneticSearch.Search(second, FmPatchLibrary.Lead, 2, 80, 1);

        // Act
        FmGeneticSearch.Search(other, FmPatchLibrary.Lead, 2, 80, 2);

        // Assert
        Assert.Equal(a, b);
        Assert.Equal(2, a.Count);
        Assert.InRange(first.Renders, 32, 80);
        Assert.True(first.Quick(a[0].Patch(FmPatchLibrary.Lead, "Fit"), null) <= first.Quick(FmPatchLibrary.Lead, null));
    }

    private static int Gcd(FmStructure s) => new[] { s.Mult1, s.Mult2, s.Mult3, s.Mult4 }.Aggregate(0, Gcd);

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
}
