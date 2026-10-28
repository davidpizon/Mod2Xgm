namespace Mod2Xgm.Scoring;

/// <summary>
/// Lines a rendering up with the original. Two renderings of one song are rarely at the same offset (a player's lead-in,
/// a driver's start-up), and every metric would punish that. The offset is found by sliding the candidate's
/// note-start curve over the original's (docs/adr/0042-scoring-metrics.md).
/// </summary>
internal static class Alignment
{
    /// <summary>
    /// How late the candidate is compared with the reference: positive when its music starts later. Found on the
    /// spectral-flux curves, so the answer is a whole number of curve steps (about 5.8 ms at 44.1 kHz).
    /// </summary>
    /// <param name="reference">The original rendering.</param>
    /// <param name="candidate">The rendering to line up; the same sample rate.</param>
    /// <param name="maxLagSeconds">Largest offset to look for, either way.</param>
    /// <returns>The lag in samples (a multiple of the curve step); 0 when either signal has no note starts to go by.</returns>
    public static int EstimateLagSamples(AudioSignal reference, AudioSignal candidate, double maxLagSeconds)
    {
        SpectralMetrics.CheckSameRate(reference, candidate);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLagSeconds);
        int total = Math.Max(reference.Length, candidate.Length);
        double[] a = Center(OnsetMetrics.NoveltyCurve(reference, total));
        double[] b = Center(OnsetMetrics.NoveltyCurve(candidate, total));
        int maxLag = (int)Math.Round(maxLagSeconds / OnsetMetrics.CurveStepSeconds(reference.SampleRate));
        maxLag = Math.Min(maxLag, a.Length - 1);

        // Smaller lags first and a strict comparison: a repeating beat matches at every multiple of its period, and the
        // nearest match is the likely one.
        double bestScore = 0;
        int bestLag = 0;
        foreach (int lag in Enumerable.Range(0, (2 * maxLag) + 1).Select(i => (i + 1) / 2 * (i % 2 == 1 ? 1 : -1)))
        {
            // b[t] is compared with a[t - lag]: a candidate that is late by `lag` steps has its events at t = s + lag.
            int from = Math.Max(0, lag);
            int to = Math.Min(a.Length, a.Length + lag);
            double dot = 0;
            for (int t = from; t < to; t++)
            {
                dot += b[t] * a[t - lag];
            }

            double score = dot;
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        return bestLag * OnsetMetrics.HopSize;
    }

    /// <summary>The candidate moved earlier or later so it lines up with the reference.</summary>
    /// <param name="reference">The original rendering.</param>
    /// <param name="candidate">The rendering to line up; the same sample rate.</param>
    /// <param name="maxLagSeconds">Largest offset to correct, either way.</param>
    /// <param name="lagSamples">The offset that was found and removed.</param>
    public static AudioSignal Align(AudioSignal reference, AudioSignal candidate, double maxLagSeconds, out int lagSamples)
    {
        lagSamples = EstimateLagSamples(reference, candidate, maxLagSeconds);
        return candidate.Shifted(-lagSamples);
    }

    private static double[] Center(double[] curve)
    {
        double mean = curve.Length == 0 ? 0 : curve.Average();
        return [.. curve.Select(v => v - mean)];
    }
}
