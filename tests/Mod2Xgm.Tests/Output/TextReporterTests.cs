using Mod2Xgm.Output;
using Serilog.Events;
using Serilog.Parsing;

namespace Mod2Xgm.Tests.Output;

/// <summary>Tests of <see cref="TextReporter"/> and <see cref="TextValues"/>, <c>--progress text</c> (plan T17.3, docs/cli-output.md#text). Whole runs are in <see cref="ReporterGoldenTests"/>.</summary>
public sealed class TextReporterTests : IDisposable
{
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private readonly TextReporter _sut;

    /// <summary>A reporter writing stdout and stderr to strings.</summary>
    public TextReporterTests() => _sut = new TextReporter(_output, _error);

    /// <summary>Disposes the reporter and its writers.</summary>
    public void Dispose()
    {
        _sut.Dispose();
        _output.Dispose();
        _error.Dispose();
    }

    /// <summary>Every sample row keeps its name exactly, in quotes, with quotes escaped and control characters shown.</summary>
    [Theory]
    [InlineData("bassdrum", "\"bassdrum\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("a\\b", "\"a\\\\b\"")]
    [InlineData("bad\u0007", "\"bad·\"")]
    [InlineData("{x} [y]", "\"{x} [y]\"")]
    public void Quote_WithName_ReturnsQuotedEscapedName(string name, string expected)
    {
        // Act
        string quoted = TextValues.Quote(name);

        // Assert
        Assert.Equal(expected, quoted);
    }

    /// <summary>Values are quoted only when they need it; null is a dash.</summary>
    [Theory]
    [InlineData("dmf", "dmf")]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("a=b", "\"a=b\"")]
    [InlineData(@"C:\out\song.dmf", @"C:\out\song.dmf")]
    [InlineData(null, "-")]
    public void Format_WithText_QuotesOnlyWhenNeeded(string? value, string expected)
    {
        // Act
        string formatted = TextValues.Format(value);

        // Assert
        Assert.Equal(expected, formatted);
    }

    /// <summary>Numbers and flags are written invariantly, the same everywhere.</summary>
    [Theory]
    [MemberData(nameof(Scalars))]
    public void Format_WithScalar_WritesInvariantText(object value, string expected)
    {
        // Act
        string formatted = TextValues.Format(value);

        // Assert
        Assert.Equal(expected, formatted);
    }

    /// <summary>A duration is written with one decimal and <c>s</c>.</summary>
    [Fact]
    public void Seconds_WithDuration_WritesOneDecimal()
    {
        // Act
        string text = TextValues.Seconds(TimeSpan.FromSeconds(4));

        // Assert
        Assert.Equal("4.0s", text);
    }

    /// <summary>An estimate is rounded up to whole seconds, <c>+</c> when partial, <c>?</c> when unknown.</summary>
    [Theory]
    [InlineData(11.2, false, "12s")]
    [InlineData(12.0, true, "12s+")]
    [InlineData(null, false, "?")]
    public void Eta_WithEstimate_WritesWholeSeconds(double? seconds, bool partial, string expected)
    {
        // Arrange
        TimeSpan? eta = seconds is { } s ? TimeSpan.FromSeconds(s) : null;

        // Act
        string text = TextValues.Eta(eta, partial);

        // Assert
        Assert.Equal(expected, text);
    }

    /// <summary>A share is written as a whole percentage, rounded down.</summary>
    [Fact]
    public void Percent_WithShare_WritesWholePercentRoundedDown()
    {
        // Act
        string text = TextValues.Percent(0.409);

        // Assert
        Assert.Equal("40%", text);
    }

    /// <summary>Phases print a start, progress at most every five seconds, and an end; the run line names the steps.</summary>
    [Fact]
    public void Phase_WithStartProgressAndEnd_WritesThrottledStepLines()
    {
        // Arrange
        PhaseInfo map = new(5, "map", "Map channels", "orders");

        // Act
        _sut.RunStarted(new RunInfo("C:/music/song.mod", "dmf", "normal", [map]));
        _sut.Phase(Update(map, PhaseEvent.Start, 0, 10, 0, 12));
        _sut.Phase(Update(map, PhaseEvent.Progress, 4, 10, 1, 9));
        _sut.Phase(Update(map, PhaseEvent.Progress, 4, 10, 6, 9));
        _sut.Phase(Update(map, PhaseEvent.Progress, 0, 0, 12, null));
        _sut.Phase(Update(map, PhaseEvent.End, 10, 10, 13, 0));

        // Assert
        Assert.Equal(
            [
                $"run mod2xgm={RunInfo.Version} input=song.mod format=dmf quality=normal steps=1",
                "step 5/9 map start | eta 12s",
                "step 5/9 map 40% | 6.0s | eta 9s",
                "step 5/9 map 0 orders | 12.0s | eta ?",
                "step 5/9 map done | 13.0s",
            ],
            Lines(_output));
    }

    /// <summary>A run with steps at once (the harness) names its jobs on the run line.</summary>
    [Fact]
    public void RunStarted_WithJobs_NamesJobsOnRunLine()
    {
        // Act
        _sut.RunStarted(new RunInfo("corpus", RunInfo.HarnessFormat, "normal", [new PhaseInfo(1, "song:a", "a", string.Empty)], Jobs: 3));

        // Assert
        Assert.EndsWith("format=harness quality=normal steps=1 jobs=3" + Environment.NewLine, _output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A successful outcome prints the timing line and the result, with what the conversion wrote.</summary>
    [Fact]
    public void Completed_WithSuccess_WritesTimingAndOkResult()
    {
        // Arrange
        PhaseInfo read = new(2, "read", "Read MOD", string.Empty);
        _sut.Emit(Row(new ReportRow("result", ("input", "song.mod"), ("output", "C:/out/song.dmf"), ("notes", 3))));

        // Act
        _sut.Completed(new RunOutcome(ExitCode.Success, [new PhaseTiming(read, TimeSpan.FromSeconds(0.25), 0)], TimeSpan.FromSeconds(2)));

        // Assert
        Assert.Equal(
            "time read=0.3s total=2.0s" + Environment.NewLine
            + "result ok exit=0 out=C:/out/song.dmf notes=3 time=2.0s" + Environment.NewLine,
            _output.ToString());
    }

    /// <summary>An outcome with a non-zero exit code prints a <c>fail</c> result.</summary>
    [Fact]
    public void Completed_WithDoesNotFit_WritesFailResult()
    {
        // Arrange
        _sut.Emit(Row(new ReportRow("result", ("input", "song.mod"), ("output", "C:/out/song.dmf"), ("notes", 3))));

        // Act
        _sut.Completed(new RunOutcome(ExitCode.DoesNotFit, [], TimeSpan.FromSeconds(2)));

        // Assert
        Assert.EndsWith("result fail exit=6 out=C:/out/song.dmf notes=3 time=2.0s" + Environment.NewLine, _output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A usage error names the missing argument and the usage line.</summary>
    [Fact]
    public void Failed_WithUsageError_WritesMissingArgumentAndUsageLine()
    {
        // Arrange
        UsageException usage = new(["Required argument missing: <input.mod>."], ["input.mod"]);

        // Act
        _sut.Failed(new RunFailure(ExitCode.UsageError, usage.Message, null, usage, [], TimeSpan.Zero));

        // Assert
        Assert.Equal(
            ["error exit=1 kind=usage message=\"Required argument missing: <input.mod>.\" missing=input.mod usage=\"mod2xgm <input.mod> [options]\""],
            Lines(_error));
    }

    /// <summary>A crash writes its error line, then its stack.</summary>
    [Fact]
    public void Failed_WithCrash_WritesErrorThenStack()
    {
        // Act
        _sut.Failed(new RunFailure(ExitCode.InternalError, "boom", new InvalidOperationException("boom"), null, [], TimeSpan.Zero));

        // Assert
        string[] lines = Lines(_error);
        Assert.Equal("error exit=5 kind=internal message=boom", lines[0]);
        Assert.Equal("stack System.InvalidOperationException: boom", lines[1]);
    }

    /// <summary>Notes, detail and ordinary lines get their keyword; parts of a report row print nothing; output rows print their fields.</summary>
    [Fact]
    public void Emit_WithEachKindOfEvent_WritesItsKeyword()
    {
        // Act
        _sut.Emit(Event(LogEventLevel.Warning, "Effect dropped"));
        _sut.Emit(Event(LogEventLevel.Debug, "detail"));
        _sut.Emit(Event(LogEventLevel.Information, "Emulating ProTracker 2"));
        _sut.Emit(Row(ReportRow.Part));
        _sut.Emit(Row(new ReportRow("instrument", ("kind", "DAC"), ("n", 1), ("from", new[] { 3 }), ("rate_hz", 11025), ("bytes", 5321), ("offset", 2048), ("volume_db", -6.0))));

        // Assert
        Assert.Equal(
            "note: Effect dropped" + Environment.NewLine
            + "debug detail" + Environment.NewLine
            + "info Emulating ProTracker 2" + Environment.NewLine
            + "output DAC 01 <- 03 \"\" rate=11025 bytes=5321 offset=2048 volume_db=-6" + Environment.NewLine,
            _output.ToString());
    }

    /// <summary>Scalars and their invariant text.</summary>
    public static TheoryData<object, string> Scalars() => new()
    {
        { 140.5, "140.5" },
        { 3L, "3" },
        { true, "true" },
        { 1.5f, "1.5" },
    };

    /// <summary>The non-empty lines of <paramref name="writer"/>.</summary>
    private static string[] Lines(StringWriter writer) => writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A phase update with its elapsed time and run estimate in seconds.</summary>
    private static PhaseUpdate Update(PhaseInfo phase, PhaseEvent phaseEvent, long done, long total, double elapsed, double? eta) =>
        new(phase, 9, phaseEvent, done, total, TimeSpan.FromSeconds(elapsed), null, TimeSpan.FromSeconds(elapsed), eta is { } e ? TimeSpan.FromSeconds(e) : null, false);

    /// <summary>A log event at <paramref name="level"/> whose message is <paramref name="text"/>.</summary>
    private static LogEvent Event(LogEventLevel level, string text) =>
        new(DateTimeOffset.UnixEpoch, level, null, new MessageTemplateParser().Parse(text), []);

    /// <summary>An Information event carrying <paramref name="row"/>.</summary>
    private static LogEvent Row(ReportRow row)
    {
        LogEvent logEvent = new(DateTimeOffset.UnixEpoch, LogEventLevel.Information, null, new MessageTemplateParser().Parse("{ReportLine:l}"), []);
        row.Enrich(logEvent, null!); // ReportRow doesn't use the factory
        return logEvent;
    }
}
