using Mod2Xgm.Scoring;
using R128Net;

namespace Mod2Xgm.Harness;

/// <summary>
/// The EBU R128 loudness of a rendering (ITU-R BS.1770, measured by R128Net, a port of libebur128; ADR 0053): what a
/// loudness meter would show, gated so silences and fades don't pull it down.
/// </summary>
/// <param name="IntegratedLufs">Gated integrated loudness, in LUFS; <c>null</c> when the audio is silent or below the −70 LUFS gate.</param>
/// <param name="RangeLu">Loudness range (LRA): how far the loud parts are from the quiet ones, in LU.</param>
/// <param name="TruePeakDbtp">The higher of the two channels' true peaks (4× oversampled), in dBTP; above 0 clips on a DAC.</param>
internal sealed record Loudness(double? IntegratedLufs, double RangeLu, double? TruePeakDbtp)
{
    /// <summary>Frames handed to the meter at a time.</summary>
    private const int ChunkFrames = 4096;

    /// <summary>Measures stereo <paramref name="audio"/>.</summary>
    public static Loudness Of(StereoAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        using LoudnessMeter meter = new(2, audio.SampleRate, LoudnessModes.Integrated | LoudnessModes.LoudnessRange | LoudnessModes.TruePeak);
        float[] interleaved = new float[ChunkFrames * 2];
        for (int start = 0; start < audio.Length; start += ChunkFrames)
        {
            int frames = Math.Min(ChunkFrames, audio.Length - start);
            for (int i = 0; i < frames; i++)
            {
                interleaved[2 * i] = audio.Left[start + i];
                interleaved[(2 * i) + 1] = audio.Right[start + i];
            }

            meter.AddFrames(interleaved.AsSpan(0, frames * 2));
        }

        double peak = Math.Max(meter.GetTruePeak(0), meter.GetTruePeak(1));
        return new Loudness(Finite(meter.IntegratedLoudness), meter.LoudnessRange, peak > 0 ? 20 * Math.Log10(peak) : null);
    }

    /// <summary>
    /// The gated integrated loudness of mono <paramref name="signal"/> (one channel, weight 1), in LUFS; <c>null</c> when
    /// it is silent or below the gate. Used to compare stems, where only differences matter.
    /// </summary>
    public static double? GatedLufs(AudioSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        using LoudnessMeter meter = new(1, signal.SampleRate, LoudnessModes.Integrated);
        meter.AddFrames(signal.Samples);
        return Finite(meter.IntegratedLoudness);
    }

    /// <summary>The value, or <c>null</c> for the meter's −∞ (silence).</summary>
    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
}

/// <summary>A rendering's loudness next to the reference's.</summary>
/// <param name="Reference">The original, rendered by the reference player.</param>
/// <param name="Candidate">The conversion's rendering.</param>
internal sealed record LoudnessComparison(Loudness Reference, Loudness Candidate)
{
    /// <summary>How much louder the conversion is, in LU (negative = quieter); <c>null</c> when either is silent.</summary>
    public double? DifferenceLu => Candidate.IntegratedLufs - Reference.IntegratedLufs;
}
