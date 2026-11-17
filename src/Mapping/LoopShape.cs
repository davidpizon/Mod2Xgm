using Mod2Xgm.Model;

namespace Mod2Xgm.Mapping;

/// <summary>What a sample's loop is (docs/adr/0117-tail-and-noise-loops.md).</summary>
internal enum LoopKind
{
    /// <summary>The sample doesn't loop.</summary>
    None,

    /// <summary>The loop repeats a tone: the sample is a sustained note.</summary>
    Tone,

    /// <summary>The loop is noise: repeating it matches only at its own length, which is no pitch.</summary>
    Noise,

    /// <summary>The loop is a short tail after a long recording (a bell hit ending in a hiss): the recording is what is heard.</summary>
    Tail,
}

/// <summary>
/// Tells a loop that holds a tone from one that doesn't (plan T5.3b, docs/adr/0117-tail-and-noise-loops.md). The simple
/// mapper plays a tone loop by FM and the others like a sample that plays once; <see cref="SamplePitch"/> measures a tone
/// loop's pitch from the loop and the others' from the recording.
/// </summary>
internal static class LoopShape
{
    /// <summary>A loop is a tail when the recording before it is at least this many times longer than the loop…</summary>
    private const int TailRatio = 4;

    /// <summary>…and at least this many frames long.</summary>
    private const int TailLeadFrames = 2048;

    /// <summary>What <paramref name="sample"/>'s loop is, from its <paramref name="traits"/> and where the loop lies.</summary>
    public static LoopKind Of(Sample sample, SampleTraits traits)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.Loop is not { } loop)
        {
            return LoopKind.None;
        }

        if (traits.NoiseLike)
        {
            return LoopKind.Noise;
        }

        return loop.Start >= TailLeadFrames && loop.Start >= TailRatio * loop.Length ? LoopKind.Tail : LoopKind.Tone;
    }
}
