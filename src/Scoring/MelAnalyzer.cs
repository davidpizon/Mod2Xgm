namespace Mod2Xgm.Scoring;

/// <summary>
/// Turns audio into a log-mel spectrogram: for every frame, the energy in each of a set of bands spaced like hearing
/// (mel scale), in decibels. This is the picture of the sound the metrics compare (docs/adr/0042-scoring-metrics.md).
/// </summary>
internal sealed class MelAnalyzer
{
    /// <summary>The quietest level reported, in dB relative to a full-scale sine; anything below it counts as silence.</summary>
    public const double FloorDb = -80;

    private readonly int _sampleRate;
    private readonly int _hop;
    private readonly FftPlan _fft;
    private readonly double[] _window;
    private readonly double _powerScale;
    private readonly (int FirstBin, double[] Weights)[] _bands;

    /// <summary>Prepares an analysis with the given frame size, step and number of bands.</summary>
    /// <param name="sampleRate">Samples per second of the audio that will be analyzed.</param>
    /// <param name="fftSize">Frame length in samples; a power of two.</param>
    /// <param name="hopSize">Samples between the starts of two frames.</param>
    /// <param name="melBands">Number of mel bands, from 0 Hz to half the sample rate.</param>
    public MelAnalyzer(int sampleRate, int fftSize, int hopSize, int melBands)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(hopSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(melBands, 0);
        _sampleRate = sampleRate;
        _hop = hopSize;
        FftSize = fftSize;
        MelBands = melBands;
        _fft = new FftPlan(fftSize);

        // Periodic Hann window; the scale makes a full-scale sine measure 0 dB (its peak bin is sum(window) / 2).
        _window = new double[fftSize];
        double windowSum = 0;
        for (int i = 0; i < fftSize; i++)
        {
            _window[i] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / fftSize));
            windowSum += _window[i];
        }

        double peak = windowSum / 2;
        _powerScale = 1 / (peak * peak);
        _bands = BuildBands(sampleRate, fftSize, melBands);
    }

    /// <summary>Frame length in samples.</summary>
    public int FftSize { get; }

    /// <summary>Number of mel bands in each frame.</summary>
    public int MelBands { get; }

    /// <summary>Samples between the starts of two frames.</summary>
    public int HopSize => _hop;

    /// <summary>Frames needed to cover <paramref name="totalSamples"/> samples: the last frame may run past the end (zero-padded).</summary>
    /// <param name="totalSamples">Length of the audio.</param>
    public int FrameCount(int totalSamples) =>
        totalSamples <= FftSize ? 1 : 1 + (int)Math.Ceiling((double)(totalSamples - FftSize) / _hop);

    /// <summary>The time of the middle of frame <paramref name="frame"/>, in seconds.</summary>
    /// <param name="frame">Frame number, from 0.</param>
    public double FrameCenterSeconds(int frame) => ((double)frame * _hop / _sampleRate) + ((double)FftSize / 2 / _sampleRate);

    /// <summary>
    /// The log-mel spectrogram of <paramref name="samples"/> as if the audio were <paramref name="totalSamples"/> long
    /// (shorter audio is treated as followed by silence), so two signals of different lengths give the same frame count.
    /// </summary>
    /// <param name="samples">The audio.</param>
    /// <param name="totalSamples">The length to analyze; at least <c>samples.Length</c>.</param>
    /// <returns>One array of <see cref="MelBands"/> decibel values per frame, each at least <see cref="FloorDb"/>.</returns>
    public double[][] LogMel(ReadOnlySpan<float> samples, int totalSamples)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(totalSamples, samples.Length);
        int frames = FrameCount(totalSamples);
        double[][] result = new double[frames][];
        double[] real = new double[FftSize];
        double[] imag = new double[FftSize];
        double[] power = new double[(FftSize / 2) + 1];

        for (int t = 0; t < frames; t++)
        {
            int start = t * _hop;
            for (int i = 0; i < FftSize; i++)
            {
                int index = start + i;
                real[i] = index < samples.Length ? samples[index] * _window[i] : 0;
                imag[i] = 0;
            }

            _fft.Forward(real, imag);
            for (int k = 0; k < power.Length; k++)
            {
                power[k] = ((real[k] * real[k]) + (imag[k] * imag[k])) * _powerScale;
            }

            double[] row = new double[MelBands];
            for (int b = 0; b < MelBands; b++)
            {
                (int firstBin, double[] weights) = _bands[b];
                double energy = 0;
                for (int k = 0; k < weights.Length; k++)
                {
                    energy += weights[k] * power[firstBin + k];
                }

                row[b] = energy <= 0 ? FloorDb : Math.Max(FloorDb, 10 * Math.Log10(energy));
            }

            result[t] = row;
        }

        return result;
    }

    private static double Mel(double hertz) => 2595 * Math.Log10(1 + (hertz / 700));

    private static double Hertz(double mel) => 700 * (Math.Pow(10, mel / 2595) - 1);

    /// <summary>
    /// Triangular filters, equally spaced on the mel scale from 0 Hz to half the sample rate. A filter narrower than one
    /// FFT bin would be empty, so it takes the bin nearest its centre instead.
    /// </summary>
    private static (int FirstBin, double[] Weights)[] BuildBands(int sampleRate, int fftSize, int melBands)
    {
        int bins = (fftSize / 2) + 1;
        double topMel = Mel(sampleRate / 2.0);
        double[] edgeBins = new double[melBands + 2];
        for (int i = 0; i < edgeBins.Length; i++)
        {
            edgeBins[i] = Hertz(topMel * i / (melBands + 1)) * fftSize / sampleRate;
        }

        (int, double[])[] bands = new (int, double[])[melBands];
        for (int b = 0; b < melBands; b++)
        {
            double low = edgeBins[b];
            double centre = edgeBins[b + 1];
            double high = edgeBins[b + 2];
            int first = Math.Max(0, (int)Math.Ceiling(low));
            int last = Math.Min(bins - 1, (int)Math.Floor(high));
            List<double> weights = [];
            for (int k = first; k <= last; k++)
            {
                double w = k <= centre ? (k - low) / (centre - low) : (high - k) / (high - centre);
                weights.Add(Math.Max(0, w));
            }

            if (weights.Count == 0 || weights.Sum() <= 0)
            {
                first = Math.Min(bins - 1, (int)Math.Round(centre));
                weights = [1];
            }

            bands[b] = (first, [.. weights]);
        }

        return bands;
    }
}
