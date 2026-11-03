namespace Mod2Xgm.Output;

/// <summary>
/// Guesses how long a run has left (docs/cli-output.md#time-estimate). The running phase is judged by its own work once
/// it has done <see cref="MinCountedShare"/> of it for <see cref="MinCountedTime"/>; before that, and for the phases not
/// started yet, by <see cref="History"/>: how many seconds per unit of song size each phase took on earlier runs, times
/// this song's size in that unit (<see cref="SongSize.Of"/>). The running phase's guess is smoothed so it doesn't jump.
/// </summary>
internal sealed class EtaEstimator
{
    /// <summary>Share of its work a phase has done before its own pace is trusted.</summary>
    public const double MinCountedShare = 0.02;

    /// <summary>Time a phase has run before its own pace is trusted.</summary>
    public static readonly TimeSpan MinCountedTime = TimeSpan.FromSeconds(0.5);

    /// <summary>Weight of a new guess in the smoothed guess of the running phase.</summary>
    public const double Smoothing = 0.3;

    private readonly Dictionary<string, long> _songSize = new(StringComparer.Ordinal);

    // The smoothed guess of each running phase; the harness runs several songs at once.
    private readonly Dictionary<string, double> _smoothedSeconds = new(StringComparer.Ordinal);

    /// <summary>Earlier runs' timings; <c>null</c> with <c>--no-cache</c>, or before the options are read.</summary>
    public TimingHistory? History { get; set; }

    /// <summary>The song's size in <paramref name="unit"/> (a <see cref="SongSize"/> unit) is known.</summary>
    public void SetSongSize(string unit, long amount)
    {
        ArgumentException.ThrowIfNullOrEmpty(unit);
        _songSize[unit] = amount;
    }

    /// <summary>Time left in <paramref name="phase"/> after <paramref name="elapsed"/> with <paramref name="done"/> of <paramref name="total"/> work done; <c>null</c> when unknown.</summary>
    public TimeSpan? Remaining(PhaseInfo phase, TimeSpan elapsed, long done, long total)
    {
        ArgumentNullException.ThrowIfNull(phase);
        double? guess = null;
        if (total > 0 && done > 0 && (double)done / total >= MinCountedShare && elapsed >= MinCountedTime)
        {
            guess = elapsed.TotalSeconds * (total - Math.Min(done, total)) / done;
        }
        else if (Expected(phase) is { } expected)
        {
            guess = Math.Max(0, (expected - elapsed).TotalSeconds);
        }

        bool known = _smoothedSeconds.TryGetValue(phase.Id, out double smoothed);
        if (guess is { } g)
        {
            smoothed = known ? smoothed + (Smoothing * (g - smoothed)) : g;
            _smoothedSeconds[phase.Id] = smoothed;
            known = true;
        }

        return known ? TimeSpan.FromSeconds(smoothed) : null;
    }

    /// <summary>
    /// Time left in the run: <paramref name="current"/> plus the expected time of every phase in <paramref name="later"/>;
    /// partial (a lower bound) when some of them have no guess, <c>null</c> when none has.
    /// </summary>
    public (TimeSpan? Remaining, bool Partial) RunRemaining(TimeSpan? current, IEnumerable<PhaseInfo> later)
    {
        ArgumentNullException.ThrowIfNull(later);
        TimeSpan sum = current ?? TimeSpan.Zero;
        bool known = current is not null;
        bool partial = current is null;
        foreach (PhaseInfo phase in later)
        {
            if (Expected(phase) is { } expected)
            {
                sum += expected;
                known = true;
            }
            else
            {
                partial = true;
            }
        }

        return known ? (sum, partial) : (null, true);
    }

    /// <summary><paramref name="phase"/> finished after <paramref name="elapsed"/>: the history learns its pace.</summary>
    public void Finished(PhaseInfo phase, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(phase);
        _smoothedSeconds.Remove(phase.Id);
        History?.Record(phase.Id, elapsed, Size(phase));
    }

    /// <summary>How long <paramref name="phase"/> is expected to take for this song, from history.</summary>
    private TimeSpan? Expected(PhaseInfo phase) => History?.Expected(phase.Id, Size(phase));

    /// <summary>The song's size in the unit <paramref name="phase"/>'s time scales with; 0 while not known.</summary>
    private long Size(PhaseInfo phase) => _songSize.GetValueOrDefault(SongSize.Of(phase));
}

/// <summary>
/// The measures of a song's size that phase times scale with (docs/cli-output.md#time-estimate): the render with the
/// ticks played, the sample analysis with the samples analyzed, every other phase with the rows played.
/// </summary>
internal static class SongSize
{
    /// <summary>Rows played (after the usage scan).</summary>
    public const string Rows = "rows";

    /// <summary>Ticks played (after the tick player).</summary>
    public const string Ticks = "ticks";

    /// <summary>Samples with sound that aren't ignored (after the song model).</summary>
    public const string Samples = "samples";

    /// <summary>The unit <paramref name="phase"/>'s time scales with: its own work unit when that is a song size, else rows.</summary>
    public static string Of(PhaseInfo phase)
    {
        ArgumentNullException.ThrowIfNull(phase);
        return phase.Unit is Ticks or Samples ? phase.Unit : Rows;
    }
}
