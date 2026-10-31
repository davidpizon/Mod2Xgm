namespace Mod2Xgm.Render;

/// <summary>
/// Brings the Genesis render from the YM3438's own rate (about 53 kHz) to the output rate (docs/adr/0059-genesis-renderer.md):
/// a 32-tap Kaiser-windowed sinc from a table of <see cref="Phases"/> filters, each summing to 1. Going down, the cutoff
/// sits a little below the output's Nyquist frequency, so the chip's sound above it is filtered out rather than folded
/// back. Much faster than the sample processor's resampler, which minutes of 53 kHz audio would keep busy for longer
/// than the chips take to play them.
/// </summary>
internal sealed class ChipRateResampler
{
    /// <summary>Source samples each output sample is made from.</summary>
    private const int Taps = 32;

    /// <summary>Taps before the current source sample.</summary>
    private const int TapsBefore = (Taps / 2) - 1;

    /// <summary>Fractions of a source sample the table holds a filter for.</summary>
    private const int Phases = 1024;

    /// <summary>Kaiser window β: about 80 dB down outside the pass band.</summary>
    private const double Beta = 8;

    /// <summary>Cutoff as a share of the lower of the two Nyquist frequencies.</summary>
    private const double Cutoff = 0.92;

    private readonly float[] _filters = new float[Phases * Taps];
    private readonly double _step;

    /// <summary>Prepares a resampler from <paramref name="fromHz"/> to <paramref name="toHz"/>.</summary>
    public ChipRateResampler(double fromHz, double toHz)
    {
        if (!double.IsFinite(fromHz) || fromHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fromHz), fromHz, "The rate must be a positive number.");
        }

        if (!double.IsFinite(toHz) || toHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(toHz), toHz, "The rate must be a positive number.");
        }

        _step = fromHz / toHz;
        double cutoff = Math.Min(1, toHz / fromHz) * Cutoff;
        double[] filter = new double[Taps];
        for (int phase = 0; phase < Phases; phase++)
        {
            double fraction = (double)phase / Phases;
            double sum = 0;
            for (int tap = 0; tap < Taps; tap++)
            {
                double x = tap - TapsBefore - fraction;
                filter[tap] = cutoff * SincTable.Sinc(cutoff * x) * SincTable.Kaiser(Beta, x / (Taps / 2.0));
                sum += filter[tap];
            }

            for (int tap = 0; tap < Taps; tap++)
            {
                _filters[(phase * Taps) + tap] = (float)(filter[tap] / sum);
            }
        }
    }

    /// <summary>
    /// <paramref name="data"/> at the new rate: ⌊length ÷ step⌋ samples, each the filtered source around its position.
    /// Outside the data the source counts as silent.
    /// </summary>
    public float[] Resample(float[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        int length = (int)Math.Floor(data.Length / _step);
        float[] result = new float[length];
        for (int n = 0; n < length; n++)
        {
            double position = n * _step;
            int index = (int)position;
            int phase = Math.Min((int)((position - index) * Phases), Phases - 1);
            ReadOnlySpan<float> filter = _filters.AsSpan(phase * Taps, Taps);
            int first = index - TapsBefore;
            float sum = 0;
            if (first >= 0 && first + Taps <= data.Length)
            {
                ReadOnlySpan<float> source = data.AsSpan(first, Taps);
                for (int tap = 0; tap < Taps; tap++)
                {
                    sum += source[tap] * filter[tap];
                }
            }
            else
            {
                for (int tap = 0; tap < Taps; tap++)
                {
                    int at = first + tap;
                    if (at >= 0 && at < data.Length)
                    {
                        sum += data[at] * filter[tap];
                    }
                }
            }

            result[n] = sum;
        }

        return result;
    }
}
