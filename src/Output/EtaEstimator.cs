namespace Mod2Xgm.Output;

/// <summary>
/// Guesses how long a run has left (docs/cli-output.md#time-estimate). The running phase is judged by its own work once
/// it has done <see cref="MinCountedShare"/> of it for <see cref="MinCountedTime"/>; the guess is smoothed so it doesn't
/// jump. Phases not started yet have no guess until earlier runs' timings are kept (plan T17.6).
/// </summary>
internal sealed class EtaEstimator
{
    /// <summary>Share of its work a phase has done before its own pace is trusted.</summary>
    private const double MinCountedShare = 0.02;

    /// <summary>Time a phase has run before its own pace is trusted.</summary>
    private static readonly TimeSpan MinCountedTime = TimeSpan.FromSeconds(0.5);

    /// <summary>Weight of a new guess in the smoothed guess of the running phase.</summary>
    public const double Smoothing = 0.3;

    private string? _phase;
    private double? _smoothedSeconds;

    /// <summary>The song's size in rows played, once known; 0 before.</summary>
    public long SongRows { get; set; }

    /// <summary>Time left in <paramref name="phase"/> after <paramref name="elapsed"/> with <paramref name="done"/> of <paramref name="total"/> work done; <c>null</c> when unknown.</summary>
    public TimeSpan? Remaining(PhaseInfo phase, TimeSpan elapsed, long done, long total)
    {
        ArgumentNullException.ThrowIfNull(phase);
        if (_phase != phase.Id)
        {
            _phase = phase.Id;
            _smoothedSeconds = null;
        }

        if (total > 0 && done > 0 && (double)done / total >= MinCountedShare && elapsed >= MinCountedTime)
        {
            double guess = elapsed.TotalSeconds * (total - Math.Min(done, total)) / done;
            _smoothedSeconds = _smoothedSeconds is { } s ? s + (Smoothing * (guess - s)) : guess;
        }

        return _smoothedSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
    }

    /// <summary>
    /// Time left in the run: <paramref name="current"/> when no phase in <paramref name="later"/> is left, else a lower
    /// bound (partial); <c>null</c> when the current phase has no guess either.
    /// </summary>
    public static (TimeSpan? Remaining, bool Partial) RunRemaining(TimeSpan? current, IEnumerable<PhaseInfo> later)
    {
        ArgumentNullException.ThrowIfNull(later);
        return current is null ? (null, true) : (current, later.Any());
    }

    /// <summary><paramref name="phase"/> finished after <paramref name="elapsed"/>.</summary>
    public void Finished(PhaseInfo phase, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(phase);
        _phase = null;
        _smoothedSeconds = null;
    }
}
