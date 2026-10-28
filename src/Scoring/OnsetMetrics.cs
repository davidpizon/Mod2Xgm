namespace Mod2Xgm.Scoring;

/// <summary>How well one list of note-start times matches another.</summary>
/// <param name="Precision">Share of the candidate's onsets that match a reference onset; 1 when it has none.</param>
/// <param name="Recall">Share of the reference's onsets the candidate has; 1 when the reference has none.</param>
/// <param name="F1">The harmonic mean of the two; 1 when neither list has an onset.</param>
/// <param name="MeanTimingErrorSeconds">Mean distance between matched onsets, or <c>null</c> when none matched.</param>
/// <param name="Matched">Number of matched pairs.</param>
internal readonly record struct OnsetComparison(double Precision, double Recall, double F1, double? MeanTimingErrorSeconds, int Matched);

/// <summary>
/// Finds where sounds start (spectral flux: how fast the log-mel spectrum rises) and compares two songs' note starts.
/// A song can match in tone and still be late or miss notes; this catches that (docs/adr/0042-scoring-metrics.md).
/// </summary>
internal static class OnsetMetrics
{
    private const int FftSize = 1024;
    /// <summary>Samples between points of the novelty curve.</summary>
    internal const int HopSize = 256;
    private const int MelBands = 48;

    /// <summary>An onset must rise this many dB (average over bands) above the local average of the curve.</summary>
    private const double PeakHeightDb = 1.5;

    /// <summary>Half-width, in seconds, of the stretch the local average is taken over.</summary>
    private const double LocalAverageSeconds = 0.4;

    /// <summary>Two onsets closer than this are one: the louder wins.</summary>
    private const double MinSeparationSeconds = 0.03;

    /// <summary>
    /// Where in a frame, as a fraction of its length, a note start is when the curve first rises: the Hann window hides
    /// an attack in its first part, so it is seen once it is about three quarters of the way in.
    /// </summary>
    private const double AttackPosition = 0.75;

    /// <summary>How far apart two onsets may be and still be the same note.</summary>
    public const double DefaultToleranceSeconds = 0.05;

    /// <summary>Seconds between points of the curve from <see cref="NoveltyCurve"/>.</summary>
    /// <param name="sampleRate">Sample rate of the analyzed audio.</param>
    public static double CurveStepSeconds(int sampleRate) => (double)HopSize / sampleRate;

    /// <summary>
    /// How much the spectrum rises at every step: the sum over bands of the increase in dB since the previous frame
    /// (falls count as 0), divided by the number of bands. Point 0 is 0.
    /// </summary>
    /// <param name="signal">The audio.</param>
    /// <param name="totalSamples">The length to analyze (for equal-length curves); at least the signal's length.</param>
    public static double[] NoveltyCurve(AudioSignal signal, int totalSamples)
    {
        ArgumentNullException.ThrowIfNull(signal);
        MelAnalyzer analyzer = new(signal.SampleRate, FftSize, HopSize, MelBands);
        double[][] frames = analyzer.LogMel(signal.Samples, totalSamples);
        double[] novelty = new double[frames.Length];
        for (int t = 1; t < frames.Length; t++)
        {
            if ((t * HopSize) + FftSize > signal.Length)
            {
                // A frame that runs past the end holds the abrupt cut-off of the audio, which is not a note start.
                break;
            }

            double rise = 0;
            for (int b = 0; b < MelBands; b++)
            {
                rise += Math.Max(0, frames[t][b] - frames[t - 1][b]);
            }

            novelty[t] = rise / MelBands;
        }

        return novelty;
    }

    /// <summary>The times, in seconds, at which sounds start in <paramref name="signal"/>.</summary>
    /// <param name="signal">The audio.</param>
    public static IReadOnlyList<double> Detect(AudioSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        double[] novelty = NoveltyCurve(signal, signal.Length);
        double step = CurveStepSeconds(signal.SampleRate);
        int window = Math.Max(1, (int)Math.Round(LocalAverageSeconds / step));
        int separation = Math.Max(1, (int)Math.Round(MinSeparationSeconds / step));

        List<int> peaks = [];
        for (int t = 1; t < novelty.Length; t++)
        {
            if (novelty[t] <= 0 || novelty[t] < novelty[t - 1] || (t + 1 < novelty.Length && novelty[t] <= novelty[t + 1]))
            {
                continue;
            }

            int from = Math.Max(0, t - window);
            int to = Math.Min(novelty.Length - 1, t + window);
            double sum = 0;
            for (int i = from; i <= to; i++)
            {
                sum += novelty[i];
            }

            if (novelty[t] < (sum / (to - from + 1)) + PeakHeightDb)
            {
                continue;
            }

            if (peaks.Count > 0 && t - peaks[^1] < separation)
            {
                if (novelty[t] > novelty[peaks[^1]])
                {
                    peaks[^1] = t;
                }

                continue;
            }

            peaks.Add(t);
        }

        return [.. peaks.Select(t => ((t * HopSize) + (AttackPosition * FftSize)) / signal.SampleRate)];
    }

    /// <summary>
    /// Matches each reference onset to at most one candidate onset within <paramref name="toleranceSeconds"/> (nearest
    /// first, in time order) and scores the match.
    /// </summary>
    /// <param name="reference">Onset times of the original, ascending.</param>
    /// <param name="candidate">Onset times of the rendering being judged, ascending.</param>
    /// <param name="toleranceSeconds">Largest distance that still counts as the same note.</param>
    public static OnsetComparison Compare(IReadOnlyList<double> reference, IReadOnlyList<double> candidate, double toleranceSeconds = DefaultToleranceSeconds)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        int r = 0;
        int c = 0;
        int matched = 0;
        double errorSum = 0;
        while (r < reference.Count && c < candidate.Count)
        {
            double diff = candidate[c] - reference[r];
            if (Math.Abs(diff) <= toleranceSeconds)
            {
                matched++;
                errorSum += Math.Abs(diff);
                r++;
                c++;
            }
            else if (diff < 0)
            {
                c++;
            }
            else
            {
                r++;
            }
        }

        double precision = candidate.Count == 0 ? 1 : (double)matched / candidate.Count;
        double recall = reference.Count == 0 ? 1 : (double)matched / reference.Count;
        double f1 = reference.Count == 0 && candidate.Count == 0
            ? 1
            : precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        return new OnsetComparison(precision, recall, f1, matched == 0 ? null : errorSum / matched, matched);
    }
}
