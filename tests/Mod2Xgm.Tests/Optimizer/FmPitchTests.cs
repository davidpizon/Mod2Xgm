using Mod2Xgm.Mapping;
using Mod2Xgm.Optimizer;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="FmPitch"/>: the guard that keeps fitted patches at the note's own pitch.</summary>
public sealed class FmPitchTests
{
    /// <summary><see cref="FmPatchLibrary.Lead"/> with the given multiples (0 = ½).</summary>
    private static FmPatch Lead(params int[] mults) =>
        FmPatchLibrary.Lead with { Operators = [.. FmPatchLibrary.Lead.Operators.Select((o, i) => o with { Mult = mults[i] })] };

    /// <summary>A patch keeps the pitch when its sounding multiples share no factor and none is ½.</summary>
    [Theory]
    [InlineData(1, 1, 1, 1, true)]
    [InlineData(1, 2, 4, 1, true)]
    [InlineData(2, 3, 2, 2, true)]
    [InlineData(2, 2, 2, 2, false)]
    [InlineData(3, 6, 3, 9, false)]
    [InlineData(1, 1, 1, 0, false)]
    [InlineData(0, 0, 0, 0, false)]
    public void APatchKeepsThePitchWhenItsMultiplesShareNoFactor(int m1, int m2, int m3, int m4, bool keeps)
    {
        Assert.Equal(keeps, FmPitch.Keeps(Lead(m1, m2, m3, m4)));
    }

    /// <summary>Silent operators don't count: a CH3 half is judged by its own two operators.</summary>
    [Fact]
    public void SilentOperatorsDontCount()
    {
        FmPatch half = Lead(1, 1, 2, 2) with { Operators = [.. Lead(1, 1, 2, 2).Operators.Select((o, i) => i < 2 ? o with { Tl = 127 } : o)] };

        Assert.False(FmPitch.Keeps(half));
        Assert.Equal([1, 1, 1, 1], FmPitch.Normalized(half)!.Operators.Select(o => o.Mult));
    }

    /// <summary>Normalizing divides the shared factor out (½ counts as half a step), and gives up past multiple 15.</summary>
    [Theory]
    [InlineData(new[] { 2, 2, 2, 2 }, new[] { 1, 1, 1, 1 })]
    [InlineData(new[] { 3, 6, 3, 9 }, new[] { 1, 2, 1, 3 })]
    [InlineData(new[] { 0, 1, 1, 3 }, new[] { 1, 2, 2, 6 })]
    [InlineData(new[] { 1, 2, 4, 1 }, new[] { 1, 2, 4, 1 })]
    public void NormalizingDividesTheSharedFactorOut(int[] mults, int[] expected)
    {
        FmPatch normalized = FmPitch.Normalized(Lead(mults))!;

        Assert.Equal(expected, normalized.Operators.Select(o => o.Mult));
        Assert.True(FmPitch.Keeps(normalized));
        FmStructure structure = new(4, 0, mults[0], mults[1], mults[2], mults[3], FmStructure.DefaultModulatorTl);
        Assert.Equal(expected, FmPitch.Normalized(structure) is { } s ? [s.Mult1, s.Mult2, s.Mult3, s.Mult4] : null);
    }

    /// <summary>A ½ beside a multiple above 7 can't be doubled onto the grid, so there is no normalized patch.</summary>
    [Fact]
    public void NormalizingGivesUpPastFifteen()
    {
        Assert.Null(FmPitch.Normalized(Lead(0, 8, 1, 1)));
        Assert.Null(FmPitch.Normalized(new FmStructure(4, 0, 0, 8, 1, 1, FmStructure.DefaultModulatorTl)));
    }

    /// <summary>
    /// The level fit never moves into a patch off the note's pitch, even toward a target that sounds an octave up: one step
    /// of OP1 from 1 to 2 would match it, and play every note an octave high.
    /// </summary>
    [Fact]
    public void TheLevelFitNeverLeavesTheNotesPitch()
    {
        FmEvaluator evaluator = new(FmTargets.Of(Lead(2, 2, 2, 2)));

        FmVoiceSetting fitted = FmLocalSearch.Refine(evaluator, new FmVoiceSetting(Lead(1, 2, 2, 2), null), false, false, 150);

        Assert.True(FmPitch.Keeps(fitted.Patch), string.Join(' ', fitted.Patch.Operators.Select(o => o.Mult)));
    }
}
