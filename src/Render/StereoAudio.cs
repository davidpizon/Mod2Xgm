using Mod2Xgm.Audio;
using Mod2Xgm.Scoring;

namespace Mod2Xgm.Render;

/// <summary>Two channels of audio at one rate, samples in −1…+1. A mono recording has the same array in both.</summary>
/// <param name="Left">Left channel.</param>
/// <param name="Right">Right channel; the same length as <paramref name="Left"/>.</param>
/// <param name="SampleRate">Samples per second.</param>
internal sealed record StereoAudio(float[] Left, float[] Right, int SampleRate)
{
    /// <summary>Samples per channel.</summary>
    public int Length => Left.Length;

    /// <summary>Length in seconds.</summary>
    public double Seconds => (double)Left.Length / SampleRate;

    /// <summary>Both channels mixed to one, which is what the scorer compares (ADR 0042).</summary>
    public AudioSignal ToMono() => AudioSignal.FromStereo(Left, Right, SampleRate);

    /// <summary>The first <paramref name="seconds"/> of the audio, or all of it when it is shorter.</summary>
    public StereoAudio Head(double seconds)
    {
        int length = (int)Math.Min(Length, Math.Round(seconds * SampleRate));
        if (length == Length)
        {
            return this;
        }

        float[] left = Left[..length];
        return new StereoAudio(left, ReferenceEquals(Left, Right) ? left : Right[..length], SampleRate);
    }

    /// <summary>
    /// The same audio at <paramref name="sampleRate"/>, through the converter's own smooth resampler, so renders made
    /// at different rates (DefleMask, Furnace) can be compared with the reference. Returns this when the rate matches.
    /// </summary>
    public StereoAudio At(int sampleRate)
    {
        if (sampleRate == SampleRate)
        {
            return this;
        }

        float[] left = SampleProcessor.Resample(Left, SampleRate, sampleRate, ResamplingMode.Smooth);
        float[] right = ReferenceEquals(Left, Right) ? left : SampleProcessor.Resample(Right, SampleRate, sampleRate, ResamplingMode.Smooth);
        return new StereoAudio(left, right, sampleRate);
    }
}
