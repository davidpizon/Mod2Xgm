using Mod2Xgm.Mapping;

namespace Mod2Xgm.Tests.Mapping;

/// <summary>Tests for the voice measures the DMF translator and the Genesis renderer share (docs/adr/0059-genesis-renderer.md).</summary>
public sealed class PlanVoicingTests
{
    /// <summary>A pan plays on one side only beyond a third of the way out; nearer the centre on both.</summary>
    [Theory]
    [InlineData(-1, -1)]
    [InlineData(-0.34, -1)]
    [InlineData(-0.33, 0)]
    [InlineData(0, 0)]
    [InlineData(0.33, 0)]
    [InlineData(0.34, 1)]
    [InlineData(1, 1)]
    public void Side_WithPan_PicksSide(double pan, int expected)
    {
        // Act
        int side = PlanVoicing.Side(pan);

        // Assert
        Assert.Equal(expected, side);
    }

    /// <summary>Semitones from C-0 turn into hertz around A-4 at 440 Hz.</summary>
    [Theory]
    [InlineData(PlanVoicing.A4, 440.0)]
    [InlineData(PlanVoicing.A4 + 12, 880.0)]
    [InlineData(48, 261.6256)]
    public void Hz_WithSemitone_ReturnsFrequency(int semitone, double expected)
    {
        // Act
        double hz = PlanVoicing.Hz(semitone);

        // Assert
        Assert.Equal(expected, hz, 3);
    }
}
