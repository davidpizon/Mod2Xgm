using Mod2Xgm.Audio;
using Mod2Xgm.Model;
using Serilog.Core;

namespace Mod2Xgm.Tests.Audio;

/// <summary>Tests for <see cref="SampleProcessor"/> (plan T5.1): resampling, loops, trimming, gain and PCM conversion.</summary>
public sealed class SampleProcessorTests
{
    /// <summary>A MOD note's playback rate: PAL Paula clock / (2 × period 428), C-2 on the Amiga.</summary>
    private const double ModRateHz = 8287.14;

    /// <summary>A sine of <paramref name="hz"/> at <paramref name="rate"/>, <paramref name="length"/> points long.</summary>
    private static float[] Sine(double hz, double rate, int length) =>
        [.. Enumerable.Range(0, length).Select(n => (float)Math.Sin(2 * Math.PI * hz * n / rate))];

    /// <summary>
    /// Amplitude of the <paramref name="hz"/> component in <paramref name="length"/> points from <paramref name="start"/>,
    /// measured through a Hann window so the edges of the stretch don't leak.
    /// </summary>
    private static double Amplitude(float[] signal, double hz, double rate, int start, int length)
    {
        double re = 0, im = 0, windowSum = 0;
        for (int i = 0; i < length; i++)
        {
            double w = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (length - 1)));
            double phase = 2 * Math.PI * hz * (start + i) / rate;
            re += w * signal[start + i] * Math.Cos(phase);
            im -= w * signal[start + i] * Math.Sin(phase);
            windowSum += w;
        }

        return 2 * Math.Sqrt((re * re) + (im * im)) / windowSum;
    }

    /// <summary>Largest difference from a sine of <paramref name="hz"/> at <paramref name="rate"/> between two points.</summary>
    private static double MaxErrorFromSine(float[] signal, double hz, double rate, int from, int to)
    {
        double max = 0;
        for (int n = from; n < to; n++)
        {
            max = Math.Max(max, Math.Abs(signal[n] - Math.Sin(2 * Math.PI * hz * n / rate)));
        }

        return max;
    }

    /// <summary>Raising the rate keeps the tone's pitch, level and timing: the copy is the same sine at the new rate.</summary>
    [Fact]
    public void SmoothUpsamplingKeepsTheTone()
    {
        float[] result = SampleProcessor.Resample(Sine(440, ModRateHz, 8287), ModRateHz, 32000, ResamplingMode.Smooth);

        Assert.InRange(result.Length, 31998, 31999);
        Assert.True(MaxErrorFromSine(result, 440, 32000, 1000, result.Length - 1000) < 0.002);
    }

    /// <summary>Lowering the rate keeps the tone with no delay (NWaves' own filter would shift it by 50 points).</summary>
    [Fact]
    public void SmoothDownsamplingKeepsTheToneWithoutDelay()
    {
        float[] result = SampleProcessor.Resample(Sine(440, 32000, 32000), 32000, 8000, ResamplingMode.Smooth);

        Assert.Equal(8000, result.Length);
        Assert.True(MaxErrorFromSine(result, 440, 8000, 200, result.Length - 200) < 0.002);
    }

    /// <summary>A tone above the new rate's limit is removed instead of folding back as a wrong pitch.</summary>
    [Fact]
    public void SmoothDownsamplingRemovesWhatWouldAlias()
    {
        float[] result = SampleProcessor.Resample(Sine(6000, 32000, 32000), 32000, 8000, ResamplingMode.Smooth);

        double rms = Math.Sqrt(result[200..^200].Average(v => (double)v * v));
        Assert.True(rms < 0.001, $"RMS {rms}");
    }

    /// <summary>
    /// Amiga mode keeps Paula's steps: next to the tone there is its image at rate − pitch (about −25 dB for a 440 Hz
    /// tone at 8287 Hz), which smooth mode doesn't have.
    /// </summary>
    [Fact]
    public void AmigaModeKeepsPaulasOvertones()
    {
        float[] source = Sine(440, ModRateHz, 8287);
        float[] amiga = SampleProcessor.Resample(source, ModRateHz, 32000, ResamplingMode.Amiga);
        float[] smooth = SampleProcessor.Resample(source, ModRateHz, 32000, ResamplingMode.Smooth);
        double image = ModRateHz - 440;

        Assert.InRange(amiga.Length, 31998, 32000);
        Assert.InRange(Amplitude(amiga, 440, 32000, 4000, 16000), 0.98, 1.0);
        Assert.InRange(Amplitude(amiga, image, 32000, 4000, 16000), 0.04, 0.07);
        Assert.True(Amplitude(smooth, image, 32000, 4000, 16000) < 0.001);
    }

    /// <summary>Amiga mode's steps are still band-limited: nothing folds back below the target's limit.</summary>
    [Fact]
    public void AmigaModeDoesNotAlias()
    {
        // At 8287 Hz, Paula's images of a 440 Hz tone sit at 7847 and 8727 Hz; at an 8000 Hz target both are past its
        // 4000 Hz limit and would fold back to 153 and 727 Hz if they weren't removed.
        float[] result = SampleProcessor.Resample(Sine(440, ModRateHz, 8287), ModRateHz, 8000, ResamplingMode.Amiga);

        Assert.True(Amplitude(result, 153, 8000, 1000, 6000) < 0.002);
        Assert.True(Amplitude(result, 727, 8000, 1000, 6000) < 0.002);
        Assert.InRange(Amplitude(result, 440, 8000, 1000, 6000), 0.98, 1.0);
    }

    /// <summary>Doubling the rate doubles the length exactly; the same rate returns an equal copy.</summary>
    [Theory]
    [InlineData(nameof(ResamplingMode.Smooth))]
    [InlineData(nameof(ResamplingMode.Amiga))]
    public void LengthFollowsTheRates(string mode)
    {
        ResamplingMode resampling = Enum.Parse<ResamplingMode>(mode);
        float[] source = Sine(100, 8000, 1000);

        Assert.Equal(2000, SampleProcessor.Resample(source, 8000, 16000, resampling).Length);
        Assert.Equal(500, SampleProcessor.Resample(source, 8000, 4000, resampling).Length);
    }

    /// <summary>Smooth resampling at the same rate is a copy, not the same array.</summary>
    [Fact]
    public void SameRateIsACopy()
    {
        float[] source = Sine(100, 8000, 100);
        float[] result = SampleProcessor.Resample(source, 8000, 8000, ResamplingMode.Smooth);

        Assert.Equal(source, result);
        Assert.NotSame(source, result);
    }

    /// <summary>An empty sample stays empty.</summary>
    [Fact]
    public void EmptyStaysEmpty() => Assert.Empty(SampleProcessor.Resample([], 8000, 16000, ResamplingMode.Amiga));

    /// <summary>Rates must be positive numbers that NWaves can take.</summary>
    [Theory]
    [InlineData(0, 8000)]
    [InlineData(-1, 8000)]
    [InlineData(8000, double.NaN)]
    [InlineData(8000, double.PositiveInfinity)]
    [InlineData(8000, 0.001)]
    [InlineData(8000, 3e7)]
    public void BadRatesAreRejected(double fromHz, double toHz) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleProcessor.Resample([0.5f], fromHz, toHz, ResamplingMode.Smooth));

    /// <summary>A loop plays from the start to the loop's end, then the loop repeats; data past the loop never plays.</summary>
    [Fact]
    public void UnrollRepeatsTheLoop()
    {
        Sample sample = new("loop", [0.1f, 0.2f, 0.3f, 0.4f, 0.5f], new SampleLoop(1, 2));

        float[] seven = [0.1f, 0.2f, 0.3f, 0.2f, 0.3f, 0.2f, 0.3f];
        float[] two = [0.1f, 0.2f];
        Assert.Equal(seven, SampleProcessor.Unroll(sample, 7));
        Assert.Equal(two, SampleProcessor.Unroll(sample, 2));
        Assert.Empty(SampleProcessor.Unroll(sample, 0));
    }

    /// <summary>A sample without a loop stops at its end.</summary>
    [Fact]
    public void UnrollStopsAOneShotSample()
    {
        Sample sample = new("hit", [0.1f, 0.2f, 0.3f], null);

        float[] all = [0.1f, 0.2f, 0.3f];
        float[] one = [0.1f];
        Assert.Equal(all, SampleProcessor.Unroll(sample, 10));
        Assert.Equal(one, SampleProcessor.Unroll(sample, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleProcessor.Unroll(sample, -1));
    }

    /// <summary>Data past a loop's end is cut; a sample that ends with its loop, or has none, is unchanged.</summary>
    [Fact]
    public void CutAfterLoopDropsWhatNeverPlays()
    {
        Sample looped = new("loop", [0.1f, 0.2f, 0.3f, 0.4f, 0.5f], new SampleLoop(1, 2));
        Sample cut = SampleProcessor.CutAfterLoop(looped);

        float[] kept = [0.1f, 0.2f, 0.3f];
        Assert.Equal(kept, cut.Data);
        Assert.Equal(looped.Loop, cut.Loop);
        Assert.Equal("loop", cut.Name);
        Assert.Same(cut, SampleProcessor.CutAfterLoop(cut));

        Sample oneShot = new("hit", [0.1f, 0.2f], null);
        Assert.Same(oneShot, SampleProcessor.CutAfterLoop(oneShot));
    }

    /// <summary>Only the points that would round to 0 at the target depth are trimmed, so the stored sound is the same.</summary>
    [Fact]
    public void TrailingSilenceIsTrimmedPerDepth()
    {
        // 0.003 rounds to 0 in 8-bit (under half of 1/128) but not in 16-bit.
        Sample sample = new("hit", [0.5f, 0.003f, -0.001f, 0f], null);

        float[] eightBit = [0.5f];
        float[] sixteenBit = [0.5f, 0.003f, -0.001f];
        Assert.Equal(eightBit, SampleProcessor.TrimTrailingSilence(sample, PcmDepth.Bits8).Data);
        Assert.Equal(sixteenBit, SampleProcessor.TrimTrailingSilence(sample, PcmDepth.Bits16).Data);
        Assert.Empty(SampleProcessor.TrimTrailingSilence(new Sample("quiet", [0f, 0f], null), PcmDepth.Bits16).Data);

        Sample loud = new("loud", [0.5f], null);
        Assert.Same(loud, SampleProcessor.TrimTrailingSilence(loud, PcmDepth.Bits8));
    }

    /// <summary>A looping sample keeps its silent end: the loop plays it again and again.</summary>
    [Fact]
    public void LoopingSamplesAreNotTrimmed()
    {
        Sample sample = new("loop", [0.5f, 0f, 0f], new SampleLoop(0, 3));
        Assert.Same(sample, SampleProcessor.TrimTrailingSilence(sample, PcmDepth.Bits8));
    }

    /// <summary>Gain multiplies every point and may go past ±1; bad gains are rejected.</summary>
    [Fact]
    public void GainScalesEveryPoint()
    {
        float[] doubled = [0.25f, -0.5f, 1.5f];
        Assert.Equal(doubled, SampleProcessor.ApplyGain([0.125f, -0.25f, 0.75f], 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleProcessor.ApplyGain([0f], -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleProcessor.ApplyGain([0f], double.NaN));
    }

    /// <summary>Every MOD 8-bit value survives the trip to −1…+1 and back to 8-bit and 16-bit exactly, without notes.</summary>
    [Fact]
    public void ModPointsConvertExactly()
    {
        float[] data = [.. Enumerable.Range(-128, 256).Select(v => v / 128f)];
        ConversionNotes notes = new(Logger.None);

        sbyte[] eight = SampleProcessor.ToSigned8(data, "all", notes);
        short[] sixteen = SampleProcessor.ToSigned16(data, "all", notes);

        Assert.Equal(Enumerable.Range(-128, 256), eight.Select(v => (int)v));
        Assert.Equal(Enumerable.Range(-128, 256).Select(v => v * 256), sixteen.Select(v => (int)v));
        Assert.Equal(0, notes.Count);
    }

    /// <summary>Points round to the nearest step, halves away from zero (no dither).</summary>
    [Fact]
    public void PointsRoundToTheNearestStep()
    {
        ConversionNotes notes = new(Logger.None);
        float[] data = [0.5f / 128, -0.5f / 128, 0.4f / 128, 1.6f / 128];

        int[] expected = [1, -1, 0, 2];
        Assert.Equal(expected, SampleProcessor.ToSigned8(data, "round", notes).Select(v => (int)v));
        Assert.Equal(0, notes.Count);
    }

    /// <summary>Points past the range are clipped, and one conversion note per sample says how many and how loud.</summary>
    [Fact]
    public void ClippingIsANote()
    {
        CollectingSink sink = new();
        ConversionNotes notes = new(sink.CreateLogger());
        float[] data = [1.2f, -1.5f, 0f, 1f];

        sbyte[] eight = SampleProcessor.ToSigned8(data, "loud", notes);
        short[] sixteen = SampleProcessor.ToSigned16(data, "loud", notes);

        int[] expectedEight = [127, -128, 0, 127];
        int[] expectedSixteen = [32767, -32768, 0, 32767];
        string[] expectedNotes =
        [
            "Sample loud: 3 of 4 frames clipped converting to 8-bit PCM (peak 1.5)",
            "Sample loud: 3 of 4 frames clipped converting to 16-bit PCM (peak 1.5)",
        ];
        Assert.Equal(expectedEight, eight.Select(v => (int)v));
        Assert.Equal(expectedSixteen, sixteen.Select(v => (int)v));
        Assert.Equal(expectedNotes, notes.Messages);
    }
}
