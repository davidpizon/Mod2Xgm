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
    public void Keeps_WithCoprimeMultiples_ReturnsTrue(int m1, int m2, int m3, int m4, bool keeps)
    {
        // Act & Assert
        Assert.Equal(keeps, FmPitch.Keeps(Lead(m1, m2, m3, m4)));
    }

    /// <summary>Silent operators don't count: a CH3 half is judged by its own two operators.</summary>
    [Fact]
    public void Keeps_WithSilentOperators_IgnoresThem()
    {
        // Act
        FmPatch half = Lead(1, 1, 2, 2) with { Operators = [.. Lead(1, 1, 2, 2).Operators.Select((o, i) => i < 2 ? o with { Tl = 127 } : o)] };

        // Assert
        Assert.False(FmPitch.Keeps(half));
        Assert.Equal([1, 1, 1, 1], FmPitch.Normalized(half)!.Operators.Select(o => o.Mult));
    }

    /// <summary>
    /// A modulator behind a silent carrier doesn't count either: with OP2 muted only OP3 → OP4 is heard (algorithm 4), or
    /// only OP4 is (algorithm 0's chain), and their 2 2 plays an octave up even though OP1 is ×3. Normalizing leaves OP1 alone.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(0)]
    public void Keeps_BehindSilentCarrier_IgnoresOperators(int algorithm)
    {
        // Arrange
        FmPatch lead = Lead(3, 1, 2, 2) with { Algorithm = algorithm };

        // Act
        FmPatch muted = lead with { Operators = [.. lead.Operators.Select((o, i) => i == 1 ? o with { Tl = 127 } : o)] };

        // Assert
        Assert.True(FmPitch.Keeps(lead));
        Assert.False(FmPitch.Keeps(muted));
        Assert.Equal([3, 1, 1, 1], FmPitch.Normalized(muted)!.Operators.Select(o => o.Mult));
    }

    /// <summary>Normalizing a patch divides the shared factor out (½ counts as half a step), and the result keeps the pitch.</summary>
    [Theory]
    [InlineData(new[] { 2, 2, 2, 2 }, new[] { 1, 1, 1, 1 })]
    [InlineData(new[] { 3, 6, 3, 9 }, new[] { 1, 2, 1, 3 })]
    [InlineData(new[] { 0, 1, 1, 3 }, new[] { 1, 2, 2, 6 })]
    [InlineData(new[] { 1, 2, 4, 1 }, new[] { 1, 2, 4, 1 })]
    public void Normalized_WithSharedFactor_DividesItOut(int[] mults, int[] expected)
    {
        // Act
        FmPatch? normalized = FmPitch.Normalized(Lead(mults));

        // Assert
        Assert.NotNull(normalized);
        Assert.Equal(expected, normalized.Operators.Select(o => o.Mult));
        Assert.True(FmPitch.Keeps(normalized));
    }

    /// <summary>Normalizing a structure divides the same factor out.</summary>
    [Theory]
    [InlineData(new[] { 2, 2, 2, 2 }, new[] { 1, 1, 1, 1 })]
    [InlineData(new[] { 3, 6, 3, 9 }, new[] { 1, 2, 1, 3 })]
    [InlineData(new[] { 0, 1, 1, 3 }, new[] { 1, 2, 2, 6 })]
    [InlineData(new[] { 1, 2, 4, 1 }, new[] { 1, 2, 4, 1 })]
    public void Normalized_WithStructure_DividesFactorOut(int[] mults, int[] expected)
    {
        // Arrange
        FmStructure structure = new(4, 0, mults[0], mults[1], mults[2], mults[3], FmStructure.DefaultModulatorTl);

        // Act
        FmStructure? normalized = FmPitch.Normalized(structure);

        // Assert
        Assert.Equal(expected, normalized is { } s ? [s.Mult1, s.Mult2, s.Mult3, s.Mult4] : null);
    }

    /// <summary>A ½ beside a multiple above 7 can't be doubled onto the grid, so there is no normalized patch.</summary>
    [Fact]
    public void Normalized_PastFifteen_GivesUp()
    {
        // Act
        FmPatch? normalized = FmPitch.Normalized(Lead(0, 8, 1, 1));

        // Assert
        Assert.Null(normalized);
    }

    /// <summary>The same goes for a structure.</summary>
    [Fact]
    public void Normalized_WithStructurePastFifteen_GivesUp()
    {
        // Act
        FmStructure? normalized = FmPitch.Normalized(new FmStructure(4, 0, 0, 8, 1, 1, FmStructure.DefaultModulatorTl));

        // Assert
        Assert.Null(normalized);
    }

    /// <summary>
    /// The level fit never moves into a patch off the note's pitch, even toward a target that sounds an octave up: one step
    /// of OP1 from 1 to 2 would match it, and play every note an octave high.
    /// </summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Fit_WithLevels_KeepsNotePitch()
    {
        // Arrange
        FmEvaluator evaluator = new(FmTargets.Of(Lead(2, 2, 2, 2)));

        // Act
        FmVoiceSetting fitted = FmLocalSearch.Refine(evaluator, new FmVoiceSetting(Lead(1, 2, 2, 2), null), false, false, 150);

        // Assert
        Assert.True(FmPitch.Keeps(fitted.Patch), string.Join(' ', fitted.Patch.Operators.Select(o => o.Mult)));
    }
}
