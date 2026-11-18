using Mod2Xgm.Analysis;
using Mod2Xgm.Model;

namespace Mod2Xgm.Mapping;

/// <summary>
/// The pitch a sample sounds at, measured once per sample (docs/adr/0044-dmf-effects-and-song-grid.md): how many frames one
/// cycle of its tone lasts. A MOD note only says how fast the sample is played; what that sounds like depends on the
/// recording (a 32-frame loop sounds five octaves above a 1024-frame one), and an FM channel needs the pitch heard.
/// <para>The repeating part (the loop, else the sound after its first frames) is searched for the shortest lag at which it
/// matches itself, with the YIN difference function (de Cheveigné and Kawahara, 2002; <see cref="Yin"/>). A sample with no clear repeat
/// (noise, a drum) has no pitch. A loop that isn't a tone (<see cref="LoopShape"/>) would match itself only at its own
/// length, a hum that isn't there: a loop of noise has no pitch, and a short tail after a long recording is measured from
/// the recording, as a sample that plays once is (docs/adr/0117-tail-and-noise-loops.md). A one-shot too short for the full window (a chip blip of a few hundred frames) is searched
/// with half of what it has as the window and the other half as the longest cycle.</para>
/// </summary>
/// <param name="PeriodFrames">Frames per cycle, with sub-frame precision, or <c>null</c> when the sample has no clear pitch.</param>
internal readonly record struct SamplePitch(double? PeriodFrames)
{
    /// <summary>Frames the difference function sums over.</summary>
    private const int WindowFrames = 2048;

    /// <summary>The shortest window a short one-shot is searched with: a few cycles of a high tone.</summary>
    private const int MinWindowFrames = 64;

    /// <summary>Longest cycle searched, in frames (8 Hz at ProTracker's C-2 rate).</summary>
    private const int MaxLag = 1024;

    /// <summary>Shortest cycle searched, in frames.</summary>
    private const int MinLag = 2;

    /// <summary>A lag counts as a cycle when its normalized difference is below this (YIN's usual threshold).</summary>
    private const double Threshold = 0.15;

    /// <summary>Frames skipped at the start of a sample without a loop, where an attack is least like the tone that follows.</summary>
    private const int AttackFrames = 256;

    /// <summary>
    /// How far either side of a partial its power is counted, in cents: chorus spreads a partial over a few cents, and a
    /// chord's equal-tempered notes sit up to 16 cents from the harmonics of a just chord (a major third is 14 cents above
    /// 5:4). Partials a semitone apart stay apart.
    /// </summary>
    public const double ChordCents = 25;

    /// <summary>A partial within this of the loudest, in dB, is a note heard in its own right.</summary>
    public const double StrongDb = 20;

    /// <summary>The frequency the sample sounds at when played at <paramref name="playbackRateHz"/>, or <c>null</c> without a pitch.</summary>
    public double? FrequencyAt(double playbackRateHz) => PeriodFrames is { } period ? playbackRateHz / period : null;

    /// <summary>Measures <paramref name="sample"/>.</summary>
    public static SamplePitch Measure(Sample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        LoopKind loop = LoopShape.Of(sample, SampleTraits.Analyze(sample));
        if (loop == LoopKind.Noise)
        {
            return new SamplePitch(null);
        }

        float[]? window = Window(sample, loop);
        if (window is null)
        {
            return new SamplePitch(null);
        }

        // A short one-shot sums over half its frames and searches cycles up to the other half.
        int windowFrames = window.Length >= WindowFrames + (2 * MinLag) ? WindowFrames : window.Length / 2;
        int maxLag = Math.Min(MaxLag, window.Length - windowFrames);
        double[] normalized = Yin.Normalized(window, windowFrames, maxLag);
        if (Yin.FirstDip(normalized, MinLag, Threshold) is { } period)
        {
            return new SamplePitch(period);
        }

        return loop == LoopKind.Tone && BottomNote(sample) is { } bottom ? new SamplePitch(bottom) { Chord = true } : new SamplePitch(null);
    }

    /// <summary>
    /// True when no cycle repeats in the sample's tone loop, so <see cref="PeriodFrames"/> is the cycle of its bottom note
    /// (docs/adr/0125-chord-samples.md): a chord, or a chorused tone.
    /// </summary>
    public bool Chord { get; init; }

    /// <summary>
    /// The pitch of a tone loop that never repeats exactly (docs/adr/0125-chord-samples.md): a chord, or a chorused tone,
    /// has no one cycle, and the MOD's notes were tuned by ear to what it sounds like. Its bottom note, the lowest partial
    /// within <see cref="StrongDb"/> of the loudest, is what one voice standing for it plays: the chord's lowest note, or the
    /// tone itself. <c>null</c> when the loop has no partial.
    /// </summary>
    private static double? BottomNote(Sample sample) =>
        LoopSpectrum.Of(sample)?.BottomNote(ChordCents, StrongDb, 1.0 / MaxLag) is { } cyclesPerFrame ? 1 / cyclesPerFrame : null;

    /// <summary>
    /// How far, in cents, a library FM patch standing for <paramref name="sample"/> plays above its measured pitch
    /// (docs/adr/0125-chord-samples.md): a loop whose cycle is a chord's common cycle (a just major triad is harmonics 4, 5
    /// and 6 of the cycle two octaves below its root) is heard at its lowest strong harmonic, which a patch that sounds its own
    /// fundamental must be moved up to. 0 for a tone, whose fundamental is heard, or when <paramref name="harmonic"/> is the
    /// patch itself and sounds the harmonics at the cycle.
    /// </summary>
    public static double LibraryLiftCents(HarmonicPatch? harmonic) =>
        harmonic is { Patch: null, Lowest: > 1 } ? 1200 * Math.Log2(harmonic.Lowest) : 0;

    /// <summary>
    /// The frames to search: the loop repeated as often as needed (a one-cycle loop is a whole tone), else the sound after
    /// its attack; <c>null</c> when the sample is silent or has fewer than two <see cref="MinWindowFrames"/> to search.
    /// </summary>
    private static float[]? Window(Sample sample, LoopKind kind)
    {
        int needed = WindowFrames + MaxLag;
        float[] window;
        if (kind == LoopKind.Tone && sample.Loop is { } loop)
        {
            window = new float[needed];
            for (int i = 0; i < needed; i++)
            {
                window[i] = sample.Data[loop.Start + (i % loop.Length)];
            }
        }
        else
        {
            int heard = sample.Loop is { } tail ? tail.Start : sample.Length;
            int start = Math.Min(AttackFrames, heard / 4);
            int length = Math.Min(needed, heard - start);
            if (length < 2 * MinWindowFrames)
            {
                return null;
            }

            window = sample.Data.AsSpan(start, length).ToArray();
        }

        return window.Any(v => v != 0) ? window : null;
    }
}
