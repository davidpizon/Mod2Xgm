using Mod2Xgm.Output;
using Serilog.Events;
using Serilog.Parsing;

namespace Mod2Xgm.Tests.Output;

/// <summary>Tests of <c>--progress text</c> (plan T17.3, docs/cli-output.md#text).</summary>
[Collection(ProgramState.Name)]
public sealed class TextReporterTests
{
    /// <summary>A whole conversion in text mode matches its golden file: the report, the notes and the result.</summary>
    [Fact]
    public void ConversionMatchesGolden()
    {
        (int code, string output, string error) = OutputRuns.Convert("text");

        Assert.Equal((int)ExitCode.Success, code);
        Assert.Equal(string.Empty, error);
        Golden.Verify(Path.Combine("output", "text-conversion.txt"), output);
    }

    /// <summary>A failed conversion ends with an <c>error</c> line on stderr and nothing more on stdout.</summary>
    [Fact]
    public void FailureIsAnErrorLine()
    {
        (int code, _, string error) = OutputRuns.Convert("text", "--output", "/no/such/folder/out.dmf");

        Assert.Equal((int)ExitCode.OutputNotWritable, code);
        Assert.StartsWith("error exit=4 kind=output-not-writable message=\"Output directory doesn't exist:", error, StringComparison.Ordinal);
    }

    /// <summary>Every sample row keeps its name exactly, in quotes, with quotes escaped and control characters shown.</summary>
    [Theory]
    [InlineData("bassdrum", "\"bassdrum\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("a\\b", "\"a\\\\b\"")]
    [InlineData("bad\u0007", "\"bad·\"")]
    [InlineData("{x} [y]", "\"{x} [y]\"")]
    public void NamesAreQuoted(string name, string expected) => Assert.Equal(expected, TextValues.Quote(name));

    /// <summary>Values are quoted only when they need it; numbers are invariant.</summary>
    [Theory]
    [InlineData("dmf", "dmf")]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("a=b", "\"a=b\"")]
    [InlineData(@"C:\out\song.dmf", @"C:\out\song.dmf")]
    [InlineData(null, "-")]
    public void ValuesAreQuotedWhenNeeded(string? value, string expected) => Assert.Equal(expected, TextValues.Format(value));

    /// <summary>Numbers, flags and times are written the same everywhere.</summary>
    [Fact]
    public void NumbersAndTimes()
    {
        Assert.Equal("140.5", TextValues.Format(140.5));
        Assert.Equal("3", TextValues.Format(3L));
        Assert.Equal("true", TextValues.Format(true));
        Assert.Equal("1.5", TextValues.Format(1.5f));
        Assert.Equal("4.0s", TextValues.Seconds(TimeSpan.FromSeconds(4)));
        Assert.Equal("12s", TextValues.Eta(TimeSpan.FromSeconds(11.2), false));
        Assert.Equal("12s+", TextValues.Eta(TimeSpan.FromSeconds(12), true));
        Assert.Equal("?", TextValues.Eta(null, false));
        Assert.Equal("40%", TextValues.Percent(0.409));
    }

    /// <summary>Phases print a start, progress at most every five seconds, and an end; the run line names the steps.</summary>
    [Fact]
    public void PhaseLines()
    {
        using StringWriter output = new();
        using TextReporter reporter = new(output, TextWriter.Null);
        PhaseInfo map = new(5, "map", "Map channels", "orders");

        reporter.RunStarted(new RunInfo("C:/music/song.mod", "dmf", "normal", [map]));
        reporter.Phase(Update(map, PhaseEvent.Start, 0, 10, 0, 12));
        reporter.Phase(Update(map, PhaseEvent.Progress, 4, 10, 1, 9));
        reporter.Phase(Update(map, PhaseEvent.Progress, 4, 10, 6, 9));
        reporter.Phase(Update(map, PhaseEvent.Progress, 0, 0, 12, null));
        reporter.Phase(Update(map, PhaseEvent.End, 10, 10, 13, 0));

        string[] lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            [
                $"run mod2xgm={RunInfo.Version} input=song.mod format=dmf quality=normal steps=1",
                "step 5/9 map start | eta 12s",
                "step 5/9 map 40% | 6.0s | eta 9s",
                "step 5/9 map 0 orders | 12.0s | eta ?",
                "step 5/9 map done | 13.0s",
            ],
            lines);
    }

    /// <summary>The outcome prints the timing line and the result, with what the conversion wrote.</summary>
    [Fact]
    public void ResultLine()
    {
        using StringWriter output = new();
        using TextReporter reporter = new(output, TextWriter.Null);
        PhaseInfo read = new(2, "read", "Read MOD", string.Empty);

        reporter.Emit(Row(new ReportRow("result", ("input", "song.mod"), ("output", "C:/out/song.dmf"), ("notes", 3))));
        reporter.Completed(new RunOutcome(ExitCode.Success, [new PhaseTiming(read, TimeSpan.FromSeconds(0.25), 0)], TimeSpan.FromSeconds(2)));
        reporter.Completed(new RunOutcome(ExitCode.DoesNotFit, [], TimeSpan.FromSeconds(2)));

        Assert.Equal(
            "time read=0.3s total=2.0s" + Environment.NewLine
            + "result ok exit=0 out=C:/out/song.dmf notes=3 time=2.0s" + Environment.NewLine
            + "result fail exit=6 out=C:/out/song.dmf notes=3 time=2.0s" + Environment.NewLine,
            output.ToString());
    }

    /// <summary>A usage error names the missing argument and the usage line; a crash adds its stack.</summary>
    [Fact]
    public void ErrorLines()
    {
        using StringWriter error = new();
        using TextReporter reporter = new(TextWriter.Null, error);
        UsageException usage = new(["Required argument missing: <input.mod>."], ["input.mod"]);

        reporter.Failed(new RunFailure(ExitCode.UsageError, usage.Message, null, usage, [], TimeSpan.Zero));
        reporter.Failed(new RunFailure(ExitCode.InternalError, "boom", new InvalidOperationException("boom"), null, [], TimeSpan.Zero));

        string[] lines = error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("error exit=1 kind=usage message=\"Required argument missing: <input.mod>.\" missing=input.mod usage=\"mod2xgm <input.mod> [options]\"", lines[0]);
        Assert.Equal("error exit=5 kind=internal message=boom", lines[1]);
        Assert.Equal("stack System.InvalidOperationException: boom", lines[2]);
    }

    /// <summary>Notes, detail and ordinary lines get their keyword; parts of a report row print nothing.</summary>
    [Fact]
    public void EventLines()
    {
        using StringWriter output = new();
        using TextReporter reporter = new(output, TextWriter.Null);

        reporter.Emit(Event(LogEventLevel.Warning, "Effect dropped"));
        reporter.Emit(Event(LogEventLevel.Debug, "detail"));
        reporter.Emit(Event(LogEventLevel.Information, "Emulating ProTracker 2"));
        reporter.Emit(Row(ReportRow.Part));
        reporter.Emit(Row(new ReportRow("instrument", ("kind", "DAC"), ("n", 1), ("from", new[] { 3 }), ("rate_hz", 11025), ("bytes", 5321), ("offset", 2048), ("volume_db", -6.0))));

        Assert.Equal(
            "note: Effect dropped" + Environment.NewLine
            + "debug detail" + Environment.NewLine
            + "info Emulating ProTracker 2" + Environment.NewLine
            + "output DAC 01 <- 03 \"\" rate=11025 bytes=5321 offset=2048 volume_db=-6" + Environment.NewLine,
            output.ToString());
    }

    private static PhaseUpdate Update(PhaseInfo phase, PhaseEvent phaseEvent, long done, long total, double elapsed, double? eta) =>
        new(phase, 9, phaseEvent, done, total, TimeSpan.FromSeconds(elapsed), null, TimeSpan.FromSeconds(elapsed), eta is { } e ? TimeSpan.FromSeconds(e) : null, false);

    private static LogEvent Event(LogEventLevel level, string text) =>
        new(DateTimeOffset.UnixEpoch, level, null, new MessageTemplateParser().Parse(text), []);

    private static LogEvent Row(ReportRow row)
    {
        LogEvent logEvent = new(DateTimeOffset.UnixEpoch, LogEventLevel.Information, null, new MessageTemplateParser().Parse("{ReportLine:l}"), []);
        row.Enrich(logEvent, null!); // ReportRow doesn't use the factory
        return logEvent;
    }
}
