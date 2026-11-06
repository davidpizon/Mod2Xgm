using Mod2Xgm.Output;
using Moq;

namespace Mod2Xgm.Tests.Output;

/// <summary>Tests of <see cref="PhaseTracker"/>: steps, work, throttling and timings (plan T17.5, docs/cli-output.md#phases).</summary>
public sealed class PhaseTrackerTests
{
    private static readonly PhaseInfo Read = new(1, "read", "Read MOD", "bytes");
    private static readonly PhaseInfo Render = new(2, "render", "Render WAV", "ticks");

    private readonly FakeClock _clock = new();
    private readonly Mock<IConversionReporter> _reporter = new();
    private readonly List<PhaseUpdate> _updates = [];
    private readonly PhaseTracker _sut;

    /// <summary>A tracker on a hand-moved clock whose reporter keeps every phase update.</summary>
    public PhaseTrackerTests()
    {
        _reporter.Setup(r => r.Phase(It.IsAny<PhaseUpdate>())).Callback<PhaseUpdate>(_updates.Add);
        _sut = new PhaseTracker(_reporter.Object, _clock, new EtaEstimator());
    }

    /// <summary>Beginning a run passes the run, with its phase list, to the reporter once.</summary>
    [Fact]
    public void Begin_WithRun_TellsReporterOnce()
    {
        // Arrange
        RunInfo run = new("song.mod", "dmf", "normal", [Read, Render]);

        // Act
        _sut.Begin(run);

        // Assert
        _reporter.Verify(r => r.RunStarted(run), Times.Once);
    }

    /// <summary>Every phase reports a start and an end, each saying how many phases the run has.</summary>
    [Fact]
    public void Start_ThenDone_ReportsStartAndEndOfEachPhase()
    {
        // Arrange
        _sut.Begin(new RunInfo("song.mod", "dmf", "normal", [Read, Render]));

        // Act
        _sut.Start("read", 100).Done();
        _sut.Start("render").Done();

        // Assert
        Assert.Equal(["read Start", "read End", "render Start", "render End"], _updates.Select(u => $"{u.Phase.Id} {u.Event}"));
        Assert.All(_updates, u => Assert.Equal(2, u.Of));
    }

    /// <summary>A phase's end carries its time, its work done and nothing left.</summary>
    [Fact]
    public void Done_AfterWorkAndTime_ReportsElapsedDoneAndNothingRemaining()
    {
        // Arrange
        _sut.Begin(new RunInfo("song.mod", "dmf", "normal", [Read, Render]));
        IPhase read = _sut.Start("read", 100);
        read.Advance(100);
        _clock.Advance(TimeSpan.FromSeconds(0.25));

        // Act
        read.Done();

        // Assert
        PhaseUpdate end = _updates[^1];
        Assert.Equal(PhaseEvent.End, end.Event);
        Assert.Equal(TimeSpan.FromSeconds(0.25), end.Elapsed);
        Assert.Equal(100, end.Done);
        Assert.Equal(TimeSpan.Zero, end.Remaining);
    }

    /// <summary>Ending a phase twice reports and times it once.</summary>
    [Fact]
    public void Done_CalledTwice_EndsPhaseOnce()
    {
        // Arrange
        _sut.Begin(new RunInfo("song.mod", "dmf", "normal", [Read, Render]));
        IPhase read = _sut.Start("read", 100);

        // Act
        read.Done();
        read.Done();

        // Assert
        Assert.Single(_updates, u => u.Event == PhaseEvent.End);
        Assert.Single(_sut.Timings);
    }

    /// <summary>The run's timings list every finished phase, in order, with its time.</summary>
    [Fact]
    public void Timings_AfterTwoPhases_ListsThemInOrderWithTimes()
    {
        // Arrange
        _sut.Begin(new RunInfo("song.mod", "dmf", "normal", [Read, Render]));
        IPhase read = _sut.Start("read");
        _clock.Advance(TimeSpan.FromSeconds(0.25));
        read.Done();
        IPhase render = _sut.Start("render");
        _clock.Advance(TimeSpan.FromSeconds(1));
        render.Done();

        // Act
        IReadOnlyList<PhaseTiming> timings = _sut.Timings;

        // Assert
        Assert.Equal(["read", "render"], timings.Select(t => t.Phase.Id));
        Assert.Equal(TimeSpan.FromSeconds(0.25), timings[0].Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(1), timings[1].Elapsed);
    }

    /// <summary>Work is passed on at most every <see cref="PhaseTracker.UpdateInterval"/>, however often it is counted.</summary>
    [Fact]
    public void Advance_EveryMillisecond_ReportsAtMostEveryUpdateInterval()
    {
        // Arrange
        _sut.Begin(new RunInfo("song.mod", "dmf", "normal", [Read, Render]));
        IPhase render = _sut.Start("render", 1000);

        // Act
        AdvanceEachMillisecond(render, 1000);

        // Assert
        PhaseUpdate[] progress = [.. _updates.Where(u => u.Event == PhaseEvent.Progress)];
        Assert.InRange(progress.Length, 9, 10);
        Assert.Equal(1000, progress[^1].Total);
        Assert.NotNull(progress[^1].Remaining);
        Assert.False(progress[^1].TotalPartial);
    }

    /// <summary>
    /// Phases may run at once (the harness's songs): while one of two running phases has no guess, the run's time left
    /// is at least the other's, since the run can't end before it does, and marked partial.
    /// </summary>
    [Fact]
    public void Advance_WithParallelPhaseUnknown_ReportsKnownPhasePartialTimeLeft()
    {
        // Arrange
        PhaseInfo third = new(3, "c", "C", "ticks");
        _sut.Begin(new RunInfo("corpus", RunInfo.HarnessFormat, "normal", [Read, Render, third], Jobs: 2));
        IPhase read = _sut.Start("read", 100);
        _sut.Start("render", 100);
        _clock.Advance(TimeSpan.FromSeconds(1));

        // Act
        read.Advance(50);

        // Assert
        PhaseUpdate update = _updates[^1];
        Assert.Equal(TimeSpan.FromSeconds(1), update.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(1), update.TotalRemaining);
        Assert.True(update.TotalPartial);
    }

    /// <summary>Once both running phases have a guess, the run's time left is the longest of them, still partial for the phase not started.</summary>
    [Fact]
    public void Advance_WithBothParallelPhasesKnown_ReportsLongestTimeLeft()
    {
        // Arrange
        PhaseInfo third = new(3, "c", "C", "ticks");
        _sut.Begin(new RunInfo("corpus", RunInfo.HarnessFormat, "normal", [Read, Render, third], Jobs: 2));
        IPhase read = _sut.Start("read", 100);
        IPhase render = _sut.Start("render", 100);
        _clock.Advance(TimeSpan.FromSeconds(1));
        read.Advance(50);

        // Act
        render.Advance(50);

        // Assert
        PhaseUpdate update = _updates[^1];
        Assert.Equal(TimeSpan.FromSeconds(1), update.TotalRemaining);
        Assert.True(update.TotalPartial);
    }

    /// <summary>A phase that isn't in the run's plan is a bug.</summary>
    [Fact]
    public void Start_WithPhaseNotInPlan_ThrowsInvalidOperation()
    {
        // Arrange
        _sut.Begin(new RunInfo("song.mod", "dmf", "normal", [Read]));

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _sut.Start("render"));
    }

    /// <summary>Progress outside a run takes every call without failing.</summary>
    [Fact]
    public void NoProgress_WhenUsedLikeTracker_AcceptsEveryCall()
    {
        // Arrange
        NoProgress sut = NoProgress.Instance;

        // Act
        Exception? error = Record.Exception(() =>
        {
            sut.Begin(new RunInfo("song.mod", "dmf", "normal", [Read]));
            sut.SetSongSize(SongSize.Rows, 64);
            IPhase phase = sut.Start("anything", 5);
            phase.SetTotal(10);
            phase.Advance(3);
            phase.Done();
        });

        // Assert
        Assert.Null(error);
    }

    /// <summary>The counter that counts nothing takes every call without failing.</summary>
    [Fact]
    public void NoWork_WhenCounted_AcceptsEveryCall()
    {
        // Arrange
        NoWork sut = NoWork.Instance;

        // Act
        Exception? error = Record.Exception(() =>
        {
            sut.SetTotal(1);
            sut.Advance();
        });

        // Assert
        Assert.Null(error);
    }

    /// <summary>Advances <paramref name="phase"/> by one, <paramref name="count"/> times, a millisecond apart.</summary>
    private void AdvanceEachMillisecond(IPhase phase, int count)
    {
        for (int tick = 0; tick < count; tick++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(1));
            phase.Advance();
        }
    }

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class FakeClock : TimeProvider
    {
        private long _ticks;

        /// <inheritdoc/>
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <inheritdoc/>
        public override long GetTimestamp() => _ticks;

        /// <summary>Moves the clock forward by <paramref name="time"/>.</summary>
        public void Advance(TimeSpan time) => _ticks += time.Ticks;
    }
}
