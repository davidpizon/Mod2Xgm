using Mod2Xgm.Analysis;

namespace Mod2Xgm.Optimizer;

/// <summary>
/// Brings a chip's output to <see cref="SampleAnalyzer.AnalysisRateHz"/>, so a candidate note can be measured as the
/// sample's regions are: a Blackman-windowed sinc low-pass read at each output point. The converter's resampler (NWaves,
/// ADR 0038) is built for output quality and takes most of a fit's time at a fixed ratio; this one is about 50 times
/// faster and its error stays far below the distances it serves (docs/adr/0069-fm-patch-fitting.md).
/// </summary>
internal sealed class AnalysisResampler
{
    /// <summary>Fractional positions the filter is tabulated at.</summary>
    private const int Phases = 512;

    /// <summary>The filter's cutoff, below the analysis rate's half (22 050 Hz), so what is above it doesn't fold back.</summary>
    private const double CutoffHz = 19_500;

    /// <summary>The filter, <see cref="_taps"/> per phase, each phase summing to 1.</summary>
    private readonly float[] _kernel;

    private readonly int _taps;

    /// <summary>
    /// A resampler from <paramref name="inputRateHz"/> with a filter <paramref name="taps"/> input samples long: enough
    /// that it falls from <see cref="CutoffHz"/> to silence before 22 kHz (the faster the input, the more taps that takes).
    /// </summary>
    public AnalysisResampler(double inputRateHz, int taps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inputRateHz, SampleAnalyzer.AnalysisRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(taps);
        InputRateHz = inputRateHz;
        _taps = taps;
        _kernel = BuildKernel(inputRateHz, taps);
    }

    /// <summary>The rate <see cref="Resample"/> takes.</summary>
    public double InputRateHz { get; }

    /// <summary><paramref name="input"/>, at <see cref="InputRateHz"/>, at <see cref="SampleAnalyzer.AnalysisRateHz"/>.</summary>
    public float[] Resample(float[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        double step = InputRateHz / SampleAnalyzer.AnalysisRateHz;
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

            ReadOnlySpan<float> kernel = _kernel.AsSpan(phase * _taps, _taps);
            int first = i0 - (_taps / 2) + 1;
            double sum = 0;
            for (int t = 0; t < _taps; t++)
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

    /// <summary>The windowed sinc at each phase: tap <c>t</c> of phase <c>p</c> weighs the input <c>t − taps/2 + 1 − p/Phases</c> samples from the output point.</summary>
    private static float[] BuildKernel(double inputRateHz, int taps)
    {
        double cutoff = CutoffHz / inputRateHz;
        double half = taps / 2.0;
        float[] kernel = new float[Phases * taps];
        for (int p = 0; p < Phases; p++)
        {
            double fraction = (double)p / Phases;
            double[] row = new double[taps];
            for (int t = 0; t < taps; t++)
            {
                double d = t - half + 1 - fraction;
                double sinc = d == 0 ? 1 : Math.Sin(2 * Math.PI * cutoff * d) / (2 * Math.PI * cutoff * d);
                double window = Math.Abs(d) >= half ? 0 : 0.42 + (0.5 * Math.Cos(Math.PI * d / half)) + (0.08 * Math.Cos(2 * Math.PI * d / half));
                row[t] = sinc * window;
            }

            double sum = row.Sum();
            for (int t = 0; t < taps; t++)
            {
                kernel[(p * taps) + t] = (float)(row[t] / sum);
            }
        }

        return kernel;
    }
}
