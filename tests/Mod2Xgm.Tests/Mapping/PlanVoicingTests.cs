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

    /// <summary>
    /// A sample whose loop is a short quiet tail after a long recording is played on FM as loud as its recording, as a
    /// sample that plays once is, not as its tail: the MOD's notes play the recording (FunkyChristmas's melody sample 23,
    /// 15 dB quieter in its tail, played 15 dB too quiet; docs/adr/0126-tail-loop-level.md).
    /// </summary>
    [Fact]
    public void FmLevel_WithQuietTailLoop_FollowsTheRecording()
    {
        // Arrange: 8192 frames of a loud tone, then a 1536-frame loop 20 dB quieter.
        float[] data = [.. Sine(8192, 0.5, period: 64), .. Sine(1536, 0.05, period: 64)];
        GenesisPlan plan = PlanOf(false) with { FmPatches = [FmPatchLibrary.Sine] };
        PlanVoicing sut = new(plan, SongOf(new Sample("tail", data, new SampleLoop(8192, 1536))));
        PlanVoicing oneShot = new(plan, SongOf(new Sample("once", data, null)));

        // Act
        double gain = sut.FmLevel(0, 0).Gain;

        // Assert
        Assert.Equal(oneShot.FmLevel(0, 0).Gain, gain, 6);
    }

    /// <summary>
    /// An FM note of a sample whose loop is a quiet tail after a long recording plays the hit at full level and fades with
    /// the recording into the tail's level, not holding the hit (FunkyChristmas's last melody note; docs/adr/0131-tail-loop-decay.md).
    /// </summary>
    [Fact]
    public void FmDecay_WithQuietTailLoop_FallsToTheTailsLevel()
    {
        // Arrange: 8192 frames of a loud tone, then a 1536-frame loop 20 dB quieter.
        GenesisPlan plan = PlanOf(false) with { FmPatches = [FmPatchLibrary.Sine] };
        PlanVoicing sut = new(plan, SongOf(TailSample()));

        // Act
        double hit = sut.FmDecay(0, 0, 0);
        double tail = sut.FmDecay(0, 0, 9000);
        double? settles = sut.FmTailDecay(0, 0);

        // Assert
        Assert.Equal(1, hit, 3);
        Assert.Equal(0.1, tail, 3);
        Assert.Equal(0.1, settles!.Value, 3);
    }

    /// <summary>A sample that plays once, or whose loop is its held tone, never fades on FM: its level is set as before.</summary>
    [Fact]
    public void FmDecay_WithToneLoopOrOneShot_StaysAtOne()
    {
        // Arrange
        GenesisPlan plan = PlanOf(false) with { FmPatches = [FmPatchLibrary.Sine] };
        float[] data = [.. Sine(8192, 0.5, period: 64), .. Sine(1536, 0.05, period: 64)];
        PlanVoicing sut = new(plan, SongOf(new Sample("tone", data, new SampleLoop(0, data.Length))));
        PlanVoicing oneShot = new(plan, SongOf(new Sample("once", data, null)));

        // Act
        double tone = sut.FmDecay(0, 0, 9000);
        double once = oneShot.FmDecay(0, 0, 9000);

        // Assert
        Assert.Equal(1, tone);
        Assert.Equal(1, once);
        Assert.Null(sut.FmTailDecay(0, 0));
        Assert.Null(oneShot.FmTailDecay(0, 0));
    }

    /// <summary>
    /// A patch that falls further on its own than the recording does (its carrier decays 24 dB to its sustain level) is
    /// left to fall by itself: the factor takes the patch's own fall out, so it stays at 1 rather than fading twice.
    /// </summary>
    [Fact]
    public void FmDecay_WithPatchFallingFurtherThanTheRecording_StaysAtOne()
    {
        // Arrange
        FmOperator muted = new(1, 0, 127, 0, 31, false, 0, 0, 0, 8);
        FmPatch falling = new("falling", 7, 0, [new FmOperator(1, 0, 0, 0, 31, false, 10, 0, 8, 8), muted, muted, muted]);
        GenesisPlan plan = PlanOf(false) with { FmPatches = [falling] };
        PlanVoicing sut = new(plan, SongOf(TailSample()));

        // Act
        double tail = sut.FmDecay(0, 0, 9000);

        // Assert
        Assert.Equal(1, tail);
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

    /// <summary>8192 frames of a tone at 0.5, then a 1536-frame loop of it 20 dB quieter: a loop that is a tail (ADR 0117).</summary>
    private static Sample TailSample() =>
        new("tail", [.. Sine(8192, 0.5, period: 64), .. Sine(1536, 0.05, period: 64)], new SampleLoop(8192, 1536));

    private static Song SongOf(float[] data) => SongOf(new Sample("s", data, null));

    private static Song SongOf(Sample sample) =>
        new("test", [new Channel(0)], [], null, [], [], [sample], new Timing(6, 125));

    private static GenesisPlan PlanOf(bool tuned) =>
        new("test", [], null, new Timing(6, 125), [], []) { TunedByEar = tuned };
}
