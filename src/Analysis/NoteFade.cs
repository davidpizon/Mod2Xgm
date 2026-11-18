namespace Mod2Xgm.Analysis;

/// <summary>
/// How a note's volume moves while it sounds, tick by tick, against the volume it starts at: a volume slide, tremolo or a
/// <c>Cxx</c> partway through (plan T5.3d, docs/adr/0120-faded-recordings.md). A recording on XGM or XGM2 can't change
/// its volume while it plays, so the fade is baked into a copy of its own. Two fades are equal when every tick's gain and
/// length are, so notes that fade alike share one copy.
/// </summary>
internal sealed class NoteFade : IEquatable<NoteFade>
{
    /// <summary>A gain that differs from another by less than this is the same: rounding, not an effect.</summary>
    private const double Same = 1e-6;

    /// <summary>How long a step between two ticks' gains is ramped over, so it doesn't click (2 ms).</summary>
    private const double RampSeconds = 0.002;

    private readonly double[] _gains;
    private readonly double[] _seconds;

    private NoteFade(double[] gains, double[] seconds, bool endsSilent)
    {
        _gains = gains;
        _seconds = seconds;
        EndsSilent = endsSilent;
        AudibleSeconds = seconds.Sum();
    }

    /// <summary>Each tick's volume against the first tick's, the first tick's included (1).</summary>
    public IReadOnlyList<double> Gains => _gains;

    /// <summary>True when the note's volume reaches 0 and stays there until it ends: the copy stops there.</summary>
    public bool EndsSilent { get; }

    /// <summary>How long the note sounds before it falls silent for good, in seconds: all of it when it doesn't.</summary>
    public double AudibleSeconds { get; }

    /// <summary>The longest copy a fade that <see cref="EndsSilent"/> needs, in seconds: its audible part and the ramp down.</summary>
    public double StoredSeconds => AudibleSeconds + RampSeconds;

    /// <summary>
    /// The fade of a note that plays at <paramref name="volumes"/> for <paramref name="seconds"/> each tick, or <c>null</c>
    /// when its volume never moves (or it starts silent): a copy at its starting volume plays it as it is.
    /// </summary>
    /// <param name="volumes">The note's volume on each tick it sounds, 0–1, the first tick first.</param>
    /// <param name="seconds">Each of those ticks' length.</param>
    public static NoteFade? Of(IReadOnlyList<double> volumes, IReadOnlyList<double> seconds)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(seconds);
        if (volumes.Count == 0 || volumes.Count != seconds.Count || volumes[0] <= 0)
        {
            return null;
        }

        double[] gains = [.. volumes.Select(v => Math.Round(Math.Max(0, v) / volumes[0], 6))];
        if (gains.All(g => Math.Abs(g - 1) < Same))
        {
            return null;
        }

        int audible = gains.Length;
        while (audible > 1 && gains[audible - 1] < Same)
        {
            audible--;
        }

        return new NoteFade(gains[..audible], [.. seconds.Take(audible).Select(s => Math.Round(s, 9))], audible < gains.Length);
    }

    /// <summary>
    /// <paramref name="data"/>, a recording that starts with the note at <paramref name="rateHz"/> frames a second, with the
    /// fade applied: each tick's gain from its start, ramped over 2 ms from the tick before. Past the last tick the last
    /// gain holds, unless the note <see cref="EndsSilent"/>: then the copy ramps down and stops.
    /// </summary>
    public float[] Apply(float[] data, double rateHz)
    {
        ArgumentNullException.ThrowIfNull(data);
        int length = EndsSilent ? Math.Min(data.Length, (int)Math.Ceiling(StoredSeconds * rateHz)) : data.Length;
        float[] faded = new float[length];
        int ramp = Math.Max(1, (int)Math.Round(RampSeconds * rateHz));
        int tick = 0;
        double tickStart = 0;
        double end = _seconds[0];
        for (int i = 0; i < length; i++)
        {
            double at = i / rateHz;
            while (tick < _gains.Length && at >= end)
            {
                tick++;
                tickStart = end;
                end += tick < _seconds.Length ? _seconds[tick] : double.PositiveInfinity;
            }

            double gain = tick < _gains.Length ? _gains[tick] : EndsSilent ? 0 : _gains[^1];
            double before = tick == 0 ? gain : _gains[Math.Min(tick, _gains.Length) - 1];
            int into = (int)((at - tickStart) * rateHz);
            if (tick > 0 && into < ramp)
            {
                gain = before + ((gain - before) * (into + 1) / ramp);
            }

            faded[i] = (float)(data[i] * gain);
        }

        return faded;
    }

    /// <inheritdoc/>
    public bool Equals(NoteFade? other) =>
        other is not null && EndsSilent == other.EndsSilent && _gains.AsSpan().SequenceEqual(other._gains) && _seconds.AsSpan().SequenceEqual(other._seconds);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as NoteFade);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(EndsSilent);
        foreach (double g in _gains)
        {
            hash.Add(g);
        }

        foreach (double s in _seconds)
        {
            hash.Add(s);
        }

        return hash.ToHashCode();
    }
}
