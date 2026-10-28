namespace Mod2Xgm.Scoring;

/// <summary>
/// A mono block of audio the scorer compares (docs/testing.md#accuracy-harness): samples in −1…+1 at one sample rate.
/// Stereo renders are mixed down first (<see cref="FromStereo"/>), since the score judges what the song sounds like,
/// not where it sits in the stereo field.
/// </summary>
internal sealed class AudioSignal
{
    private readonly float[] _samples;

    /// <summary>Wraps <paramref name="samples"/> (not copied; don't change them afterwards).</summary>
    /// <param name="samples">Samples in −1…+1.</param>
    /// <param name="sampleRate">Samples per second, above 0.</param>
    public AudioSignal(float[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        _samples = samples;
        SampleRate = sampleRate;
    }

    /// <summary>Samples per second.</summary>
    public int SampleRate { get; }

    /// <summary>The samples.</summary>
    public ReadOnlySpan<float> Samples => _samples;

    /// <summary>Number of samples.</summary>
    public int Length => _samples.Length;

    /// <summary>Length in seconds.</summary>
    public double Seconds => (double)_samples.Length / SampleRate;

    /// <summary>The average of two channels.</summary>
    /// <param name="left">Left channel.</param>
    /// <param name="right">Right channel; the same length as <paramref name="left"/>.</param>
    /// <param name="sampleRate">Samples per second.</param>
    public static AudioSignal FromStereo(float[] left, float[] right, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Length != right.Length)
        {
            throw new ArgumentException("The two channels must have the same length.", nameof(right));
        }

        float[] mixed = new float[left.Length];
        for (int i = 0; i < mixed.Length; i++)
        {
            mixed[i] = (left[i] + right[i]) * 0.5f;
        }

        return new AudioSignal(mixed, sampleRate);
    }

    /// <summary>
    /// This signal moved in time: a positive <paramref name="samples"/> delays it (silence in front), a negative one
    /// advances it (the start is cut off). The length changes by the same amount.
    /// </summary>
    /// <param name="samples">How many samples to move by.</param>
    public AudioSignal Shifted(int samples)
    {
        if (samples == 0)
        {
            return this;
        }

        if (samples > 0)
        {
            float[] delayed = new float[_samples.Length + samples];
            _samples.CopyTo(delayed, samples);
            return new AudioSignal(delayed, SampleRate);
        }

        int cut = Math.Min(-samples, _samples.Length);
        return new AudioSignal(_samples[cut..], SampleRate);
    }
}
