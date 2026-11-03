using Mod2Xgm.Output;
using Serilog.Events;

namespace Mod2Xgm.Tests.Output;

/// <summary>Tests of <see cref="EtaEstimator"/> and <see cref="TimingHistory"/> (plan T17.6, docs/cli-output.md#time-estimate).</summary>
[Collection(ProgramState.Name)]
public sealed class TimeEstimateTests
{
    private static readonly PhaseInfo Map = new(6, "map", "Map channels", string.Empty);
    private static readonly PhaseInfo Write = new(7, "write", "Write DMF", string.Empty);
    private static readonly PhaseInfo Render = new(8, "render", "Render WAV", "ticks");

    /// <summary>A phase's own pace counts only after 2 % of its work and half a second; then it is smoothed.</summary>
    [Fact]
    public void TheRunningPhaseIsJudgedByItsOwnPace()
    {
        EtaEstimator estimator = new();

        Assert.Null(estimator.Remaining(Render, TimeSpan.FromSeconds(0.4), 50, 100));
        Assert.Null(estimator.Remaining(Render, TimeSpan.FromSeconds(1), 1, 100));
        Assert.Equal(TimeSpan.FromSeconds(1), estimator.Remaining(Render, TimeSpan.FromSeconds(1), 50, 100));
        TimeSpan? smoothed = estimator.Remaining(Render, TimeSpan.FromSeconds(2), 50, 100);
        Assert.Equal(1 + (EtaEstimator.Smoothing * (2 - 1)), smoothed?.TotalSeconds ?? 0, 6);
    }

    /// <summary>Without history, the run's estimate is the current phase's, partial while phases are left.</summary>
    [Fact]
    public void WithoutHistoryLaterPhasesAreUnknown()
    {
        EtaEstimator estimator = new();

        Assert.Equal((null, true), estimator.RunRemaining(null, [Write]));
        Assert.Equal((TimeSpan.FromSeconds(3), true), estimator.RunRemaining(TimeSpan.FromSeconds(3), [Write]));
        Assert.Equal((TimeSpan.FromSeconds(3), false), estimator.RunRemaining(TimeSpan.FromSeconds(3), []));
    }

    /// <summary>History gives phases not started yet, and the running phase before it has a pace, a guess per unit of song size; a phase whose unit isn't known yet has none.</summary>
    [Fact]
    public void HistoryEstimatesByRows()
    {
        using TempDirectory temp = new();
        CollectingSink sink = new();
        TimingHistory history = TimingHistory.Load(temp.Path, sink.CreateLogger());
        EtaEstimator estimator = new() { History = history };
        estimator.SetSongSize(SongSize.Rows, 100);

        estimator.Finished(Map, TimeSpan.FromSeconds(2));
        estimator.Finished(Write, TimeSpan.FromSeconds(1));
        estimator.SetSongSize(SongSize.Rows, 200);

        Assert.Equal(TimeSpan.FromSeconds(4), history.Expected("map", 200));
        Assert.Equal(TimeSpan.FromSeconds(3), estimator.Remaining(Map, TimeSpan.FromSeconds(1), 0, 0));
        Assert.Equal((TimeSpan.FromSeconds(5), false), estimator.RunRemaining(TimeSpan.FromSeconds(3), [Write]));
        Assert.Equal((TimeSpan.FromSeconds(2), true), estimator.RunRemaining(null, [Write, Render]));
    }

    /// <summary>The history survives a save and a load, and each run moves a phase's average halfway to its new pace.</summary>
    [Fact]
    public void HistoryRoundTrips()
    {
        using TempDirectory temp = new();
        CollectingSink sink = new();
        TimingHistory first = TimingHistory.Load(temp.Path, sink.CreateLogger());
        first.Record("map", TimeSpan.FromSeconds(1), 100);
        first.Save();
        Assert.True(File.Exists(temp.Combine(TimingHistory.FileName)));

        TimingHistory second = TimingHistory.Load(temp.Path, sink.CreateLogger());
        second.Record("map", TimeSpan.FromSeconds(3), 100);
        second.Record("map", TimeSpan.FromSeconds(1), 0);
        second.Save();

        TimingHistory third = TimingHistory.Load(temp.Path, sink.CreateLogger());
        Assert.Equal(2.0, third.Expected("map", 100)?.TotalSeconds ?? 0, 6);
        Assert.Null(third.Expected("render", 100));
        Assert.Null(third.Expected("map", 0));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    /// <summary>The render scales with ticks, the analysis with samples, every other phase with rows.</summary>
    [Fact]
    public void EachPhaseScalesWithItsOwnSize()
    {
        using TempDirectory temp = new();
        TimingHistory history = TimingHistory.Load(temp.Path, new CollectingSink().CreateLogger());
        EtaEstimator estimator = new() { History = history };
        estimator.SetSongSize(SongSize.Rows, 100);
        estimator.SetSongSize(SongSize.Ticks, 1000);
        estimator.Finished(Render, TimeSpan.FromSeconds(10));
        estimator.Finished(Map, TimeSpan.FromSeconds(1));
        estimator.SetSongSize(SongSize.Ticks, 3000);

        Assert.Equal((TimeSpan.FromSeconds(31), false), estimator.RunRemaining(TimeSpan.FromSeconds(1), [Render]));
        Assert.Equal(SongSize.Samples, SongSize.Of(new PhaseInfo(6, "analyze", "Analyze samples", "samples")));
        Assert.Equal(SongSize.Rows, SongSize.Of(new PhaseInfo(2, "read", "Read MOD", "bytes")));
    }

    /// <summary>A damaged or foreign file is ignored with a Debug line, never a failure.</summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("{\"v\":2,\"seconds_per_unit\":{\"map\":1}}")]
    [InlineData("{\"v\":1,\"seconds_per_unit\":{\"map\":-1,\"write\":\"x\"}}")]
    public void ABadFileIsIgnored(string content)
    {
        using TempDirectory temp = new();
        File.WriteAllText(temp.Combine(TimingHistory.FileName), content);
        CollectingSink sink = new();

        TimingHistory history = TimingHistory.Load(temp.Path, sink.CreateLogger());

        Assert.Null(history.Expected("map", 100));
        Assert.Null(history.Expected("write", 100));
        Assert.DoesNotContain(sink.Events, e => e.Level >= LogEventLevel.Warning);
    }

    /// <summary>A history that can't be written is reported at Debug and leaves no temporary file.</summary>
    [Fact]
    public void AnUnwritableHistoryIsIgnored()
    {
        using TempDirectory temp = new();
        string blocked = temp.CreateFile("blocked", [1]); // a file where the cache folder should be
        CollectingSink sink = new();
        TimingHistory history = TimingHistory.Load(blocked, sink.CreateLogger());
        history.Record("map", TimeSpan.FromSeconds(1), 10);

        history.Save();

        Assert.Contains(sink.Events, e => e.Level == LogEventLevel.Debug && e.MessageTemplate.Text.Contains("can't be written", StringComparison.Ordinal));
        Assert.Single(Directory.GetFiles(temp.Path));
    }

    /// <summary>A conversion keeps its timings in the cache folder, and the next run of the song uses them for every step left.</summary>
    [Fact]
    public void AConversionKeepsItsTimings()
    {
        using TempDirectory cache = new();

        (_, string first, _) = OutputRuns.ConvertWithCache("text", cache.Path);
        (_, string second, _) = OutputRuns.ConvertWithCache("text", cache.Path);

        Assert.True(File.Exists(cache.Combine(TimingHistory.FileName)));
        Assert.Contains("step 4/7 model start | eta ?", first, StringComparison.Ordinal);
        Assert.Contains("step 4/7 model start | eta #s", second, StringComparison.Ordinal);
    }
}
