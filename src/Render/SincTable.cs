namespace Mod2Xgm.Render;

/// <summary>
/// An 8-tap Kaiser-windowed sinc interpolator, like OpenMPT's default resampler for MODs (docs/adr/0054-mod-renderer.md):
/// a table of <see cref="Phases"/> filters, one per fraction of a source sample. Each filter reads the source samples
/// at offsets −3 … +4 from the current one and sums to 1, so a constant signal keeps its level. Like OpenMPT, a voice
/// played faster than the output rate switches to a lower cutoff (<see cref="For"/>); the three tables' responses were
/// matched to libopenmpt's within 0.3 dB.
/// </summary>
internal sealed class SincTable
{
    /// <summary>Source samples each output sample is made from.</summary>
    public const int Taps = 8;

    /// <summary>Fractions of a source sample the table holds a filter for.</summary>
    public const int Phases = 4096;

    /// <summary>Taps before the current source sample (offsets −3 … −1).</summary>
    public const int TapsBefore = 3;

    private readonly float[] _filters;

    /// <summary>Builds the table for a low-pass at <paramref name="cutoff"/> × the source's Nyquist frequency.</summary>
    /// <param name="beta">Kaiser window β: higher cuts more far from the cutoff and less near it.</param>
    /// <param name="cutoff">Cutoff as a share of the source's Nyquist frequency.</param>
    private SincTable(double beta, double cutoff)
    {
        _filters = new float[Phases * Taps];
        double[] filter = new double[Taps];
        for (int phase = 0; phase < Phases; phase++)
        {
            double fraction = (double)phase / Phases;
            double sum = 0;
            for (int tap = 0; tap < Taps; tap++)
            {
                double x = tap - TapsBefore - fraction;
                filter[tap] = cutoff * Sinc(cutoff * x) * Kaiser(beta, x / (Taps / 2.0));
                sum += filter[tap];
            }

            for (int tap = 0; tap < Taps; tap++)
            {
                _filters[(phase * Taps) + tap] = (float)(filter[tap] / sum);
            }
        }
    }

    /// <summary>For playing a sample at up to <see cref="Downsample13xFrom"/> times the output rate (OpenMPT's Kaiser sinc).</summary>
    public static SincTable Normal { get; } = new(9.6377, 0.97);

    /// <summary>For playing a sample at <see cref="Downsample13xFrom"/> to <see cref="Downsample2xFrom"/> times the output rate (OpenMPT's 1.3× table).</summary>
    public static SincTable Downsample13x { get; } = new(8.5, 0.5);

    /// <summary>For playing a sample at more than <see cref="Downsample2xFrom"/> times the output rate: fitted to libopenmpt's response.</summary>
    public static SincTable Downsample2x { get; } = new(7.25, 0.415);

    /// <summary>The increment above which OpenMPT switches to <see cref="Downsample13x"/> (0x1.3 in its 32.32 fixed point).</summary>
    public const double Downsample13xFrom = 1.1875;

    /// <summary>The increment above which OpenMPT switches to <see cref="Downsample2x"/>.</summary>
    public const double Downsample2xFrom = 1.5;

    /// <summary>The table for a voice that steps <paramref name="increment"/> source samples per output sample.</summary>
    public static SincTable For(double increment) => increment switch
    {
        > Downsample2xFrom => Downsample2x,
        > Downsample13xFrom => Downsample13x,
        _ => Normal,
    };

    /// <summary>The filter for a position <paramref name="fraction"/> (0 ≤ fraction &lt; 1) past the current source sample.</summary>
    public ReadOnlySpan<float> Filter(double fraction)
    {
        int phase = Math.Clamp((int)(fraction * Phases), 0, Phases - 1);
        return _filters.AsSpan(phase * Taps, Taps);
    }

    /// <summary>sin(πx) / (πx), 1 at 0.</summary>
    private static double Sinc(double x) => Math.Abs(x) < 1e-12 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);

    /// <summary>The Kaiser window with <paramref name="beta"/> at <paramref name="x"/> (−1 … +1 across the filter; 0 outside).</summary>
    private static double Kaiser(double beta, double x) => Math.Abs(x) >= 1 ? 0 : BesselI0(beta * Math.Sqrt(1 - (x * x))) / BesselI0(beta);

    /// <summary>The modified Bessel function of the first kind, order 0, by its power series.</summary>
    private static double BesselI0(double x)
    {
        double sum = 1;
        double term = 1;
        double half = x / 2;
        for (int k = 1; k < 50 && term > sum * 1e-17; k++)
        {
            term *= half / k * (half / k);
            sum += term;
        }

        return sum;
    }
}
