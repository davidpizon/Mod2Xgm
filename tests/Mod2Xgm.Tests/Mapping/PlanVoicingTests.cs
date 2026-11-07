using Mod2Xgm.Mapping;
using Mod2Xgm.Model;

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

    /// <summary>Plain plans keep the noise as loud as the sample; a plan tuned by ear plays only its highs, 12 dB down, so a kick's hiss nearly vanishes.</summary>
    [Fact]
    public void NoiseLevel_WithPlanTunedByEar_IsFarQuieter()
    {
        // Arrange
        Song song = SongOf(Sine(4000, 0.1, period: 64));

        // Act
        double plain = new PlanVoicing(PlanOf(false), song).NoiseLevel(0).Gain;
        double tuned = new PlanVoicing(PlanOf(true), song).NoiseLevel(0).Gain;

        // Assert
        Assert.Equal(0.1 / Math.Sqrt(2) / VoiceLevel.PsgNoise, plain, 3);
        Assert.True(tuned < plain / 4);
    }

    /// <summary>A PSG tone stands in for the whole sample, so it stays as loud as the sample even in a plan tuned by ear.</summary>
    [Fact]
    public void ToneLevel_WithPlanTunedByEar_FollowsWholeSample()
    {
        // Arrange
        Song song = SongOf(Sine(4000, 0.1, period: 64));

        // Act
        double gain = new PlanVoicing(PlanOf(true), song).ToneLevel(0).Gain;

        // Assert
        Assert.Equal(0.1 / Math.Sqrt(2) / VoiceLevel.PsgNoise, gain, 3);
    }

    /// <summary>A bass note on a panned channel plays centred only in a plan tuned by ear, and only when it is panned and low.</summary>
    [Theory]
    [InlineData(true, -0.5, 110.0, true)]
    [InlineData(false, -0.5, 110.0, false)]
    [InlineData(true, 0, 110.0, false)]
    [InlineData(true, -0.5, 440.0, false)]
    public void CentredBass_WithNote_FollowsRule(bool tuned, double pan, double hz, bool expected)
    {
        // Arrange: a 64-frame cycle sounds at the playback rate over 64.
        Song song = SongOf(Sine(640, 0.5, period: 64));
        PlanVoicing sut = new(PlanOf(tuned), song);

        // Act
        bool centred = sut.CentredBass(new NotePitch(48, 0, hz * 64), 0, pan);

        // Assert
        Assert.Equal(expected, centred);
    }

    /// <summary>A patch measured from a render uses that level, not the one worked out from its registers.</summary>
    [Fact]
    public void FmLevel_WithHeardRms_UsesIt()
    {
        // Arrange
        Song song = SongOf(new Sample("s", Sine(640, 0.5, period: 64), new SampleLoop(0, 64)));
        FmPatch patch = FmPatchLibrary.Sine with { HeardRms = 1.0 };
        GenesisPlan plan = PlanOf(true) with { FmPatches = [patch] };
        PlanVoicing sut = new(plan, song);

        // Act
        double gain = sut.FmLevel(0, 0).Gain;

        // Assert
        Assert.Equal(0.5 / Math.Sqrt(2), gain, 3);
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

    private static float[] Sine(int frames, double level, int period) =>
        [.. Enumerable.Range(0, frames).Select(i => (float)(level * Math.Sin(2 * Math.PI * i / period)))];

    private static Song SongOf(float[] data) => SongOf(new Sample("s", data, null));

    private static Song SongOf(Sample sample) =>
        new("test", [new Channel(0)], [], null, [], [], [sample], new Timing(6, 125));

    private static GenesisPlan PlanOf(bool tuned) =>
        new("test", [], null, new Timing(6, 125), [], []) { TunedByEar = tuned };
}
