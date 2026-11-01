namespace Mod2Xgm.Analysis;

/// <summary>
/// The YIN pitch detector's steps (de Cheveigné and Kawahara, 2002), shared by the sample's one pitch
/// (<c>Mapping.SamplePitch</c>) and its pitch track (<see cref="SampleAnalyzer"/>): the cumulative mean normalized
/// difference of a stretch of sound with itself, and the first lag at which it repeats.
/// </summary>
internal static class Yin
{
    /// <summary>
    /// The cumulative mean normalized difference for lags 0 to <paramref name="maxLag"/>: 1 on average, near 0 at a lag
    /// that repeats the sound. Sums over the first <paramref name="windowFrames"/> frames of <paramref name="frames"/>,
    /// which needs <paramref name="windowFrames"/> + <paramref name="maxLag"/> frames.
    /// </summary>
    public static double[] Normalized(ReadOnlySpan<float> frames, int windowFrames, int maxLag)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frames.Length, windowFrames + maxLag);
        double[] normalized = new double[maxLag + 1];
        normalized[0] = 1;
        double running = 0;
        for (int lag = 1; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (int i = 0; i < windowFrames; i++)
            {
                double step = frames[i] - frames[i + lag];
                sum += step * step;
            }

            running += sum;
            normalized[lag] = running <= 0 ? 1 : sum * lag / running;
        }

        return normalized;
    }

    /// <summary>
    /// The first lag from <paramref name="minLag"/> below <paramref name="threshold"/>, followed down to its dip and
    /// refined between whole lags; <c>null</c> when no lag repeats the sound that well.
    /// </summary>
    public static double? FirstDip(double[] normalized, int minLag, double threshold)
    {
        int maxLag = normalized.Length - 1;
        for (int lag = minLag; lag < maxLag; lag++)
        {
            if (normalized[lag] >= threshold)
            {
                continue;
            }

            while (lag + 1 < maxLag && normalized[lag + 1] < normalized[lag])
            {
                lag++;
            }

            return Refine(normalized, lag);
        }

        return null;
    }

    /// <summary>
    /// The lowest lag from <paramref name="lowLag"/> to <paramref name="highLag"/>, refined between whole lags, when it is
    /// below <paramref name="threshold"/>; <c>null</c> when the sound doesn't repeat that well within those lags.
    /// </summary>
    public static double? DipNear(double[] normalized, int lowLag, int highLag, double threshold)
    {
        int best = lowLag;
        for (int lag = lowLag + 1; lag <= highLag; lag++)
        {
            if (normalized[lag] < normalized[best])
            {
                best = lag;
            }
        }

        return normalized[best] < threshold ? Refine(normalized, best) : null;
    }

    /// <summary>The minimum between whole lags, from a parabola through <paramref name="lag"/> and its neighbours.</summary>
    private static double Refine(double[] normalized, int lag)
    {
        if (lag <= 0 || lag + 1 >= normalized.Length)
        {
            return lag;
        }

        double before = normalized[lag - 1];
        double at = normalized[lag];
        double after = normalized[lag + 1];
        double curve = before - (2 * at) + after;
        return curve <= 0 ? lag : lag + ((before - after) / (2 * curve));
    }
}
