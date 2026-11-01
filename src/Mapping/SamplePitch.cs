using Mod2Xgm.Analysis;
using Mod2Xgm.Model;

namespace Mod2Xgm.Mapping;

/// <summary>
/// The pitch a sample sounds at, measured once per sample (docs/adr/0044-dmf-effects-and-song-grid.md): how many frames one
/// cycle of its tone lasts. A MOD note only says how fast the sample is played; what that sounds like depends on the
/// recording (a 32-frame loop sounds five octaves above a 1024-frame one), and an FM channel needs the pitch heard.
/// <para>The repeating part (the loop, else the sound after its first frames) is searched for the shortest lag at which it
/// matches itself, with the YIN difference function (de Cheveigné and Kawahara, 2002; <see cref="Yin"/>). A sample with no clear repeat
/// (noise, a drum) has no pitch.</para>
/// </summary>
/// <param name="PeriodFrames">Frames per cycle, with sub-frame precision, or <c>null</c> when the sample has no clear pitch.</param>
internal readonly record struct SamplePitch(double? PeriodFrames)
{
    /// <summary>Frames the difference function sums over.</summary>
    private const int WindowFrames = 2048;

    /// <summary>Longest cycle searched, in frames (8 Hz at ProTracker's C-2 rate).</summary>
    private const int MaxLag = 1024;

    /// <summary>Shortest cycle searched, in frames.</summary>
    private const int MinLag = 2;

    /// <summary>A lag counts as a cycle when its normalized difference is below this (YIN's usual threshold).</summary>
    private const double Threshold = 0.15;

    /// <summary>Frames skipped at the start of a sample without a loop, where an attack is least like the tone that follows.</summary>
    private const int AttackFrames = 256;

    /// <summary>The frequency the sample sounds at when played at <paramref name="playbackRateHz"/>, or <c>null</c> without a pitch.</summary>
    public double? FrequencyAt(double playbackRateHz) => PeriodFrames is { } period ? playbackRateHz / period : null;

    /// <summary>Measures <paramref name="sample"/>.</summary>
    public static SamplePitch Measure(Sample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        float[]? window = Window(sample);
        if (window is null)
        {
            return new SamplePitch(null);
        }

        int maxLag = Math.Min(MaxLag, window.Length - WindowFrames);
        double[] normalized = Yin.Normalized(window, WindowFrames, maxLag);
        return new SamplePitch(Yin.FirstDip(normalized, MinLag, Threshold));
    }

    /// <summary>
    /// The frames to search: the loop repeated as often as needed (a one-cycle loop is a whole tone), else the sound after
    /// its attack; <c>null</c> when the sample is too short or silent.
    /// </summary>
    private static float[]? Window(Sample sample)
    {
        int needed = WindowFrames + MaxLag;
        float[] window;
        if (sample.Loop is { } loop)
        {
            window = new float[needed];
            for (int i = 0; i < needed; i++)
            {
                window[i] = sample.Data[loop.Start + (i % loop.Length)];
            }
        }
        else
        {
            int start = Math.Min(AttackFrames, sample.Length / 4);
            int length = Math.Min(needed, sample.Length - start);
            if (length < WindowFrames + (2 * MinLag))
            {
                return null;
            }

            window = sample.Data.AsSpan(start, length).ToArray();
        }

        return window.Any(v => v != 0) ? window : null;
    }
}
