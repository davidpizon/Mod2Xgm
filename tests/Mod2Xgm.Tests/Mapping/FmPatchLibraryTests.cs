using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;
using Mod2Xgm.Optimizer;

namespace Mod2Xgm.Tests.Mapping;

/// <summary>
/// Tests for <see cref="FmPatchLibrary"/>: its notes start with a ramp of about 1 ms, not the one-sample step of attack
/// rate 31 that clicks (docs/adr/0121-soft-fm-attack.md).
/// </summary>
public sealed class FmPatchLibraryTests
{
    /// <summary>
    /// A sine note on the chip plays its first half millisecond at under half the level the same patch at rate 31 does,
    /// and is at full level by 3 ms.
    /// </summary>
    [Fact]
    public void Sine_OnChip_RisesOverAboutOneMillisecond()
    {
        // Arrange
        FmPatch sut = FmPatchLibrary.Sine;
        FmPatch instant = sut with { Operators = [.. sut.Operators.Select(o => o with { Ar = 31 })] };

        // Act
        float[] note = FmNoteRenderer.Render(sut, null, 440, 0.05);
        float[] step = FmNoteRenderer.Render(instant, null, 440, 0.05);

        // Assert
        double steady = Peak(note, 0.020, 0.040);
        Assert.InRange(Peak(note, 0, 0.0005), 0, Peak(step, 0, 0.0005) * 0.5);
        Assert.InRange(Peak(note, 0.003, 0.006), steady * 0.95, steady * 1.05);
    }

    /// <summary>Every operator of the library that starts at once uses the soft attack rate, never rate 31.</summary>
    [Fact]
    public void All_Operators_NeverUseInstantAttack()
    {
        // Arrange
        IReadOnlyList<FmPatch> sut = FmPatchLibrary.All;

        // Act
        int[] rates = [.. sut.SelectMany(p => p.Operators).Select(o => o.Ar)];

        // Assert
        Assert.DoesNotContain(31, rates);
        Assert.Contains(FmPatchLibrary.SoftAttackRate, rates);
    }

    /// <summary>The loudest sample of <paramref name="note"/> between <paramref name="from"/> and <paramref name="to"/> seconds.</summary>
    private static double Peak(float[] note, double from, double to) =>
        note.Skip((int)(from * SampleAnalyzer.AnalysisRateHz)).Take(Math.Max(1, (int)((to - from) * SampleAnalyzer.AnalysisRateHz))).Max(Math.Abs);
}
