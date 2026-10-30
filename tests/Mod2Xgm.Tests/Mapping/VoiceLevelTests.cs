using Mod2Xgm.Mapping;
using Mod2Xgm.Model;

namespace Mod2Xgm.Tests.Mapping;

/// <summary>Tests for <see cref="VoiceLevel"/>: sample levels and FM patch levels on the DAC's scale (ADR 0050).</summary>
public sealed class VoiceLevelTests
{
    /// <summary>A sine of <paramref name="amplitude"/> with <paramref name="cycle"/> frames a cycle.</summary>
    private static float[] Sine(int frames, double amplitude, int cycle = 32) =>
        [.. Enumerable.Range(0, frames).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * i / cycle)))];

    /// <summary>A looped sample's level is its loop's RMS, whatever comes before the loop.</summary>
    [Fact]
    public void ALoopedSampleIsMeasuredOnItsLoop()
    {
        float[] data = [.. Enumerable.Repeat(1f, 64), .. Sine(64, 0.5)];

        Assert.Equal(0.5 / Math.Sqrt(2), VoiceLevel.Sample(new Sample("held", data, new SampleLoop(64, 64))), 3);
    }

    /// <summary>A one-shot's level is its loudest window: a hit that fades is as loud as its start.</summary>
    [Fact]
    public void AOneShotIsMeasuredWhereItIsLoudest()
    {
        float[] data = [.. Sine(256, 0.8), .. Sine(2048, 0.1)];
        Sample hit = new("hit", data, null);

        Assert.Equal(0.8 / Math.Sqrt(2), VoiceLevel.Sample(hit), 2);
        Assert.Equal(VoiceLevel.Sample(hit), VoiceLevel.Loudest(hit));
    }

    /// <summary>A short sample is measured whole; an empty one has no level.</summary>
    [Fact]
    public void ShortAndEmptySamples()
    {
        Assert.Equal(0.25, VoiceLevel.Loudest(new Sample("short", [0.25f, -0.25f, 0.25f, -0.25f], null)), 6);
        Assert.Equal(0, VoiceLevel.Loudest(new Sample("empty", [], null)));
    }

    /// <summary>A loop's level ignores a louder attack, which <see cref="VoiceLevel.Loudest"/> finds.</summary>
    [Fact]
    public void LoudestIgnoresTheLoop()
    {
        float[] data = [.. Sine(512, 1.0), .. Sine(512, 0.25)];
        Sample sample = new("attack and loop", data, new SampleLoop(512, 512));

        Assert.Equal(1 / Math.Sqrt(2), VoiceLevel.Loudest(sample), 2);
        Assert.Equal(0.25 / Math.Sqrt(2), VoiceLevel.Sample(sample), 2);
    }

    /// <summary>The Sine patch is one carrier at TL 0: the level of a full-scale sine, peaking at full scale.</summary>
    [Fact]
    public void TheSinePatchIsAFullScaleSine()
    {
        (double rms, double peak) = VoiceLevel.Fm(FmPatchLibrary.Sine, sustained: true);

        Assert.Equal(1 / Math.Sqrt(2), rms, 9);
        Assert.Equal(1, peak, 9);
    }

    /// <summary>Carriers' powers add: the Organ's four carriers at TL 22, 34, 44 and 22.</summary>
    [Fact]
    public void CarrierPowersAdd()
    {
        double[] amplitudes = [.. new[] { 22, 34, 44, 22 }.Select(tl => Math.Pow(10, -0.75 * tl / 20))];

        (double rms, double peak) = VoiceLevel.Fm(FmPatchLibrary.Organ, sustained: false);

        Assert.Equal(Math.Sqrt(amplitudes.Sum(a => a * a / 2)), rms, 9);
        Assert.Equal(amplitudes.Sum(), peak, 9);
    }

    /// <summary>A held Brass note settles 12 dB (SL 4) below its attack; its carriers' peak is over full scale.</summary>
    [Fact]
    public void ASustainedNoteSettlesAtItsSustainLevel()
    {
        double attack = VoiceLevel.Fm(FmPatchLibrary.Brass, sustained: false).Rms;
        (double held, double peak) = VoiceLevel.Fm(FmPatchLibrary.Brass, sustained: true);

        Assert.Equal(-12, 20 * Math.Log10(held / attack), 6);
        Assert.True(peak > 1);
    }

    /// <summary>A carrier that decays to SL 15 counts as silent when held, as does one at TL 127.</summary>
    [Fact]
    public void SustainLevel15IsSilent()
    {
        FmOperator fading = new(1, 0, 0, 0, 31, false, 10, 0, 15, 8);
        FmOperator mute = new(1, 0, 127, 0, 31, false, 0, 0, 0, 8);
        FmPatch patch = new("fade", 7, 0, [fading, mute, mute, mute]);

        Assert.Equal(0, VoiceLevel.Fm(patch, sustained: true).Rms);
        Assert.Equal(1 / Math.Sqrt(2), VoiceLevel.Fm(patch, sustained: false).Rms, 3);
    }

    /// <summary>Which operators are carriers under each algorithm (Yamaha order OP1–OP4).</summary>
    [Theory]
    [InlineData(0, new[] { 3 })]
    [InlineData(3, new[] { 3 })]
    [InlineData(4, new[] { 1, 3 })]
    [InlineData(5, new[] { 1, 2, 3 })]
    [InlineData(6, new[] { 1, 2, 3 })]
    [InlineData(7, new[] { 0, 1, 2, 3 })]
    public void CarriersFollowTheAlgorithm(int algorithm, int[] carriers) => Assert.Equal(carriers, VoiceLevel.Carriers(algorithm));
}
