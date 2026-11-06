using Mod2Xgm.Audio;
using Mod2Xgm.Model;
using Serilog.Core;

namespace Mod2Xgm.Tests.Audio;

/// <summary>Tests for <see cref="SampleProcessor"/> (plan T5.1): resampling, loops, trimming, gain and PCM conversion.</summary>
public sealed class SampleProcessorTests
{
    /// <summary>A MOD note's playback rate: PAL Paula clock / (2 × period 428), C-2 on the Amiga.</summary>
    private const double ModRateHz = 8287.14;

    private readonly ConversionNotes _notes = new(Logger.None);

    /// <summary>Raising the rate keeps the tone's pitch, level and timing: the copy is the same sine at the new rate.</summary>
    [Fact]
    public void Resample_WithSmoothUpsampling_KeepsTone()
    {
        // Arrange
        float[] source = Sine(440, ModRateHz, 8287);

        // Act
        float[] result = SampleProcessor.Resample(source, ModRateHz, 32000, ResamplingMode.Smooth);

        // Assert
        Assert.InRange(result.Length, 31998, 31999);
        Assert.InRange(MaxErrorFromSine(result, 440, 32000, 1000, result.Length - 1000), 0, 0.002);
    }

    /// <summary>Lowering the rate keeps the tone with no delay (NWaves' own filter would shift it by 50 points).</summary>
    [Fact]
    public void Resample_WithSmoothDownsampling_KeepsToneWithoutDelay()
    {
        // Arrange
        float[] source = Sine(440, 32000, 32000);

        // Act
        float[] result = SampleProcessor.Resample(source, 32000, 8000, ResamplingMode.Smooth);

        // Assert
        Assert.Equal(8000, result.Length);
        Assert.InRange(MaxErrorFromSine(result, 440, 8000, 200, result.Length - 200), 0, 0.002);
    }

    /// <summary>A tone above the new rate's limit is removed instead of folding back as a wrong pitch.</summary>
    [Fact]
    public void Resample_WithToneAboveNewLimit_RemovesIt()
    {
        // Arrange
        float[] source = Sine(6000, 32000, 32000);

        // Act
        float[] result = SampleProcessor.Resample(source, 32000, 8000, ResamplingMode.Smooth);

        // Assert
        Assert.InRange(Math.Sqrt(result[200..^200].Average(v => (double)v * v)), 0, 0.001);
    }

    /// <summary>
    /// Amiga mode keeps Paula's steps: next to the tone there is its image at rate − pitch, about −25 dB for a 440 Hz tone
    /// at 8287 Hz.
    /// </summary>
    [Fact]
    public void Resample_WithAmigaMode_KeepsPaulasOvertones()
    {
        // Arrange
        float[] source = Sine(440, ModRateHz, 8287);

        // Act
        float[] result = SampleProcessor.Resample(source, ModRateHz, 32000, ResamplingMode.Amiga);

        // Assert
        Assert.InRange(result.Length, 31998, 32000);
        Assert.InRange(Amplitude(result, 440, 32000, 4000, 16000), 0.98, 1.0);
        Assert.InRange(Amplitude(result, ModRateHz - 440, 32000, 4000, 16000), 0.04, 0.07);
    }

    /// <summary>Smooth mode has no image of the tone at rate − pitch.</summary>
    [Fact]
    public void Resample_WithSmoothMode_HasNoOvertones()
    {
        // Arrange
        float[] source = Sine(440, ModRateHz, 8287);

        // Act
        float[] result = SampleProcessor.Resample(source, ModRateHz, 32000, ResamplingMode.Smooth);

        // Assert
        Assert.InRange(Amplitude(result, ModRateHz - 440, 32000, 4000, 16000), 0, 0.001);
    }

    /// <summary>
    /// Amiga mode's steps are still band-limited: at 8287 Hz, Paula's images of a 440 Hz tone sit at 7847 and 8727 Hz; at
    /// an 8000 Hz target both are past its 4000 Hz limit and would fold back to 153 and 727 Hz if they weren't removed.
    /// </summary>
    [Fact]
    public void Resample_WithAmigaModeBelowImages_DoesNotAlias()
    {
        // Arrange
        float[] source = Sine(440, ModRateHz, 8287);

        // Act
        float[] result = SampleProcessor.Resample(source, ModRateHz, 8000, ResamplingMode.Amiga);

        // Assert
        Assert.InRange(Amplitude(result, 153, 8000, 1000, 6000), 0, 0.002);
        Assert.InRange(Amplitude(result, 727, 8000, 1000, 6000), 0, 0.002);
        Assert.InRange(Amplitude(result, 440, 8000, 1000, 6000), 0.98, 1.0);
    }

    /// <summary>The length follows the rates exactly: doubled at double the rate, halved at half.</summary>
    [Theory]
    [InlineData(nameof(ResamplingMode.Smooth), 16000, 2000)]
    [InlineData(nameof(ResamplingMode.Smooth), 4000, 500)]
    [InlineData(nameof(ResamplingMode.Amiga), 16000, 2000)]
    [InlineData(nameof(ResamplingMode.Amiga), 4000, 500)]
    public void Resample_WithOtherRate_ScalesLength(string mode, double toHz, int expected)
    {
        // Arrange
        ResamplingMode resampling = Enum.Parse<ResamplingMode>(mode);
        float[] source = Sine(100, 8000, 1000);

        // Act
        float[] result = SampleProcessor.Resample(source, 8000, toHz, resampling);

        // Assert
        Assert.Equal(expected, result.Length);
    }

    /// <summary>Smooth resampling at the same rate is a copy, not the same array.</summary>
    [Fact]
    public void Resample_WithSameRate_ReturnsCopy()
    {
        // Arrange
        float[] source = Sine(100, 8000, 100);

        // Act
        float[] result = SampleProcessor.Resample(source, 8000, 8000, ResamplingMode.Smooth);

        // Assert
        Assert.Equal(source, result);
        Assert.NotSame(source, result);
    }

    /// <summary>An empty sample stays empty.</summary>
    [Fact]
    public void Resample_WithEmptySample_ReturnsEmpty()
    {
        // Act
        float[] result = SampleProcessor.Resample([], 8000, 16000, ResamplingMode.Amiga);

        // Assert
        Assert.Empty(result);
    }

    /// <summary>Rates must be positive numbers that NWaves can take.</summary>
    [Theory]
    [InlineData(0, 8000)]
    [InlineData(-1, 8000)]
    [InlineData(8000, double.NaN)]
    [InlineData(8000, double.PositiveInfinity)]
    [InlineData(8000, 0.001)]
    [InlineData(8000, 3e7)]
    public void Resample_WithBadRate_Throws(double fromHz, double toHz)
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleProcessor.Resample([0.5f], fromHz, toHz, ResamplingMode.Smooth));
    }

    /// <summary>A loop plays from the start to the loop's end, then the loop repeats; data past the loop never plays.</summary>
    [Theory]
    [InlineData(7, new[] { 0.1f, 0.2f, 0.3f, 0.2f, 0.3f, 0.2f, 0.3f })]
    [InlineData(2, new[] { 0.1f, 0.2f })]
    [InlineData(0, new float[0])]
    public void Unroll_WithLoopedSample_RepeatsLoop(int length, float[] expected)
    {
        // Arrange
        Sample sample = new("loop", [0.1f, 0.2f, 0.3f, 0.4f, 0.5f], new SampleLoop(1, 2));

        // Act
        float[] result = SampleProcessor.Unroll(sample, length);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>A sample without a loop stops at its end.</summary>
    [Theory]
    [InlineData(10, new[] { 0.1f, 0.2f, 0.3f })]
    [InlineData(1, new[] { 0.1f })]
    public void Unroll_WithOneShotSample_StopsAtEnd(int length, float[] expected)
    {
        // Arrange
        Sample sample = new("hit", [0.1f, 0.2f, 0.3f], null);

        // Act
        float[] result = SampleProcessor.Unroll(sample, length);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>A negative length is a caller bug and throws.</summary>
    [Fact]
    public void Unroll_WithNegativeLength_Throws()
    {
        // Arrange
        Sample sample = new("hit", [0.1f, 0.2f, 0.3f], null);

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleProcessor.Unroll(sample, -1));
    }

    /// <summary>Data past a loop's end is cut; the name and loop stay.</summary>
    [Fact]
    public void CutAfterLoop_WithDataPastLoop_DropsIt()
    {
        // Arrange
        Sample looped = new("loop", [0.1f, 0.2f, 0.3f, 0.4f, 0.5f], new SampleLoop(1, 2));
        float[] expected = [0.1f, 0.2f, 0.3f];

        // Act
        Sample cut = SampleProcessor.CutAfterLoop(looped);

        // Assert
        Assert.Equal(expected, cut.Data);
        Assert.Equal(looped.Loop, cut.Loop);
        Assert.Equal("loop", cut.Name);
    }

    /// <summary>A sample that ends with its loop is returned as it is.</summary>
    [Fact]
    public void CutAfterLoop_WithSampleEndingWithLoop_ReturnsItUnchanged()
    {
        // Arrange
        Sample sample = new("loop", [0.1f, 0.2f, 0.3f], new SampleLoop(1, 2));

        // Act
        Sample result = SampleProcessor.CutAfterLoop(sample);

        // Assert
        Assert.Same(sample, result);
    }

    /// <summary>A sample without a loop is returned as it is.</summary>
    [Fact]
    public void CutAfterLoop_WithOneShotSample_ReturnsItUnchanged()
    {
        // Arrange
        Sample sample = new("hit", [0.1f, 0.2f], null);

        // Act
        Sample result = SampleProcessor.CutAfterLoop(sample);

        // Assert
        Assert.Same(sample, result);
    }

    /// <summary>Only the points that would round to 0 at the target depth are trimmed: 0.003 rounds to 0 in 8-bit (under half of 1/128) but not in 16-bit.</summary>
    [Theory]
    [InlineData(nameof(PcmDepth.Bits8), new[] { 0.5f })]
    [InlineData(nameof(PcmDepth.Bits16), new[] { 0.5f, 0.003f, -0.001f })]
    public void TrimTrailingSilence_WithQuietTail_TrimsWhatRoundsToZero(string depth, float[] expected)
    {
        // Arrange
        Sample sample = new("hit", [0.5f, 0.003f, -0.001f, 0f], null);

        // Act
        Sample trimmed = SampleProcessor.TrimTrailingSilence(sample, Enum.Parse<PcmDepth>(depth));

        // Assert
        Assert.Equal(expected, trimmed.Data);
    }

    /// <summary>A sample of nothing but silence is trimmed to nothing.</summary>
    [Fact]
    public void TrimTrailingSilence_WithOnlySilence_ReturnsEmptySample()
    {
        // Arrange
        Sample sample = new("quiet", [0f, 0f], null);

        // Act
        Sample trimmed = SampleProcessor.TrimTrailingSilence(sample, PcmDepth.Bits16);

        // Assert
        Assert.Empty(trimmed.Data);
    }

    /// <summary>A sample with no silent end is returned as it is.</summary>
    [Fact]
    public void TrimTrailingSilence_WithoutSilentEnd_ReturnsItUnchanged()
    {
        // Arrange
        Sample sample = new("loud", [0.5f], null);

        // Act
        Sample trimmed = SampleProcessor.TrimTrailingSilence(sample, PcmDepth.Bits8);

        // Assert
        Assert.Same(sample, trimmed);
    }

    /// <summary>A looping sample keeps its silent end: the loop plays it again and again.</summary>
    [Fact]
    public void TrimTrailingSilence_WithLoopedSample_ReturnsItUnchanged()
    {
        // Arrange
        Sample sample = new("loop", [0.5f, 0f, 0f], new SampleLoop(0, 3));

        // Act
        Sample trimmed = SampleProcessor.TrimTrailingSilence(sample, PcmDepth.Bits8);

        // Assert
        Assert.Same(sample, trimmed);
    }

    /// <summary>Gain multiplies every point and may go past ±1.</summary>
    [Fact]
    public void ApplyGain_WithGain_ScalesEveryPoint()
    {
        // Arrange
        float[] expected = [0.25f, -0.5f, 1.5f];

        // Act
        float[] result = SampleProcessor.ApplyGain([0.125f, -0.25f, 0.75f], 2);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>A negative or undefined gain is a caller bug and throws.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void ApplyGain_WithBadGain_Throws(double gain)
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleProcessor.ApplyGain([0f], gain));
    }

    /// <summary>Every MOD 8-bit value survives the trip to −1…+1 and back to 8-bit exactly, without notes.</summary>
    [Fact]
    public void ToSigned8_WithEveryModValue_ConvertsExactly()
    {
        // Arrange
        float[] data = [.. Enumerable.Range(-128, 256).Select(v => v / 128f)];

        // Act
        sbyte[] result = SampleProcessor.ToSigned8(data, "all", _notes);

        // Assert
        Assert.Equal(Enumerable.Range(-128, 256), result.Select(v => (int)v));
        Assert.Equal(0, _notes.Count);
    }

    /// <summary>Every MOD 8-bit value survives the trip to −1…+1 and on to 16-bit exactly, without notes.</summary>
    [Fact]
    public void ToSigned16_WithEveryModValue_ConvertsExactly()
    {
        // Arrange
        float[] data = [.. Enumerable.Range(-128, 256).Select(v => v / 128f)];

        // Act
        short[] result = SampleProcessor.ToSigned16(data, "all", _notes);

        // Assert
        Assert.Equal(Enumerable.Range(-128, 256).Select(v => v * 256), result.Select(v => (int)v));
        Assert.Equal(0, _notes.Count);
    }

    /// <summary>Points round to the nearest step, halves away from zero (no dither).</summary>
    [Fact]
    public void ToSigned8_WithPointsBetweenSteps_RoundsToNearest()
    {
        // Arrange
        float[] data = [0.5f / 128, -0.5f / 128, 0.4f / 128, 1.6f / 128];
        int[] expected = [1, -1, 0, 2];

        // Act
        sbyte[] result = SampleProcessor.ToSigned8(data, "round", _notes);

        // Assert
        Assert.Equal(expected, result.Select(v => (int)v));
        Assert.Equal(0, _notes.Count);
    }

    /// <summary>Points past the range are clipped, and one conversion note per sample says how many and how loud.</summary>
    [Fact]
    public void ToSigned8_WithPointsPastRange_ClipsAndNotes()
    {
        // Arrange
        float[] data = [1.2f, -1.5f, 0f, 1f];
        int[] expected = [127, -128, 0, 127];
        string[] expectedNotes = ["Sample loud: 3 of 4 frames clipped converting to 8-bit PCM (peak 1.5)"];

        // Act
        sbyte[] result = SampleProcessor.ToSigned8(data, "loud", _notes);

        // Assert
        Assert.Equal(expected, result.Select(v => (int)v));
        Assert.Equal(expectedNotes, _notes.Messages);
    }

    /// <summary>16-bit conversion clips and notes the same way.</summary>
    [Fact]
    public void ToSigned16_WithPointsPastRange_ClipsAndNotes()
    {
        // Arrange
        float[] data = [1.2f, -1.5f, 0f, 1f];
        int[] expected = [32767, -32768, 0, 32767];
        string[] expectedNotes = ["Sample loud: 3 of 4 frames clipped converting to 16-bit PCM (peak 1.5)"];

        // Act
        short[] result = SampleProcessor.ToSigned16(data, "loud", _notes);

        // Assert
        Assert.Equal(expected, result.Select(v => (int)v));
        Assert.Equal(expectedNotes, _notes.Messages);
    }

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
}
