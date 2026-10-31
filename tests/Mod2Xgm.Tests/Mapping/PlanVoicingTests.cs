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
    public void PanPicksASide(double pan, int side) => Assert.Equal(side, PlanVoicing.Side(pan));

    /// <summary>Semitones from C-0 turn into hertz around A-4 at 440 Hz.</summary>
    [Fact]
    public void SemitonesBecomeHertz()
    {
        Assert.Equal(440, PlanVoicing.Hz(PlanVoicing.A4), 9);
        Assert.Equal(880, PlanVoicing.Hz(PlanVoicing.A4 + 12), 9);
        Assert.Equal(261.6256, PlanVoicing.Hz(48), 3);
    }
}
