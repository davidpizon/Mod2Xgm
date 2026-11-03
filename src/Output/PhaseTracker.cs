namespace Mod2Xgm.Output;

/// <summary>Counts a phase's work (docs/cli-output.md#phases); stages take one so the run can show how far they are.</summary>
internal interface IWorkCounter
{
    /// <summary>Sets how much work the phase has, once it is known.</summary>
    void SetTotal(long total);

    /// <summary>Counts <paramref name="amount"/> more work done.</summary>
    void Advance(long amount = 1);
}

/// <summary>A counter that counts nothing: the default for stages run outside a conversion (tests, the harness).</summary>
internal sealed class NoWork : IWorkCounter
{
    /// <summary>The one instance.</summary>
    public static NoWork Instance { get; } = new();

    /// <inheritdoc/>
    public void SetTotal(long total)
    {
    }

    /// <inheritdoc/>
    public void Advance(long amount = 1)
    {
    }
}

/// <summary>A running phase: counts its work and is ended by <see cref="Done"/>.</summary>
internal interface IPhase : IWorkCounter
{
    /// <summary>The phase finished: its time is recorded and the next phase may start.</summary>
    void Done();
}

/// <summary>The steps of one run, as the conversion goes through them (docs/cli-output.md#phases).</summary>
internal interface IConversionProgress
{
    /// <summary>The run's input and phases are known.</summary>
    void Begin(RunInfo run);

    /// <summary>
    /// The song's size in <paramref name="unit"/> (<see cref="SongSize.Rows"/>, <see cref="SongSize.Ticks"/>,
    /// <see cref="SongSize.Samples"/>), once known: the time estimates of the phases not started yet scale with it.
    /// </summary>
    void SetSongSize(string unit, long amount);

    /// <summary>Starts phase <paramref name="id"/> with <paramref name="total"/> work (0 when not known yet).</summary>
    IPhase Start(string id, long total = 0);
}

/// <summary>Progress that goes nowhere: the default for <see cref="ConversionCommand"/> outside the command line.</summary>
internal sealed class NoProgress : IConversionProgress
{
    /// <summary>The one instance.</summary>
    public static NoProgress Instance { get; } = new();

    /// <inheritdoc/>
    public void Begin(RunInfo run)
    {
    }

    /// <inheritdoc/>
    public void SetSongSize(string unit, long amount)
    {
    }

    /// <inheritdoc/>
    public IPhase Start(string id, long total = 0) => Phase.Shared;

    /// <summary>A phase that counts nothing.</summary>
    private sealed class Phase : IPhase
    {
        public static Phase Shared { get; } = new();

        public void SetTotal(long total)
        {
        }

        public void Advance(long amount = 1)
        {
        }

        public void Done()
        {
        }
    }
}

/// <summary>
/// Times every phase of a run with <see cref="TimeProvider"/>, asks <see cref="EtaEstimator"/> how long is left and
/// tells the reporter. Progress is passed on at most every <see cref="UpdateInterval"/>, so counting per tick costs little.
/// Phases may run at once (the harness's songs, <see cref="RunInfo.Jobs"/>): the run's time left is what the running
/// phases have left plus the phases not started yet, shared out over the jobs.
/// </summary>
internal sealed class PhaseTracker(IConversionReporter reporter, TimeProvider time, EtaEstimator estimator) : IConversionProgress
{
    /// <summary>Least time between two progress updates passed to the reporter.</summary>
    public static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(100);

    private readonly IConversionReporter _reporter = reporter;
    private readonly TimeProvider _time = time;
    private readonly EtaEstimator _estimator = estimator;
    private readonly long _started = time.GetTimestamp();
    private readonly Lock _lock = new();
    private readonly List<PhaseTiming> _timings = [];
    private readonly HashSet<string> _startedIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimeSpan?> _running = new(StringComparer.Ordinal);
    private IReadOnlyList<PhaseInfo> _phases = [];
    private int _jobs = 1;

    /// <summary>Every finished phase's time, in order.</summary>
    public IReadOnlyList<PhaseTiming> Timings
    {
        get
        {
            lock (_lock)
            {
                return [.. _timings];
            }
        }
    }

    /// <summary>The run's time estimates; the command line gives it the timing history once the options are read.</summary>
    public EtaEstimator Estimator => _estimator;

    /// <inheritdoc/>
    public void Begin(RunInfo run)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_lock)
        {
            _phases = run.Phases;
            _jobs = Math.Max(1, run.Jobs);
            _reporter.RunStarted(run);
        }
    }

    /// <inheritdoc/>
    public void SetSongSize(string unit, long amount)
    {
        lock (_lock)
        {
            _estimator.SetSongSize(unit, amount);
        }
    }

    /// <inheritdoc/>
    public IPhase Start(string id, long total = 0)
    {
        lock (_lock)
        {
            PhaseInfo phase = _phases.FirstOrDefault(p => p.Id == id)
                ?? throw new InvalidOperationException($"Phase {id} isn't in this run's plan.");
            _startedIds.Add(id);
            Running running = new(this, phase, total);
            running.Report(PhaseEvent.Start);
            return running;
        }
    }

    private TimeSpan RunElapsed => _time.GetElapsedTime(_started);

    /// <summary>
    /// The run's time left once <paramref name="phase"/> has <paramref name="remaining"/> left: every running phase's time
    /// left plus the phases still to start, divided by the jobs; partial when some of them have no guess.
    /// </summary>
    private (TimeSpan? Remaining, bool Partial) RunRemaining(PhaseInfo phase, TimeSpan? remaining)
    {
        _running[phase.Id] = remaining;
        TimeSpan? current = null;
        bool unknown = false;
        foreach (TimeSpan? left in _running.Values)
        {
            if (left is { } l)
            {
                current = (current ?? TimeSpan.Zero) + l;
            }
            else
            {
                unknown = true;
            }
        }

        // One job runs its phases in order, so one skipped before this phase won't run; with more, any may still start.
        IEnumerable<PhaseInfo> waiting = _phases.Where(p => !_startedIds.Contains(p.Id) && (_jobs > 1 || p.Number > phase.Number));
        (TimeSpan? total, bool partial) = _estimator.RunRemaining(current, waiting);
        return (total / _jobs, partial || unknown);
    }

    /// <summary>One running phase.</summary>
    private sealed class Running(PhaseTracker tracker, PhaseInfo phase, long total) : IPhase
    {
        private readonly long _started = tracker._time.GetTimestamp();
        private long _total = total;
        private long _done;
        private long _lastReport = tracker._time.GetTimestamp();
        private bool _finished;

        public void SetTotal(long total) => _total = total;

        public void Advance(long amount = 1)
        {
            _done += amount;
            long now = tracker._time.GetTimestamp();
            if (tracker._time.GetElapsedTime(_lastReport, now) >= UpdateInterval)
            {
                _lastReport = now;
                lock (tracker._lock)
                {
                    Report(PhaseEvent.Progress);
                }
            }
        }

        public void Done()
        {
            lock (tracker._lock)
            {
                if (_finished)
                {
                    return;
                }

                _finished = true;
                TimeSpan elapsed = tracker._time.GetElapsedTime(_started);
                tracker._timings.Add(new PhaseTiming(phase, elapsed, _done));
                tracker._estimator.Finished(phase, elapsed);
                Report(PhaseEvent.End);
                tracker._running.Remove(phase.Id);
            }
        }

        /// <summary>Tells the reporter; the caller holds the tracker's lock.</summary>
        public void Report(PhaseEvent phaseEvent)
        {
            TimeSpan elapsed = tracker._time.GetElapsedTime(_started);
            TimeSpan? remaining = phaseEvent == PhaseEvent.End ? TimeSpan.Zero : tracker._estimator.Remaining(phase, elapsed, _done, _total);
            (TimeSpan? total, bool partial) = tracker.RunRemaining(phase, remaining);
            tracker._reporter.Phase(new PhaseUpdate(
                phase,
                tracker._phases.Count,
                phaseEvent,
                _done,
                _total,
                elapsed,
                remaining,
                tracker.RunElapsed,
                total,
                partial));
        }
    }
}
