using Mod2Xgm.Output;
using Serilog.Events;

namespace Mod2Xgm.Tests.Output;

/// <summary>
/// Tests of <see cref="TimingHistory"/> and of <see cref="EtaEstimator"/> with one (plan T17.6,
/// docs/cli-output.md#time-estimate). The history lives in a file in the cache folder, so these tests use a temporary folder.
/// </summary>
[Collection(ProgramState.Name)]
[Trait("Category", "Integration")]
public sealed class TimingHistoryTests : IDisposable
{
    private static readonly PhaseInfo Map = new(6, "map", "Map channels", string.Empty);
    private static readonly PhaseInfo Write = new(7, "write", "Write DMF", string.Empty);
    private static readonly PhaseInfo Render = new(8, "render", "Render WAV", "ticks");

    private readonly TempDirectory _temp = new();
    private readonly CollectingSink _sink = new();

    /// <summary>Deletes the temporary folder.</summary>
    public void Dispose() => _temp.Dispose();

    /// <summary>A phase's recorded pace is per row, so a song twice as long is expected to take twice as long.</summary>
    [Fact]
    public void Expected_AfterPhaseFinishedAtOtherSize_ScalesWithRows()
    {
        // Arrange
        TimingHistory sut = TimingHistory.Load(_temp.Path, _sink.CreateLogger());
        EtaEstimator estimator = new() { History = sut };
        estimator.SetSongSize(SongSize.Rows, 100);
        estimator.Finished(Map, TimeSpan.FromSeconds(2));

        // Act
        TimeSpan? expected = sut.Expected("map", 200);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(4), expected);
    }

    /// <summary>Before its own pace counts, the running phase's time left is its history's guess less the time spent.</summary>
    [Fact]
    public void Remaining_WithHistoryBeforeOwnPace_ReturnsHistoryGuessLessElapsed()
    {
        // Arrange
        EtaEstimator sut = EstimatorWithMapAndWrite();

        // Act
        TimeSpan? remaining = sut.Remaining(Map, TimeSpan.FromSeconds(1), 0, 0);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(3), remaining);
    }

    /// <summary>With history for every phase left, the run's time left is complete.</summary>
    [Fact]
    public void RunRemaining_WithHistoryForEveryPhaseLeft_ReturnsCompleteSum()
    {
        // Arrange
        EtaEstimator sut = EstimatorWithMapAndWrite();

        // Act
        (TimeSpan? remaining, bool partial) = sut.RunRemaining(TimeSpan.FromSeconds(3), [Write]);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(5), remaining);
        Assert.False(partial);
    }

    /// <summary>A phase left without history makes the run's time left partial.</summary>
    [Fact]
    public void RunRemaining_WithPhaseLeftWithoutHistory_ReturnsPartialSum()
    {
        // Arrange
        EtaEstimator sut = EstimatorWithMapAndWrite();

        // Act
        (TimeSpan? remaining, bool partial) = sut.RunRemaining(null, [Write, Render]);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(2), remaining);
        Assert.True(partial);
    }

    /// <summary>The render's history scales with ticks, independently of the rows.</summary>
    [Fact]
    public void RunRemaining_ForRender_ScalesWithTicks()
    {
        // Arrange
        EtaEstimator sut = new() { History = TimingHistory.Load(_temp.Path, _sink.CreateLogger()) };
        sut.SetSongSize(SongSize.Rows, 100);
        sut.SetSongSize(SongSize.Ticks, 1000);
        sut.Finished(Render, TimeSpan.FromSeconds(10));
        sut.Finished(Map, TimeSpan.FromSeconds(1));
        sut.SetSongSize(SongSize.Ticks, 3000);

        // Act
        (TimeSpan? remaining, bool partial) = sut.RunRemaining(TimeSpan.FromSeconds(1), [Render]);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(31), remaining);
        Assert.False(partial);
    }

    /// <summary>Saving writes the history file in the cache folder.</summary>
    [Fact]
    public void Save_WithRecordedPhase_WritesHistoryFile()
    {
        // Arrange
        TimingHistory sut = TimingHistory.Load(_temp.Path, _sink.CreateLogger());
        sut.Record("map", TimeSpan.FromSeconds(1), 100);

        // Act
        sut.Save();

        // Assert
        Assert.True(File.Exists(_temp.Combine(TimingHistory.FileName)));
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp"));
    }

    /// <summary>The history survives a save and a load, and each run moves a phase's average halfway to its new pace; a run of size 0 teaches nothing.</summary>
    [Fact]
    public void Load_AfterTwoSavedRuns_ReturnsAverageMovedHalfway()
    {
        // Arrange
        TimingHistory first = TimingHistory.Load(_temp.Path, _sink.CreateLogger());
        first.Record("map", TimeSpan.FromSeconds(1), 100);
        first.Save();
        TimingHistory second = TimingHistory.Load(_temp.Path, _sink.CreateLogger());
        second.Record("map", TimeSpan.FromSeconds(3), 100);
        second.Record("map", TimeSpan.FromSeconds(1), 0);
        second.Save();

        // Act
        TimingHistory sut = TimingHistory.Load(_temp.Path, _sink.CreateLogger());

        // Assert
        TimeSpan? expected = sut.Expected("map", 100);
        Assert.NotNull(expected);
        Assert.Equal(2.0, expected.Value.TotalSeconds, 6);
    }

    /// <summary>A phase never recorded, or a song of size 0, has no guess.</summary>
    [Theory]
    [InlineData("render", 100)]
    [InlineData("map", 0)]
    public void Expected_WithUnknownPhaseOrZeroSize_ReturnsNull(string phase, long size)
    {
        // Arrange
        TimingHistory sut = TimingHistory.Load(_temp.Path, _sink.CreateLogger());
        sut.Record("map", TimeSpan.FromSeconds(1), 100);

        // Act
        TimeSpan? expected = sut.Expected(phase, size);

        // Assert
        Assert.Null(expected);
    }

    /// <summary>A damaged or foreign file is ignored with a Debug line, never a failure.</summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("{\"v\":2,\"seconds_per_unit\":{\"map\":1}}")]
    [InlineData("{\"v\":1,\"seconds_per_unit\":{\"map\":-1,\"write\":\"x\"}}")]
    public void Load_WithBadFile_IgnoresItQuietly(string content)
    {
        // Arrange
        File.WriteAllText(_temp.Combine(TimingHistory.FileName), content);

        // Act
        TimingHistory sut = TimingHistory.Load(_temp.Path, _sink.CreateLogger());

        // Assert
        Assert.Null(sut.Expected("map", 100));
        Assert.Null(sut.Expected("write", 100));
        Assert.DoesNotContain(_sink.Events, e => e.Level >= LogEventLevel.Warning);
    }

    /// <summary>A history that can't be written is reported at Debug and leaves no temporary file.</summary>
    [Fact]
    public void Save_WhenFolderIsAFile_LogsDebugAndLeavesNoTemporaryFile()
    {
        // Arrange
        string blocked = _temp.CreateFile("blocked", [1]); // a file where the cache folder should be
        TimingHistory sut = TimingHistory.Load(blocked, _sink.CreateLogger());
        sut.Record("map", TimeSpan.FromSeconds(1), 10);

        // Act
        sut.Save();

        // Assert
        Assert.Contains(_sink.Events, e => e.Level == LogEventLevel.Debug && e.MessageTemplate.Text.Contains("can't be written", StringComparison.Ordinal));
        Assert.Single(Directory.GetFiles(_temp.Path));
    }

    /// <summary>A conversion keeps its timings in the cache folder, and the next run of the song uses them for every step left.</summary>
    [Fact]
    public void ConvertWithCache_RunTwice_SecondRunHasEstimates()
    {
        // Arrange
        string first = OutputRuns.ConvertWithCache("text", _temp.Path);

        // Act
        string second = OutputRuns.ConvertWithCache("text", _temp.Path);

        // Assert
        Assert.True(File.Exists(_temp.Combine(TimingHistory.FileName)));
        Assert.Contains("step 4/7 model start | eta ?", first, StringComparison.Ordinal);
        Assert.Contains("step 4/7 model start | eta #s", second, StringComparison.Ordinal);
    }

    /// <summary>An estimator whose history learned map (2 s) and write (1 s) at 100 rows, now on a 200-row song.</summary>
    private EtaEstimator EstimatorWithMapAndWrite()
    {
        EtaEstimator estimator = new() { History = TimingHistory.Load(_temp.Path, _sink.CreateLogger()) };
        estimator.SetSongSize(SongSize.Rows, 100);
        estimator.Finished(Map, TimeSpan.FromSeconds(2));
        estimator.Finished(Write, TimeSpan.FromSeconds(1));
        estimator.SetSongSize(SongSize.Rows, 200);
        return estimator;
    }
}
