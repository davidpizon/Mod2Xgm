using Mod2Xgm.Cli;
using Mod2Xgm.Output;

namespace Mod2Xgm.Tests.Output;

/// <summary>Tests of <see cref="PhaseTracker"/>: steps, work, throttling and timings (plan T17.5, docs/cli-output.md#phases).</summary>
[Collection(ProgramState.Name)]
public sealed class PhaseTrackerTests
{
    private static readonly PhaseInfo Read = new(1, "read", "Read MOD", "bytes");
    private static readonly PhaseInfo Render = new(2, "render", "Render WAV", "ticks");

    /// <summary>Every phase reports a start and an end with its time; the run's timings list them in order.</summary>
    [Fact]
    public void PhasesStartAndEnd()
    {
        FakeClock clock = new();
        RecordingReporter reporter = new();
        PhaseTracker tracker = new(reporter, clock, new EtaEstimator());

        tracker.Begin(new RunInfo("song.mod", "dmf", "normal", [Read, Render]));
        IPhase read = tracker.Start("read", 100);
        read.Advance(100);
        clock.Advance(TimeSpan.FromSeconds(0.25));
        read.Done();
        read.Done();
        IPhase render = tracker.Start("render");
        clock.Advance(TimeSpan.FromSeconds(1));
        render.Done();

        Assert.Equal(2, reporter.Run?.Phases.Count);
        Assert.Equal(["read start", "read end", "render start", "render end"], reporter.Updates.Select(u => $"{u.Phase.Id} {u.Event.ToString().ToLowerInvariant()}"));
        Assert.All(reporter.Updates, u => Assert.Equal(2, u.Of));
        Assert.Equal(TimeSpan.FromSeconds(0.25), reporter.Updates[1].Elapsed);
        Assert.Equal(100, reporter.Updates[1].Done);
        Assert.Equal(TimeSpan.Zero, reporter.Updates[1].Remaining);
        Assert.Equal(["read", "render"], tracker.Timings.Select(t => t.Phase.Id));
        Assert.Equal(TimeSpan.FromSeconds(1), tracker.Timings[1].Elapsed);
    }

    /// <summary>Work is passed on at most every <see cref="PhaseTracker.UpdateInterval"/>, however often it is counted.</summary>
    [Fact]
    public void ProgressIsThrottled()
    {
        FakeClock clock = new();
        RecordingReporter reporter = new();
        PhaseTracker tracker = new(reporter, clock, new EtaEstimator());
        tracker.Begin(new RunInfo("song.mod", "dmf", "normal", [Read, Render]));
        IPhase render = tracker.Start("render", 1000);

        for (int tick = 0; tick < 1000; tick++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            render.Advance();
        }

        PhaseUpdate[] progress = [.. reporter.Updates.Where(u => u.Event == PhaseEvent.Progress)];
        Assert.InRange(progress.Length, 9, 10);
        Assert.Equal(1000, progress[^1].Total);
        Assert.NotNull(progress[^1].Remaining);
        Assert.False(progress[^1].TotalPartial);
    }

    /// <summary>
    /// Phases may run at once (the harness's songs): the run's time left is what the running ones have left plus the
    /// phases not started yet, shared over the jobs, and partial while one of them has no guess.
    /// </summary>
    [Fact]
    public void ParallelPhasesShareTheTimeLeft()
    {
        PhaseInfo third = new(3, "c", "C", "ticks");
        FakeClock clock = new();
        RecordingReporter reporter = new();
        PhaseTracker tracker = new(reporter, clock, new EtaEstimator());
        tracker.Begin(new RunInfo("corpus", RunInfo.HarnessFormat, "normal", [Read, Render, third], Jobs: 2));
        IPhase read = tracker.Start("read", 100);
        IPhase render = tracker.Start("render", 100);

        clock.Advance(TimeSpan.FromSeconds(1));
        read.Advance(50);
        PhaseUpdate first = reporter.Updates[^1];
        render.Advance(50);
        PhaseUpdate both = reporter.Updates[^1];
        read.Done();
        render.Done();

        Assert.Equal(TimeSpan.FromSeconds(1), first.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(0.5), first.TotalRemaining);
        Assert.True(first.TotalPartial);
        Assert.Equal(TimeSpan.FromSeconds(1), both.TotalRemaining);
        Assert.True(both.TotalPartial);
        Assert.Equal(["read", "render"], tracker.Timings.Select(t => t.Phase.Id));
    }

    /// <summary>A phase that isn't in the run's plan is a bug.</summary>
    [Fact]
    public void UnknownPhaseThrows()
    {
        PhaseTracker tracker = new(new RecordingReporter(), new FakeClock(), new EtaEstimator());
        tracker.Begin(new RunInfo("song.mod", "dmf", "normal", [Read]));

        Assert.Throws<InvalidOperationException>(() => tracker.Start("render"));
    }

    /// <summary>Progress outside a run counts nothing and reports nothing.</summary>
    [Fact]
    public void NoProgressIsSilent()
    {
        NoProgress.Instance.Begin(new RunInfo("song.mod", "dmf", "normal", [Read]));
        NoProgress.Instance.SetSongSize(SongSize.Rows, 64);
        IPhase phase = NoProgress.Instance.Start("anything", 5);
        phase.SetTotal(10);
        phase.Advance(3);
        phase.Done();
        NoWork.Instance.SetTotal(1);
        NoWork.Instance.Advance();

        Assert.Empty(NoProgress.Instance.Timings);
    }

    /// <summary>A whole conversion in text mode names each step of its plan, in order, and ends with their times.</summary>
    [Fact]
    public void AConversionWalksItsPlan()
    {
        (int code, string output, _) = OutputRuns.Convert("text", "--verbose", "--render-wav");

        Assert.Equal((int)ExitCode.Success, code);
        string[] steps = [.. output.Split('\n').Where(l => l.StartsWith("step ", StringComparison.Ordinal) && l.Contains(" start ", StringComparison.Ordinal))];
        Assert.Equal(
            ["check", "read", "scan", "model", "play", "analyze", "map", "write", "render"],
            steps.Select(l => l.Split(' ')[2]));
        Assert.StartsWith("step 1/9 check start", steps[0], StringComparison.Ordinal);
        Assert.Contains("\ntime check=#s read=#s scan=#s model=#s play=#s analyze=#s map=#s write=#s render=#s total=#s", output, StringComparison.Ordinal);
    }

    /// <summary>Each option mix has its own steps, numbered from 1 within the run (docs/cli-output.md#phases).</summary>
    [Theory]
    [InlineData("dmf", false, false, "check read scan model play map write")]
    [InlineData("dmf", true, false, "check read scan model play analyze map write")]
    [InlineData("dmf", false, true, "check read scan model play map write render")]
    [InlineData("xgm2", false, true, "check read scan model play map render")]
    [InlineData("xgm", true, true, "check read scan model play analyze map render")]
    public void EveryOptionMixHasItsSteps(string format, bool analyzes, bool renderWav, string expected)
    {
        ConversionOptions options = new()
        {
            InputPath = "song.mod",
            Format = Enum.Parse<OutputFormat>(format, ignoreCase: true),
            RenderWav = renderWav,
            InfoOnly = true,
        };

        IReadOnlyList<PhaseInfo> phases = ConversionCommand.Phases(options, analyzes);

        Assert.Equal(expected, string.Join(' ', phases.Select(p => p.Id)));
        Assert.Equal(Enumerable.Range(1, phases.Count), phases.Select(p => p.Number));
    }

    /// <summary>The counted steps reach their totals in a whole run: every sample analyzed, every tick rendered.</summary>
    [Fact]
    public void CountersReachTheirTotals()
    {
        (int code, string output, _) = OutputRuns.Convert("json", "--verbose", "--render-wav");

        Assert.Equal((int)ExitCode.Success, code);
        string[] ends = [.. output.Split('\n').Where(l => l.Contains("\"event\":\"end\"", StringComparison.Ordinal))];
        Assert.Equal(9, ends.Length);
        foreach (string end in ends)
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(end);
            System.Text.Json.JsonElement root = document.RootElement;
            Assert.Equal(root.GetProperty("total").GetInt64(), root.GetProperty("done").GetInt64());
        }

        Assert.Contains(ends, e => e.Contains("\"id\":\"render\"", StringComparison.Ordinal) && !e.Contains("\"total\":0", StringComparison.Ordinal));
        Assert.Contains(ends, e => e.Contains("\"id\":\"analyze\"", StringComparison.Ordinal) && e.Contains("\"total\":6", StringComparison.Ordinal));
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

    /// <summary>Keeps what the tracker reports.</summary>
    private sealed class RecordingReporter : IConversionReporter
    {
        /// <summary>The run, once begun.</summary>
        public RunInfo? Run { get; private set; }

        /// <summary>Every phase update, in order.</summary>
        public List<PhaseUpdate> Updates { get; } = [];

        /// <inheritdoc/>
        public void Emit(Serilog.Events.LogEvent logEvent)
        {
        }

        /// <inheritdoc/>
        public void RunStarted(RunInfo run) => Run = run;

        /// <inheritdoc/>
        public void Phase(PhaseUpdate update) => Updates.Add(update);

        /// <inheritdoc/>
        public void Completed(RunOutcome outcome)
        {
        }

        /// <inheritdoc/>
        public void Failed(RunFailure failure)
        {
        }

        /// <inheritdoc/>
        public void Dispose()
        {
        }
    }
}
