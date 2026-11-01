using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;
using Mod2Xgm.Render;
using Mod2Xgm.Render.Fm;

namespace Mod2Xgm.Optimizer;

/// <summary>
/// Plays one held FM note on the YM3438 model, alone on channel 1 of an NTSC chip, and brings it to
/// <see cref="SampleAnalyzer.AnalysisRateHz"/>, so it can be measured as the sample's regions are
/// (docs/adr/0069-fm-patch-fitting.md). The key stays on: the plan holds an FM note until the next one, so a fitted
/// patch has to fade by its own decay, as the sample does.
/// </summary>
internal static class FmNoteRenderer
{
    /// <summary>One channel's output for a TL-0 sine carrier at its peak, so such a carrier renders at ±1, as a full-scale sample does (docs/adr/0050-voice-levels.md).</summary>
    private const double CarrierFullScale = Ym3438.FullScale / 6.0;

    // YM3438 registers (docs/genesis-audio.md).
    private const byte RegLfo = 0x22;
    private const byte RegCh3Mode = 0x27;
    private const byte RegKeyOn = 0x28;
    private const byte RegFnumLow = 0xa0;
    private const byte RegBlockFnumHigh = 0xa4;
    private const byte RegPanLfo = 0xb4;

    /// <summary>Register <c>$22</c>'s enable bit; the low 3 bits are the rate.</summary>
    private const byte LfoEnable = 0x08;

    /// <summary>Both outputs on, in register <c>$B4</c>.</summary>
    private const byte PanBoth = 0xc0;

    /// <summary>Register <c>$28</c>: all four operators of channel 1 on.</summary>
    private const byte KeyOnAll = 0xf0;

    /// <summary>Taps of the resampling filter: wide enough to fall from 19 kHz to silence before the analysis rate's 22 kHz.</summary>
    private const int Taps = 64;

    /// <summary>Fractional positions the filter is tabulated at.</summary>
    private const int Phases = 512;

    /// <summary>The resampling filter's cutoff, below the analysis rate's half (22 050 Hz), so the chip's 22–27 kHz doesn't fold back.</summary>
    private const double CutoffHz = 19_500;

    /// <summary>The chip's sample rate: an NTSC Genesis's.</summary>
    public static double ChipRateHz { get; } = Ym3438.SampleRateHz(Ym3438.NtscClockHz);

    /// <summary>The resampling filter, <see cref="Taps"/> per phase, each phase summing to 1.</summary>
    private static readonly float[] Kernel = BuildKernel();

    /// <summary>
    /// <paramref name="patch"/> held at <paramref name="hz"/> for <paramref name="seconds"/>, with the LFO at
    /// <paramref name="lfoRate"/> (0–7) or off (<c>null</c>), at <see cref="SampleAnalyzer.AnalysisRateHz"/>.
    /// </summary>
    public static float[] Render(FmPatch patch, int? lfoRate, double hz, double seconds)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
        Ym3438 chip = new();
        chip.WriteRegister(0, RegLfo, lfoRate is { } rate ? (byte)(LfoEnable | (rate & 7)) : (byte)0);
        chip.WriteRegister(0, RegCh3Mode, 0);
        FmVoice.LoadPatch(chip, 0, patch);
        FmVoice.Write(chip, 0, RegPanLfo, (byte)(PanBoth | (patch.Ams << 4) | patch.Fms));
        int blockFnum = FmVoice.BlockFnum(hz, ChipRateHz);
        FmVoice.Write(chip, 0, RegBlockFnumHigh, (byte)(blockFnum >> 8));
        FmVoice.Write(chip, 0, RegFnumLow, (byte)blockFnum);
        chip.WriteRegister(0, RegKeyOn, KeyOnAll);

        int frames = (int)Math.Ceiling(seconds * ChipRateHz);
        int[] left = new int[frames];
        int[] right = new int[frames];
        chip.Generate(left, right);
        float[] mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            mono[i] = (float)(left[i] / CarrierFullScale);
        }

        return ToAnalysisRate(mono);
    }

    /// <summary>
    /// <paramref name="input"/>, at <see cref="ChipRateHz"/>, at <see cref="SampleAnalyzer.AnalysisRateHz"/>: a
    /// Blackman-windowed sinc low-pass read at each output point. The converter's resampler (NWaves, ADR 0038) is built
    /// for output quality and takes most of a fit's time at this fixed ratio; this one is about 50 times faster and its
    /// error stays far below the distances it serves (docs/adr/0069-fm-patch-fitting.md).
    /// </summary>
    internal static float[] ToAnalysisRate(float[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        double step = ChipRateHz / SampleAnalyzer.AnalysisRateHz;
        int length = (int)Math.Floor(input.Length / step);
        float[] output = new float[length];
        for (int j = 0; j < length; j++)
        {
            double x = j * step;
            int i0 = (int)Math.Floor(x);
            int phase = (int)Math.Round((x - i0) * Phases);
            if (phase == Phases)
            {
                phase = 0;
                i0++;
            }

            ReadOnlySpan<float> kernel = Kernel.AsSpan(phase * Taps, Taps);
            int first = i0 - (Taps / 2) + 1;
            double sum = 0;
            for (int t = 0; t < Taps; t++)
            {
                int i = first + t;
                if ((uint)i < (uint)input.Length)
                {
                    sum += kernel[t] * input[i];
                }
            }

            output[j] = (float)sum;
        }

        return output;
    }

    /// <summary>The windowed sinc at each phase: tap <c>t</c> of phase <c>p</c> weighs the input <c>t − Taps/2 + 1 − p/Phases</c> samples from the output point.</summary>
    private static float[] BuildKernel()
    {
        double cutoff = CutoffHz / ChipRateHz;
        double half = Taps / 2.0;
        float[] kernel = new float[Phases * Taps];
        for (int p = 0; p < Phases; p++)
        {
            double fraction = (double)p / Phases;
            double[] row = new double[Taps];
            for (int t = 0; t < Taps; t++)
            {
                double d = t - half + 1 - fraction;
                double sinc = d == 0 ? 1 : Math.Sin(2 * Math.PI * cutoff * d) / (2 * Math.PI * cutoff * d);
                double window = Math.Abs(d) >= half ? 0 : 0.42 + (0.5 * Math.Cos(Math.PI * d / half)) + (0.08 * Math.Cos(2 * Math.PI * d / half));
                row[t] = sinc * window;
            }

            double sum = row.Sum();
            for (int t = 0; t < Taps; t++)
            {
                kernel[(p * Taps) + t] = (float)(row[t] / sum);
            }
        }

        return kernel;
    }
}
