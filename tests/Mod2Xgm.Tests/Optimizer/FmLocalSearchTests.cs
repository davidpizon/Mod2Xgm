using Mod2Xgm.Mapping;
using Mod2Xgm.Optimizer;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="FmLocalSearch"/>: the level and envelope fit.</summary>
public sealed class FmLocalSearchTests
{
    /// <summary><see cref="FmPatchLibrary.Lead"/> with its levels and envelope pushed off: the fit should find its way back.</summary>
    private static FmPatch OffLead { get; } = FmPatchLibrary.Lead with
    {
        Operators = [.. FmPatchLibrary.Lead.Operators.Select((o, i) => o with { Tl = Math.Min(127, o.Tl + (i % 2 == 0 ? 12 : 3)), Dr = o.Dr + 6 })],
    };

    /// <summary>From a patch knocked off its target, the fit gets much closer, and never further.</summary>
    [Fact]
    public void TheFitGetsCloser()
    {
        FmEvaluator evaluator = new(FmTargets.Of(FmPatchLibrary.Lead));
        double before = evaluator.Search(OffLead, null);

        FmVoiceSetting fitted = FmLocalSearch.Refine(evaluator, new FmVoiceSetting(OffLead, null), false, false, null);

        double after = evaluator.Search(fitted.Patch, fitted.LfoRate);
        Assert.True(after < before / 3, $"{before} dB → {after} dB");
        Assert.Equal(OffLead.Algorithm, fitted.Patch.Algorithm);
    }

    /// <summary>The fit stops at its budget, and leaves a patch that already matches as it is.</summary>
    [Fact]
    public void TheFitKeepsItsBudget()
    {
        FmEvaluator evaluator = new(FmTargets.Of(FmPatchLibrary.Lead));

        FmLocalSearch.Refine(evaluator, new FmVoiceSetting(OffLead, null), false, true, 25);
        FmVoiceSetting same = FmLocalSearch.Refine(new FmEvaluator(FmTargets.Of(FmPatchLibrary.Organ)), new FmVoiceSetting(FmPatchLibrary.Organ, null), false, false, 40);

        Assert.InRange(evaluator.Renders, 25, 26);
        Assert.Equal(FmEvaluator.Key(FmPatchLibrary.Organ, null), FmEvaluator.Key(same.Patch, same.LfoRate));
    }

    /// <summary>A target with vibrato is matched better with the LFO than without; without it, the fit leaves every LFO setting off.</summary>
    [Fact]
    public void TheLfoIsFittedOnlyWhenAllowed()
    {
        FmPatch vibrato = FmPatchLibrary.Sine with { Fms = 6 };
        FmEvaluator evaluator = new(FmTargets.Of(vibrato, 440, lfoRate: 6));

        FmVoiceSetting plain = FmLocalSearch.Refine(evaluator, new FmVoiceSetting(FmPatchLibrary.Sine, null), false, false, 300);
        FmVoiceSetting wobbling = FmLocalSearch.Refine(evaluator, plain, true, false, 300);

        Assert.Null(plain.LfoRate);
        Assert.False(plain.Patch.UsesLfo);
        Assert.NotNull(wobbling.LfoRate);
        Assert.True(wobbling.Patch.Fms > 0);
        Assert.True(evaluator.Search(wobbling.Patch, wobbling.LfoRate) < evaluator.Search(plain.Patch, plain.LfoRate));
    }

    /// <summary>Taking the LFO away clears its depths and every operator's tremolo switch.</summary>
    [Fact]
    public void WithoutLfoClearsEverything()
    {
        FmPatch patch = FmPatchLibrary.Lead with { Fms = 3, Ams = 2, Operators = [.. FmPatchLibrary.Lead.Operators.Select(o => o with { Am = true })] };

        FmVoiceSetting plain = FmLocalSearch.WithoutLfo(new FmVoiceSetting(patch, 5));

        Assert.Null(plain.LfoRate);
        Assert.Equal((0, 0), (plain.Patch.Fms, plain.Patch.Ams));
        Assert.All(plain.Patch.Operators, o => Assert.False(o.Am));
    }
}
