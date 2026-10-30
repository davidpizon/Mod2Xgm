using Mod2Xgm.Scoring;
using R128Net;

namespace Mod2Xgm.Harness.Tests;

/// <summary>
/// Tests for <see cref="Loudness"/> (EBU R128 through R128Net, ADR 0053), and a cross-check of the converter's own
/// K-weighting in <see cref="BalanceMetrics"/> against it.
/// </summary>
public sealed class LoudnessTests
{
    /// <summary>BS.1770's calibration: a full-scale 997 Hz sine in one channel of a stereo pair reads −3.01 LUFS.</summary>
    [Fact]
    public void AFullScaleSineInOneChannelReadsMinus3Lufs()
    {
        StereoAudio tone = TestFiles.Tone(5, 48_000, 997, 1f);
        StereoAudio left = new(tone.Left, new float[tone.Length], tone.SampleRate);

        Loudness loudness = Loudness.Of(left);

        Assert.Equal(-3.01, loudness.IntegratedLufs ?? double.NaN, 2);
        Assert.Equal(0, loudness.TruePeakDbtp ?? double.NaN, 1);
    }

    /// <summary>
    /// Silence after the music is gated out: as much silence again would lower an average power by 3 dB, but the
    /// integrated loudness drops only by the few blocks that straddle the end of the tone.
    /// </summary>
    [Fact]
    public void SilenceIsGatedOut()
    {
        StereoAudio tone = TestFiles.Tone(5, 44_100, 440, 0.25f);
        float[] padded = [.. tone.Left, .. new float[tone.Length]];

        Loudness alone = Loudness.Of(tone);
        Loudness withSilence = Loudness.Of(new StereoAudio(padded, padded, tone.SampleRate));

        Assert.InRange((alone.IntegratedLufs - withSilence.IntegratedLufs) ?? double.NaN, 0, 0.2);
        Assert.Equal(20 * Math.Log10(0.25), withSilence.TruePeakDbtp ?? double.NaN, 1);
    }

    /// <summary>Silent audio has no loudness and no peak, rather than −∞.</summary>
    [Fact]
    public void SilenceHasNoLoudness()
    {
        float[] silence = new float[44_100];

        Loudness loudness = Loudness.Of(new StereoAudio(silence, silence, 44_100));

        Assert.Null(loudness.IntegratedLufs);
        Assert.Null(loudness.TruePeakDbtp);
        Assert.Null(Loudness.GatedLufs(new AudioSignal(silence, 44_100)));
        Assert.Null(new LoudnessComparison(loudness, Loudness.Of(TestFiles.Tone(1))).DifferenceLu);
    }

    /// <summary>A mono meter reads one channel: 3.01 LU below the same signal in both channels of a stereo meter.</summary>
    [Fact]
    public void MonoReadsOneChannel()
    {
        StereoAudio tone = TestFiles.Tone(2, 44_100, 1000, 0.5f);

        double stereo = Loudness.Of(tone).IntegratedLufs ?? double.NaN;
        double mono = Loudness.GatedLufs(tone.ToMono()) ?? double.NaN;

        Assert.Equal(stereo - 3.01, mono, 2);
    }

    /// <summary>
    /// The converter's hand-written K-weighting (<see cref="BalanceMetrics.Levels"/>, ADR 0051) agrees with libebur128's
    /// within 0.3 dB: a 400 ms window's level, less BS.1770's 0.691 dB, is the momentary loudness, with tones where each
    /// of the two filters matters (the 38 Hz high pass, the 1.7 kHz shelf). It reads a little low, by design differences
    /// ADR 0053 measured: BS.1770's high pass has a passband gain of +0.04 dB that BalanceMetrics normalizes away, and the
    /// audio-EQ-cookbook shelf (pyloudnorm's) is up to 0.22 dB below libebur128's around 1 kHz.
    /// </summary>
    [Theory]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(22_050)]
    public void BalanceLevelsMatchMomentaryLoudness(int rate)
    {
        int window = (int)(0.4 * rate);
        float[] samples = new float[window];
        for (int i = 0; i < window; i++)
        {
            double t = (double)i / rate;
            samples[i] = (float)((0.3 * Math.Sin(2 * Math.PI * 30 * t)) + (0.2 * Math.Sin(2 * Math.PI * 1000 * t)) + (0.2 * Math.Sin(2 * Math.PI * 6000 * t)));
        }

        using LoudnessMeter meter = new(1, rate, LoudnessModes.Momentary);
        meter.AddFrames(samples.AsSpan());

        double level = BalanceMetrics.Levels(new AudioSignal(samples, rate), window)[0];

        Assert.InRange(level - 0.691 - meter.MomentaryLoudness, -0.3, 0);
    }
}
