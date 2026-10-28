namespace Mod2Xgm.Scoring;

/// <summary>One analysis scale of the multi-resolution spectral distance.</summary>
/// <param name="FftSize">Frame length in samples; a power of two.</param>
/// <param name="HopSize">Samples between frames (a quarter of the frame is usual).</param>
/// <param name="MelBands">Mel bands; small frames resolve fewer bands.</param>
internal readonly record struct MelResolution(int FftSize, int HopSize, int MelBands)
{
    /// <summary>
    /// Four scales from about 12 ms to 93 ms at 44.1 kHz: short frames judge attacks and timing, long frames judge pitch
    /// and tone (docs/adr/0042-scoring-metrics.md).
    /// </summary>
    public static IReadOnlyList<MelResolution> Default { get; } =
    [
        new(512, 128, 32),
        new(1024, 256, 48),
        new(2048, 512, 64),
        new(4096, 1024, 80),
    ];
}

/// <summary>The spectral half of the score: how different two sounds look in log-mel spectrograms and in MFCCs.</summary>
internal static class SpectralMetrics
{
    /// <summary>Number of cepstral coefficients compared by <see cref="MfccDistance"/> (c1…c13; c0 is loudness).</summary>
    private const int MfccCount = 13;

    /// <summary>Mel bands the MFCCs are computed from.</summary>
    private const int MfccBands = 40;

    private const int MfccFftSize = 1024;
    private const int MfccHopSize = 256;

    /// <summary>
    /// The multi-resolution log-mel distance: at each scale the mean absolute difference, over all frames and bands, of
    /// the two spectrograms in dB; then the mean over the scales. 0 means identical; a signal that is 6 dB quieter
    /// everywhere scores about 6. The shorter signal is treated as followed by silence, so missing music is penalized.
    /// </summary>
    /// <param name="reference">The original rendering.</param>
    /// <param name="candidate">The rendering being judged; the same sample rate.</param>
    /// <param name="resolutions">The scales to use, or <c>null</c> for <see cref="MelResolution.Default"/>.</param>
    /// <returns>The distance in decibels.</returns>
    public static double MelDistanceDb(AudioSignal reference, AudioSignal candidate, IReadOnlyList<MelResolution>? resolutions = null)
    {
        CheckSameRate(reference, candidate);
        IReadOnlyList<MelResolution> scales = resolutions ?? MelResolution.Default;
        if (scales.Count == 0)
        {
            throw new ArgumentException("At least one resolution is needed.", nameof(resolutions));
        }

        int total = Math.Max(reference.Length, candidate.Length);
        double sum = 0;
        foreach (MelResolution scale in scales)
        {
            MelAnalyzer analyzer = new(reference.SampleRate, scale.FftSize, scale.HopSize, scale.MelBands);
            double[][] a = analyzer.LogMel(reference.Samples, total);
            double[][] b = analyzer.LogMel(candidate.Samples, total);
            sum += MeanAbsoluteDifference(a, b);
        }

        return sum / scales.Count;
    }

    /// <summary>
    /// The MFCC distance (as in Sound2Synth, docs/references/sound2synth-2022.md): each frame's log-mel spectrum is
    /// reduced to 13 cepstral coefficients that describe its timbre; the distance is the mean Euclidean distance between
    /// the two songs' coefficient vectors, frame by frame. Coefficient 0 (overall loudness) is left out, so this tracks
    /// <b>tone colour</b> and complements <see cref="MelDistanceDb"/>, which also sees loudness.
    /// </summary>
    /// <param name="reference">The original rendering.</param>
    /// <param name="candidate">The rendering being judged; the same sample rate.</param>
    /// <returns>0 for identical timbre, larger as it differs (in dB-like units).</returns>
    public static double MfccDistance(AudioSignal reference, AudioSignal candidate)
    {
        CheckSameRate(reference, candidate);
        int total = Math.Max(reference.Length, candidate.Length);
        MelAnalyzer analyzer = new(reference.SampleRate, MfccFftSize, MfccHopSize, MfccBands);
        double[][] a = analyzer.LogMel(reference.Samples, total);
        double[][] b = analyzer.LogMel(candidate.Samples, total);
        double[][] basis = DctBasis(MfccBands, MfccCount);

        double sum = 0;
        for (int t = 0; t < a.Length; t++)
        {
            double squares = 0;
            for (int c = 0; c < MfccCount; c++)
            {
                double diff = 0;
                for (int band = 0; band < MfccBands; band++)
                {
                    diff += basis[c][band] * (a[t][band] - b[t][band]);
                }

                squares += diff * diff;
            }

            sum += Math.Sqrt(squares);
        }

        return sum / a.Length;
    }

    /// <summary>Throws when the two signals aren't at the same sample rate (resample first).</summary>
    internal static void CheckSameRate(AudioSignal reference, AudioSignal candidate)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        if (reference.SampleRate != candidate.SampleRate)
        {
            throw new ArgumentException($"The signals must have the same sample rate ({reference.SampleRate} Hz and {candidate.SampleRate} Hz given); resample one first.", nameof(candidate));
        }
    }

    private static double MeanAbsoluteDifference(double[][] a, double[][] b)
    {
        double sum = 0;
        long count = 0;
        for (int t = 0; t < a.Length; t++)
        {
            for (int band = 0; band < a[t].Length; band++)
            {
                sum += Math.Abs(a[t][band] - b[t][band]);
                count++;
            }
        }

        return sum / count;
    }

    /// <summary>The orthonormal DCT-II rows for coefficients 1…<paramref name="count"/> over <paramref name="bands"/> inputs.</summary>
    private static double[][] DctBasis(int bands, int count)
    {
        double[][] basis = new double[count][];
        for (int c = 0; c < count; c++)
        {
            basis[c] = new double[bands];
            for (int n = 0; n < bands; n++)
            {
                basis[c][n] = Math.Sqrt(2.0 / bands) * Math.Cos(Math.PI * (c + 1) * (n + 0.5) / bands);
            }
        }

        return basis;
    }
}
