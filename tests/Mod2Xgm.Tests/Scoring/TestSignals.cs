using Mod2Xgm.Scoring;

namespace Mod2Xgm.Tests.Scoring;

/// <summary>Synthetic audio for the scoring tests: tones, noise and tone bursts at known times.</summary>
internal static class TestSignals
{
    /// <summary>Sample rate used by the scoring tests (low, to keep them fast).</summary>
    public const int Rate = 22050;

    /// <summary>A sine wave.</summary>
    public static AudioSignal Sine(double hertz, double seconds, double amplitude = 0.5, int rate = Rate) =>
        new([.. Enumerable.Range(0, (int)(seconds * rate)).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hertz * i / rate)))], rate);

    /// <summary>Uniform white noise from a fixed seed.</summary>
    public static AudioSignal Noise(double seconds, double amplitude = 0.5, int seed = 1, int rate = Rate)
    {
        Random random = new(seed);
        return new([.. Enumerable.Range(0, (int)(seconds * rate)).Select(_ => (float)(amplitude * ((random.NextDouble() * 2) - 1)))], rate);
    }

    /// <summary>
    /// Short tone bursts (alternating 440 and 880 Hz, with a quick fade out) that start at the given times, in silence.
    /// </summary>
    public static AudioSignal Bursts(IReadOnlyList<double> startSeconds, double totalSeconds, double burstSeconds = 0.12, double delaySeconds = 0, int rate = Rate)
    {
        float[] samples = new float[(int)(totalSeconds * rate)];
        for (int n = 0; n < startSeconds.Count; n++)
        {
            double hertz = n % 2 == 0 ? 440 : 880;
            int start = (int)((startSeconds[n] + delaySeconds) * rate);
            int length = (int)(burstSeconds * rate);
            for (int i = 0; i < length && start + i < samples.Length; i++)
            {
                double fade = 1 - ((double)i / length);
                samples[start + i] = (float)(0.6 * fade * Math.Sin(2 * Math.PI * hertz * i / rate));
            }
        }

        return new AudioSignal(samples, rate);
    }

    /// <summary>The signal scaled by <paramref name="gain"/>.</summary>
    public static AudioSignal Scaled(AudioSignal signal, double gain) =>
        new([.. signal.Samples.ToArray().Select(s => (float)(s * gain))], signal.SampleRate);
}
