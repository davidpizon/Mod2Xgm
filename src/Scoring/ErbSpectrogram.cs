namespace Mod2Xgm.Scoring;

/// <summary>
/// Turns audio into band powers on the ERB scale (equivalent rectangular bandwidth, Glasberg and Moore): bands as wide as
/// the ear's own filters, which the perceptual features compare (docs/adr/0100-perceptual-features.md). The bands are
/// triangles on an FFT, like <see cref="MelAnalyzer"/>'s, spaced evenly in ERB numbers instead of mels.
/// </summary>
internal sealed class ErbSpectrogram
{
    /// <summary>The lowest band edge, in Hz: below it the corpus has little that the ear hears as pitch.</summary>
    private const double LowHz = 50;

    /// <summary>The highest band edge, in Hz, when the sample rate allows it (at most 95 % of half the rate).</summary>
    private const double HighHz = 16_000;

    private readonly int _hop;
    private readonly int _fftSize;
    private readonly FftPlan _fft;
    private readonly double[] _window;
    private readonly double _powerScale;
    private readonly (int FirstBin, double[] Weights)[] _bands;

    /// <summary>Prepares an analysis with the given frame size, step and number of bands.</summary>
    /// <param name="sampleRate">Samples per second of the audio that will be analyzed.</param>
    /// <param name="fftSize">Frame length in samples; a power of two.</param>
    /// <param name="hopSize">Samples between the starts of two frames.</param>
    /// <param name="bands">Number of ERB bands between <see cref="LowHz"/> and the top edge.</param>
    public ErbSpectrogram(int sampleRate, int fftSize, int hopSize, int bands)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(hopSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bands, 0);
        _hop = hopSize;
        _fftSize = fftSize;
        _fft = new FftPlan(fftSize);

        // Periodic Hann window, scaled so a full-scale sine measures a power of 1 (0 dB), as in MelAnalyzer.
        _window = new double[fftSize];
        double windowSum = 0;
        for (int i = 0; i < fftSize; i++)
        {
            _window[i] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / fftSize));
            windowSum += _window[i];
        }

        double peak = windowSum / 2;
        _powerScale = 1 / (peak * peak);
        double top = Math.Min(HighHz, 0.95 * sampleRate / 2);
        CentresErb = new double[bands];
        _bands = BuildBands(sampleRate, fftSize, bands, top, CentresErb);
    }

    /// <summary>Each band's centre as an ERB number (ERB-rate), so neighbours can be spaced in the ear's own units.</summary>
    public double[] CentresErb { get; }

    /// <summary>Number of bands in each frame.</summary>
    public int Bands => _bands.Length;

    /// <summary>Samples between the starts of two frames.</summary>
    public int HopSize => _hop;

    /// <summary>Frames needed to cover <paramref name="totalSamples"/> samples: the last frame may run past the end (zero-padded).</summary>
    /// <param name="totalSamples">Length of the audio.</param>
    private int FrameCount(int totalSamples) =>
        totalSamples <= _fftSize ? 1 : 1 + (int)Math.Ceiling((double)(totalSamples - _fftSize) / _hop);

    /// <summary>The ERB number of <paramref name="hertz"/>: 21.4 · log10(1 + 0.00437 f) (Glasberg and Moore, 1990).</summary>
    public static double ErbNumber(double hertz) => 21.4 * Math.Log10(1 + (0.00437 * hertz));

    /// <summary>The frequency, in Hz, of ERB number <paramref name="erb"/>; the inverse of <see cref="ErbNumber"/>.</summary>
    public static double Hertz(double erb) => (Math.Pow(10, erb / 21.4) - 1) / 0.00437;

    /// <summary>
    /// The band powers of two signals as if each were <paramref name="totalSamples"/> long (shorter audio is followed by
    /// silence), so both give the same frame count. Both go through one complex FFT per frame, one in the real part and
    /// one in the imaginary part, which halves the work of the comparison that always needs both.
    /// </summary>
    /// <param name="first">The first signal (the original, in a comparison).</param>
    /// <param name="second">The second signal (the rendering).</param>
    /// <param name="totalSamples">The length to analyze; at least each signal's length.</param>
    /// <returns>For each signal, one array of <see cref="Bands"/> linear powers per frame (1 = a full-scale sine's peak bin).</returns>
    public (double[][] First, double[][] Second) Power(ReadOnlySpan<float> first, ReadOnlySpan<float> second, int totalSamples)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(totalSamples, Math.Max(first.Length, second.Length));
        int frames = FrameCount(totalSamples);
        double[][] a = new double[frames][];
        double[][] b = new double[frames][];
        double[] real = new double[_fftSize];
        double[] imag = new double[_fftSize];
        double[] powerA = new double[(_fftSize / 2) + 1];
        double[] powerB = new double[(_fftSize / 2) + 1];

        for (int t = 0; t < frames; t++)
        {
            int start = t * _hop;
            for (int i = 0; i < _fftSize; i++)
            {
                int index = start + i;
                real[i] = index < first.Length ? first[index] * _window[i] : 0;
                imag[i] = index < second.Length ? second[index] * _window[i] : 0;
            }

            _fft.Forward(real, imag);
            Unpack(real, imag, powerA, powerB, _powerScale);
            a[t] = Bin(powerA);
            b[t] = Bin(powerB);
        }

        return (a, b);
    }

    /// <summary>
    /// Splits the transform of <c>x + i·y</c> (two real signals) into each one's power spectrum, from bin 0 to
    /// <c>powerA.Length − 1</c>: X[k] = (Z[k] + conj Z[N−k]) / 2 and Y[k] = (Z[k] − conj Z[N−k]) / 2i.
    /// </summary>
    internal static void Unpack(double[] real, double[] imag, double[] powerA, double[] powerB, double scale)
    {
        int n = real.Length;
        for (int k = 0; k < powerA.Length; k++)
        {
            int m = (n - k) % n;
            double ar = real[k] + real[m];
            double ai = imag[k] - imag[m];
            double br = imag[k] + imag[m];
            double bi = real[m] - real[k];
            powerA[k] = ((ar * ar) + (ai * ai)) * 0.25 * scale;
            powerB[k] = ((br * br) + (bi * bi)) * 0.25 * scale;
        }
    }

    /// <summary>One frame's power spectrum summed into the bands.</summary>
    private double[] Bin(double[] power)
    {
        double[] row = new double[_bands.Length];
        for (int b = 0; b < _bands.Length; b++)
        {
            (int firstBin, double[] weights) = _bands[b];
            double energy = 0;
            for (int k = 0; k < weights.Length; k++)
            {
                energy += weights[k] * power[firstBin + k];
            }

            row[b] = energy;
        }

        return row;
    }

    /// <summary>
    /// Triangular filters, equally spaced in ERB numbers from <see cref="LowHz"/> to <paramref name="topHz"/>. A filter
    /// narrower than one FFT bin would be empty, so it takes the bin nearest its centre instead.
    /// </summary>
    private static (int FirstBin, double[] Weights)[] BuildBands(int sampleRate, int fftSize, int bands, double topHz, double[] centres)
    {
        int bins = (fftSize / 2) + 1;
        double low = ErbNumber(LowHz);
        double high = ErbNumber(topHz);
        double[] edges = new double[bands + 2];
        for (int i = 0; i < edges.Length; i++)
        {
            edges[i] = low + ((high - low) * i / (bands + 1));
        }

        (int, double[])[] result = new (int, double[])[bands];
        for (int b = 0; b < bands; b++)
        {
            centres[b] = edges[b + 1];
            double lowBin = Hertz(edges[b]) * fftSize / sampleRate;
            double centreBin = Hertz(edges[b + 1]) * fftSize / sampleRate;
            double highBin = Hertz(edges[b + 2]) * fftSize / sampleRate;
            int first = Math.Max(0, (int)Math.Ceiling(lowBin));
            int last = Math.Min(bins - 1, (int)Math.Floor(highBin));
            List<double> weights = [];
            for (int k = first; k <= last; k++)
            {
                double w = k <= centreBin ? (k - lowBin) / (centreBin - lowBin) : (highBin - k) / (highBin - centreBin);
                weights.Add(Math.Max(0, w));
            }

            if (weights.Count == 0 || weights.Sum() <= 0)
            {
                first = Math.Min(bins - 1, (int)Math.Round(centreBin));
                weights = [1];
            }

            result[b] = (first, [.. weights]);
        }

        return result;
    }
}
