using Mod2Xgm.Model;

namespace Mod2Xgm.Mapping;

/// <summary>
/// Two cheap measures of a sample that the simple mapper uses as a stand-in until the accuracy harness can judge
/// (plan M6–M8): how <b>bright</b> it is and whether it is <b>noise</b>. Both look at the sample's repeating part when
/// it has a loop, else at its first <see cref="WindowFrames"/> frames.
/// </summary>
/// <param name="Brightness">0 (a slow sine) to about 0.7 (white noise): the RMS of the sample's slope relative to twice its RMS, which equals sin(π·f/rate) for a sine of frequency f.</param>
/// <param name="NoiseLike">True when the sample has no repeating shape: bright, and no lag of 8–512 frames correlates with itself above <see cref="NoiseCorrelation"/>.</param>
internal readonly record struct SampleTraits(double Brightness, bool NoiseLike)
{
    /// <summary>Frames examined.</summary>
    private const int WindowFrames = 4096;

    /// <summary>Fewest frames a window needs for the noise test to mean anything.</summary>
    private const int MinNoiseFrames = 256;

    /// <summary>Shortest and longest lag searched for a repeating shape.</summary>
    private const int MinLag = 8;

    /// <summary>Longest lag searched.</summary>
    private const int MaxLag = 512;

    /// <summary>A sample is noise only if it is at least this bright (a slow thump is not noise).</summary>
    private const double NoiseBrightness = 0.3;

    /// <summary>Normalized autocorrelation above which the sample counts as having a repeating shape.</summary>
    private const double NoiseCorrelation = 0.35;

    /// <summary>Measures <paramref name="sample"/>.</summary>
    public static SampleTraits Analyze(Sample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ReadOnlySpan<float> window = sample.Loop is { } loop
            ? sample.Data.AsSpan(loop.Start, Math.Min(loop.Length, WindowFrames))
            : sample.Data.AsSpan(0, Math.Min(sample.Length, WindowFrames));
        if (window.Length < 2)
        {
            return new SampleTraits(0, false);
        }

        double energy = 0;
        double slope = 0;
        for (int i = 0; i < window.Length; i++)
        {
            energy += (double)window[i] * window[i];
            if (i > 0)
            {
                double step = window[i] - window[i - 1];
                slope += step * step;
            }
        }

        if (energy <= 0)
        {
            return new SampleTraits(0, false);
        }

        double rms = Math.Sqrt(energy / window.Length);
        double rmsSlope = Math.Sqrt(slope / (window.Length - 1));
        double brightness = Math.Min(1, rmsSlope / (2 * rms));
        bool noise = window.Length >= MinNoiseFrames && brightness >= NoiseBrightness && PeakCorrelation(window, energy) < NoiseCorrelation;
        return new SampleTraits(brightness, noise);
    }

    /// <summary>The highest normalized autocorrelation over lags <see cref="MinLag"/>–<see cref="MaxLag"/>.</summary>
    private static double PeakCorrelation(ReadOnlySpan<float> window, double energy)
    {
        double peak = 0;
        int maxLag = Math.Min(MaxLag, window.Length / 2);
        for (int lag = MinLag; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (int i = 0; i + lag < window.Length; i++)
            {
                sum += (double)window[i] * window[i + lag];
            }

            peak = Math.Max(peak, sum / energy);
        }

        return peak;
    }
}
