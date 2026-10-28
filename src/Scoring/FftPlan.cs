namespace Mod2Xgm.Scoring;

/// <summary>
/// A radix-2 fast Fourier transform of one fixed size, with its twiddle factors and bit-reversal table worked out
/// once. Pure C# so the scorer has no native dependency (docs/architecture.md), and deterministic: the same input gives
/// the same bits on every platform.
/// </summary>
internal sealed class FftPlan
{
    private readonly double[] _cos;
    private readonly double[] _sin;
    private readonly int[] _reverse;

    /// <summary>Prepares a transform of <paramref name="size"/> points.</summary>
    /// <param name="size">A power of two, at least 2.</param>
    public FftPlan(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "The FFT size must be a power of two, at least 2.");
        }

        Size = size;
        _cos = new double[size / 2];
        _sin = new double[size / 2];
        for (int k = 0; k < size / 2; k++)
        {
            double angle = 2 * Math.PI * k / size;
            _cos[k] = Math.Cos(angle);
            _sin[k] = Math.Sin(angle);
        }

        int bits = Math.ILogB(size);
        _reverse = new int[size];
        for (int i = 0; i < size; i++)
        {
            int reversed = 0;
            for (int b = 0; b < bits; b++)
            {
                reversed |= ((i >> b) & 1) << (bits - 1 - b);
            }

            _reverse[i] = reversed;
        }
    }

    /// <summary>Number of points.</summary>
    public int Size { get; }

    /// <summary>The forward transform (e<sup>−iωt</sup>), in place: <paramref name="real"/> and <paramref name="imag"/> hold the input, then the spectrum.</summary>
    /// <param name="real">Real parts, <see cref="Size"/> long.</param>
    /// <param name="imag">Imaginary parts, <see cref="Size"/> long.</param>
    public void Forward(double[] real, double[] imag)
    {
        ArgumentNullException.ThrowIfNull(real);
        ArgumentNullException.ThrowIfNull(imag);
        if (real.Length != Size || imag.Length != Size)
        {
            throw new ArgumentException($"Both arrays must hold {Size} points.");
        }

        for (int i = 0; i < Size; i++)
        {
            int j = _reverse[i];
            if (j > i)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        for (int length = 2; length <= Size; length <<= 1)
        {
            int half = length / 2;
            int step = Size / length;
            for (int start = 0; start < Size; start += length)
            {
                for (int k = 0; k < half; k++)
                {
                    double wr = _cos[k * step];
                    double wi = -_sin[k * step];
                    int a = start + k;
                    int b = a + half;
                    double tr = (real[b] * wr) - (imag[b] * wi);
                    double ti = (real[b] * wi) + (imag[b] * wr);
                    real[b] = real[a] - tr;
                    imag[b] = imag[a] - ti;
                    real[a] += tr;
                    imag[a] += ti;
                }
            }
        }
    }
}
