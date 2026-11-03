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

    /// <summary>Every finished phase's time, in order.</summary>
    IReadOnlyList<PhaseTiming> Timings { get; }
}

/// <summary>Progress that goes nowhere: the default for <see cref="ConversionCommand"/> outside the command line.</summary>
internal sealed class NoProgress : IConversionProgress
{
    /// <summary>The one instance.</summary>
    public static NoProgress Instance { get; } = new();

    /// <inheritdoc/>
    public IReadOnlyList<PhaseTiming> Timings => [];

    /// <inheritdoc/>
    public void Begin(RunInfo run)
    {
    }

    /// <inheritdoc/>
    public void SetSongSize(string unit, long amount)
    {
    }

    /// <inheritdoc/>
    public IPhase Start(string id, long total = 0) => Phase.Instance;

    /// <summary>A phase that counts nothing.</summary>
    private sealed class Phase : IPhase
    {
        public static Phase Instance { get; } = new();

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
/// </summary>
internal sealed class PhaseTracker(IConversionReporter reporter, TimeProvider time, EtaEstimator estimator) : IConversionProgress
{
    /// <summary>Least time between two progress updates passed to the reporter.</summary>
    public static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(100);

    private readonly IConversionReporter _reporter = reporter;
    private readonly TimeProvider _time = time;
    private readonly EtaEstimator _estimator = estimator;
    private readonly long _started = time.GetTimestamp();
    private readonly List<PhaseTiming> _timings = [];
    private IReadOnlyList<PhaseInfo> _phases = [];

    /// <inheritdoc/>
    public IReadOnlyList<PhaseTiming> Timings => _timings;

    /// <summary>The run's time estimates; the command line gives it the timing history once the options are read.</summary>
    public EtaEstimator Estimator => _estimator;

    /// <inheritdoc/>
    public void Begin(RunInfo run)
    {
        ArgumentNullException.ThrowIfNull(run);
        _phases = run.Phases;
        _reporter.RunStarted(run);
    }

    /// <inheritdoc/>
    public void SetSongSize(string unit, long amount) => _estimator.SetSongSize(unit, amount);

    /// <inheritdoc/>
    public IPhase Start(string id, long total = 0)
    {
        PhaseInfo phase = _phases.FirstOrDefault(p => p.Id == id)
            ?? throw new InvalidOperationException($"Phase {id} isn't in this run's plan.");
        Running running = new(this, phase, total);
        running.Report(PhaseEvent.Start);
        return running;
    }

    private TimeSpan RunElapsed => _time.GetElapsedTime(_started);

    /// <summary>The phases after <paramref name="phase"/>.</summary>
    private IEnumerable<PhaseInfo> After(PhaseInfo phase) => _phases.Where(p => p.Number > phase.Number);

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
                Report(PhaseEvent.Progress);
            }
        }

        public void Done()
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
        }

        public void Report(PhaseEvent phaseEvent)
        {
            TimeSpan elapsed = tracker._time.GetElapsedTime(_started);
            TimeSpan? remaining = phaseEvent == PhaseEvent.End ? TimeSpan.Zero : tracker._estimator.Remaining(phase, elapsed, _done, _total);
            (TimeSpan? total, bool partial) = tracker._estimator.RunRemaining(remaining, tracker.After(phase));
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
