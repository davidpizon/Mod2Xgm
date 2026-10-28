using Mod2Xgm.Model;
using NWaves.Filters.Base;
using NWaves.Filters.Fda;
using NWaves.Operations;
using NWaves.Signals;

namespace Mod2Xgm.Audio;

/// <summary>
/// Turns song samples into what a target can store (plan T5.1, docs/architecture.md#layers): a new rate, a loop
/// played out to a length, the parts that never sound cut off, a volume applied, and 16-bit or 8-bit points. It is a
/// toolbox: the mapper decides which rates, lengths and volumes a sample needs. Work in this order: unroll the loop
/// for the note's length (at the source rate, so resampling sees the loop's real continuation), apply the volume,
/// resample, trim, then convert to PCM. Smooth resampling uses NWaves (MIT, docs/adr/0038-sample-processor.md).
/// </summary>
internal static class SampleProcessor
{
    /// <summary>
    /// NWaves takes whole-number rates, so rates are passed in hundredths of a hertz: a MOD rate like 8287.14 Hz keeps
    /// its pitch (0.01 Hz is 0.002 cents at 8 kHz).
    /// </summary>
    private const double RateScale = 100;

    /// <summary>Half-width of the windowed-sinc interpolator, in source points (NWaves' default is 15).</summary>
    private const int SincHalfWidth = 32;

    /// <summary>Taps of the anti-aliasing low-pass used when the rate goes down; odd, so its delay is whole points.</summary>
    private const int LowPassTaps = 255;

    /// <summary>
    /// Low-pass cutoff as a fraction of the new rate's upper limit (half the rate), set a little below it so the
    /// filter's slope has room before sound would fold back.
    /// </summary>
    private const double LowPassCutoff = 0.95;

    /// <summary>Amiga mode draws Paula's steps at this many times the target rate, then band-limits them down to it.</summary>
    private const int AmigaOversampling = 8;

    /// <summary>
    /// Plays <paramref name="data"/>, recorded at <paramref name="fromHz"/>, at <paramref name="toHz"/>: the result
    /// sounds at the same pitch and for the same time. Nothing above half of <paramref name="toHz"/> is kept, so the
    /// copy doesn't alias. The result has ⌊length × toHz / fromHz⌋ points.
    /// </summary>
    /// <param name="data">Sample points, −1…+1.</param>
    /// <param name="fromHz">Rate the points are played at now (MOD: the note's playback rate).</param>
    /// <param name="toHz">Rate the target plays them at.</param>
    /// <param name="mode">Smooth (default) or Amiga steps (<c>--resampler</c>).</param>
    /// <exception cref="ArgumentOutOfRangeException">A rate is not a positive number or is too high.</exception>
    public static float[] Resample(float[] data, double fromHz, double toHz, ResamplingMode mode)
    {
        int from = ScaledRate(fromHz, nameof(fromHz));
        int to = ScaledRate(toHz, nameof(toHz));
        if (data.Length == 0)
        {
            return [];
        }

        return mode == ResamplingMode.Amiga ? Steps(data, fromHz, toHz) : Smooth(data, from, to);
    }

    /// <summary>
    /// Plays <paramref name="sample"/> for <paramref name="frames"/> points the way ProTracker 2 does: from its start
    /// to its loop's end, then the loop over and over. Data past the loop's end never plays. A sample without a loop
    /// stops at its end, so the result can be shorter than <paramref name="frames"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frames"/> is negative.</exception>
    public static float[] Unroll(Sample sample, int frames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        if (sample.Loop is not { } loop)
        {
            return sample.Data.AsSpan(0, Math.Min(frames, sample.Length)).ToArray();
        }

        int loopEnd = loop.Start + loop.Length;
        float[] result = new float[frames];
        int straight = Math.Min(frames, loopEnd);
        sample.Data.AsSpan(0, straight).CopyTo(result);
        for (int i = straight; i < frames; i++)
        {
            result[i] = sample.Data[loop.Start + ((i - loopEnd) % loop.Length)];
        }

        return result;
    }

    /// <summary>
    /// Drops a looping sample's data past its loop end, which ProTracker 2 never plays (consolidation step 3,
    /// docs/sample-capacity.md). A sample without a loop, or with nothing past it, is returned as is.
    /// </summary>
    public static Sample CutAfterLoop(Sample sample)
    {
        if (sample.Loop is not { } loop || loop.Start + loop.Length == sample.Length)
        {
            return sample;
        }

        return new Sample(sample.Name, sample.Data[..(loop.Start + loop.Length)], loop);
    }

    /// <summary>
    /// Drops the silent end of a sample without a loop: the points that would round to 0 at <paramref name="depth"/>,
    /// so the stored sound is unchanged. A looping sample is returned as is, because its loop keeps playing.
    /// </summary>
    public static Sample TrimTrailingSilence(Sample sample, PcmDepth depth)
    {
        if (sample.Loop is not null)
        {
            return sample;
        }

        float silent = 0.5f / FullScale(depth);
        int length = sample.Length;
        while (length > 0 && Math.Abs(sample.Data[length - 1]) < silent)
        {
            length--;
        }

        return length == sample.Length ? sample : new Sample(sample.Name, sample.Data[..length], null);
    }

    /// <summary>
    /// Multiplies every point by <paramref name="gain"/> (a MOD volume the target can't apply: volume / 64). Points can
    /// end up past ±1; PCM conversion clips them and says so.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gain"/> is negative or not a number.</exception>
    public static float[] ApplyGain(float[] data, double gain)
    {
        if (!double.IsFinite(gain) || gain < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gain), gain, "Gain must be a finite number of 0 or more.");
        }

        float[] result = new float[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            result[i] = (float)(data[i] * gain);
        }

        return result;
    }

    /// <summary>
    /// Converts to 8-bit signed PCM (XGM, XGM2): × 128, rounded to the nearest step (halves away from zero; no
    /// dither, David). MOD's own 8-bit points come back exactly. Points past the range are clipped, with a
    /// conversion note naming <paramref name="sampleName"/>.
    /// </summary>
    public static sbyte[] ToSigned8(float[] data, string sampleName, ConversionNotes notes)
    {
        sbyte[] result = new sbyte[data.Length];
        Clipping clipping = default;
        for (int i = 0; i < data.Length; i++)
        {
            result[i] = (sbyte)Quantize(data[i], PcmDepth.Bits8, ref clipping);
        }

        clipping.Report(sampleName, data.Length, 8, notes);
        return result;
    }

    /// <summary>
    /// Converts to 16-bit signed PCM (DMF): × 32768, rounded to the nearest step (halves away from zero). MOD's 8-bit
    /// points become exact multiples of 256. Points past the range are clipped, with a conversion note naming
    /// <paramref name="sampleName"/>.
    /// </summary>
    public static short[] ToSigned16(float[] data, string sampleName, ConversionNotes notes)
    {
        short[] result = new short[data.Length];
        Clipping clipping = default;
        for (int i = 0; i < data.Length; i++)
        {
            result[i] = (short)Quantize(data[i], PcmDepth.Bits16, ref clipping);
        }

        clipping.Report(sampleName, data.Length, 16, notes);
        return result;
    }

    /// <summary>Windowed-sinc resampling between two scaled rates, without delay.</summary>
    private static float[] Smooth(float[] data, int from, int to)
    {
        if (from == to)
        {
            return [.. data];
        }

        Resampler resampler = new();
        if (to > from)
        {
            return resampler.Resample(new DiscreteSignal(from, data), to, filter: null, order: SincHalfWidth).Samples;
        }

        // Going down, the sound is band-limited first. NWaves' own pre-filter runs as a stream, which delays the sound
        // by half the filter and drops its last points, so the filter runs here as a full convolution and the delay
        // is cut off. Passing the filter to Resample tells NWaves the sound is already band-limited.
        double ratio = (double)to / from;
        FirFilter lowPass = new(DesignFilter.FirWinLp(LowPassTaps, ratio / 2 * LowPassCutoff));
        float[] filtered = lowPass.ProcessAllSamples(data);
        float[] aligned = filtered.AsSpan(LowPassTaps / 2, data.Length).ToArray();
        return resampler.Resample(new DiscreteSignal(from, aligned), to, filter: lowPass, order: SincHalfWidth).Samples;
    }

    /// <summary>
    /// Amiga mode: holds each point until the next, as Paula does, drawn finely at a multiple of the target rate, then
    /// band-limits that down to the target with <see cref="Smooth"/>.
    /// </summary>
    private static float[] Steps(float[] data, double fromHz, double toHz)
    {
        double holdHz = toHz * AmigaOversampling;
        double length = Math.Ceiling(data.Length * holdHz / fromHz);
        if (length > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(toHz), toHz, "The resampled sample would be too long.");
        }

        float[] held = new float[(int)length];
        double step = fromHz / holdHz;
        for (int k = 0; k < held.Length; k++)
        {
            held[k] = data[Math.Min((int)(k * step), data.Length - 1)];
        }

        return Smooth(held, ScaledRate(holdHz, nameof(toHz)), ScaledRate(toHz, nameof(toHz)));
    }

    /// <summary>A rate in hundredths of a hertz, as NWaves takes it.</summary>
    private static int ScaledRate(double hz, string name)
    {
        if (!double.IsFinite(hz) || hz <= 0 || hz * RateScale > int.MaxValue || Math.Round(hz * RateScale) < 1)
        {
            throw new ArgumentOutOfRangeException(name, hz, "A rate must be a positive number of hertz, at most 21 MHz.");
        }

        return (int)Math.Round(hz * RateScale);
    }

    /// <summary>Steps from 0 to full scale: 128 for 8-bit, 32768 for 16-bit.</summary>
    private static int FullScale(PcmDepth depth) => depth == PcmDepth.Bits8 ? 128 : 32768;

    /// <summary>One point as a PCM value, clipped to the depth's range and counted when it was.</summary>
    private static int Quantize(float value, PcmDepth depth, ref Clipping clipping)
    {
        int scale = FullScale(depth);
        double scaled = Math.Round(value * (double)scale, MidpointRounding.AwayFromZero);
        if (scaled > scale - 1 || scaled < -scale)
        {
            clipping.Count(value);
            return scaled > 0 ? scale - 1 : -scale;
        }

        return (int)scaled;
    }

    /// <summary>How many points were clipped and the loudest one.</summary>
    private struct Clipping
    {
        private int _frames;
        private float _peak;

        /// <summary>Counts one clipped point.</summary>
        public void Count(float value)
        {
            _frames++;
            _peak = Math.Max(_peak, Math.Abs(value));
        }

        /// <summary>Adds a conversion note when any point was clipped.</summary>
        public readonly void Report(string sampleName, int total, int bits, ConversionNotes notes)
        {
            if (_frames > 0)
            {
                notes.Add(
                    "Sample {Sample}: {Frames} of {Total} frames clipped converting to {Bits}-bit PCM (peak {Peak})",
                    sampleName,
                    _frames,
                    total,
                    bits,
                    Math.Round(_peak, 3));
            }
        }
    }
}
