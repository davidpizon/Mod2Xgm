using System.Text.Json;
using Mod2Xgm.Output;
using Serilog.Events;
using Serilog.Parsing;

namespace Mod2Xgm.Tests.Output;

/// <summary>Tests of <c>--progress json</c> (plan T17.4, docs/cli-output.md#json).</summary>
[Collection(ProgramState.Name)]
public sealed class JsonReporterTests
{
    /// <summary>A whole conversion in json mode matches its golden file, and every line is one JSON object of schema 1.</summary>
    [Fact]
    public void ConversionMatchesGolden()
    {
        (int code, string output, string error) = OutputRuns.Convert("json");

        Assert.Equal((int)ExitCode.Success, code);
        Assert.Equal(string.Empty, error);
        string[] lines = Lines(output);
        foreach (string line in lines)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.Equal(1, document.RootElement.GetProperty("v").GetInt32());
            Assert.False(string.IsNullOrEmpty(document.RootElement.GetProperty("type").GetString()));
        }

        Assert.Equal("result", Type(lines[^1]));
        Golden.Verify(Path.Combine("output", "json-conversion.jsonl"), output);
    }

    /// <summary>A failure ends with an <c>error</c> object on stdout; stderr stays empty.</summary>
    [Fact]
    public void FailureIsAnErrorObject()
    {
        (int code, string output, string error) = OutputRuns.Convert("json", "--output", "/no/such/folder/out.dmf");

        Assert.Equal((int)ExitCode.OutputNotWritable, code);
        Assert.Equal(string.Empty, error);
        using JsonDocument last = JsonDocument.Parse(Lines(output)[^1]);
        Assert.Equal("error", last.RootElement.GetProperty("type").GetString());
        Assert.Equal(4, last.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("output-not-writable", last.RootElement.GetProperty("error").GetString());
    }

    /// <summary>
    /// A run with steps at once (the harness) carries <c>jobs</c>; a failure whose kind was set (the harness's
    /// <c>cannot-run</c>) keeps it.
    /// </summary>
    [Fact]
    public void HarnessRunsCarryJobsAndTheirKind()
    {
        using StringWriter output = new();
        using JsonReporter reporter = new(output);

        reporter.RunStarted(new RunInfo("corpus", RunInfo.HarnessFormat, "normal", [new PhaseInfo(1, "song:a", "a", string.Empty)], Jobs: 2));
        reporter.Failed(new RunFailure((ExitCode)2, "libopenmpt not found", null, null, [], TimeSpan.Zero) { Kind = "cannot-run" });

        string[] lines = Lines(output.ToString());
        using JsonDocument run = JsonDocument.Parse(lines[0]);
        using JsonDocument error = JsonDocument.Parse(lines[^1]);
        Assert.Equal(2, run.RootElement.GetProperty("jobs").GetInt32());
        Assert.Equal("cannot-run", error.RootElement.GetProperty("error").GetString());
        Assert.Equal(2, error.RootElement.GetProperty("code").GetInt32());
    }

    /// <summary>A usage error lists its problems, the missing arguments and the usage line.</summary>
    [Fact]
    public void UsageErrorNamesTheMissingArgument()
    {
        using StringWriter output = new();
        using JsonReporter reporter = new(output);
        UsageException usage = new(["Required argument missing: <input.mod>."], ["input.mod"]);

        reporter.Failed(new RunFailure(ExitCode.UsageError, usage.Message, null, usage, [], TimeSpan.Zero));

        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement root = document.RootElement;
        Assert.Equal("usage", root.GetProperty("error").GetString());
        Assert.Equal("input.mod", root.GetProperty("missing")[0].GetString());
        Assert.Equal(UsageException.UsageLine, root.GetProperty("usage").GetString());
        Assert.False(root.TryGetProperty("stack", out _));
    }

    /// <summary>A crash carries its stack trace.</summary>
    [Fact]
    public void CrashCarriesTheStack()
    {
        using StringWriter output = new();
        using JsonReporter reporter = new(output);

        reporter.Failed(new RunFailure(ExitCode.InternalError, "boom", new InvalidOperationException("boom"), null, [], TimeSpan.Zero));

        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.Equal("internal", document.RootElement.GetProperty("error").GetString());
        Assert.StartsWith("System.InvalidOperationException: boom", document.RootElement.GetProperty("stack").GetString(), StringComparison.Ordinal);
    }

    /// <summary>Progress objects come at most once a second per phase; start and end always come.</summary>
    [Fact]
    public void ProgressIsThrottled()
    {
        using StringWriter output = new();
        using JsonReporter reporter = new(output);
        PhaseInfo render = new(7, "render", "Render", "ticks");

        reporter.Phase(Update(render, PhaseEvent.Start, 0, 0));
        reporter.Phase(Update(render, PhaseEvent.Progress, 1, 0.5));
        reporter.Phase(Update(render, PhaseEvent.Progress, 2, 1.0));
        reporter.Phase(Update(render, PhaseEvent.Progress, 3, 1.5));
        reporter.Phase(Update(render, PhaseEvent.End, 4, 1.6));

        string[] lines = Lines(output.ToString());
        Assert.Equal(3, lines.Length);
        using JsonDocument progress = JsonDocument.Parse(lines[1]);
        JsonElement root = progress.RootElement;
        Assert.Equal("progress", root.GetProperty("event").GetString());
        Assert.Equal(2, root.GetProperty("done").GetInt64());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("eta_s").ValueKind);
        Assert.Equal(12.5, root.GetProperty("total_eta_s").GetDouble());
    }

    /// <summary>Notes keep their template and typed properties; info and debug lines keep their message.</summary>
    [Fact]
    public void NotesKeepTheirProperties()
    {
        using StringWriter output = new();
        using JsonReporter reporter = new(output);
        MessageTemplate template = new MessageTemplateParser().Parse("Sample {Sample} truncated to {Length} bytes");

        reporter.Emit(new LogEvent(
            DateTimeOffset.UnixEpoch,
            LogEventLevel.Warning,
            null,
            template,
            [new LogEventProperty("Sample", new ScalarValue("snare \"2\"")), new LogEventProperty("Length", new ScalarValue(4096))]));
        reporter.Emit(new LogEvent(DateTimeOffset.UnixEpoch, LogEventLevel.Debug, null, new MessageTemplateParser().Parse("detail"), []));

        string[] lines = Lines(output.ToString());
        using JsonDocument note = JsonDocument.Parse(lines[0]);
        Assert.Equal("note", note.RootElement.GetProperty("type").GetString());
        Assert.Equal("Sample {Sample} truncated to {Length} bytes", note.RootElement.GetProperty("template").GetString());
        Assert.Equal("snare \"2\"", note.RootElement.GetProperty("properties").GetProperty("Sample").GetString());
        Assert.Equal(4096, note.RootElement.GetProperty("properties").GetProperty("Length").GetInt32());
        Assert.Equal("debug", Type(lines[1]));
    }

    /// <summary>Report rows become objects of their kind with typed fields; the result row feeds the result object.</summary>
    [Fact]
    public void RowsAreObjects()
    {
        using StringWriter output = new();
        using JsonReporter reporter = new(output);

        reporter.Emit(Row(new ReportRow("instrument", ("kind", "DAC"), ("n", 1), ("from", new[] { 3, 4 }), ("volume_db", -6.5))));
        reporter.Emit(Row(ReportRow.Part));
        reporter.Emit(Row(new ReportRow("result", ("input", "song.mod"), ("output", "song.dmf"), ("notes", 2))));
        reporter.Completed(new RunOutcome(ExitCode.Success, [], TimeSpan.FromSeconds(1.5)));

        string[] lines = Lines(output.ToString());
        Assert.Equal(2, lines.Length);
        using JsonDocument instrument = JsonDocument.Parse(lines[0]);
        Assert.Equal("instrument", instrument.RootElement.GetProperty("type").GetString());
        Assert.Equal(4, instrument.RootElement.GetProperty("from")[1].GetInt32());
        Assert.Equal(-6.5, instrument.RootElement.GetProperty("volume_db").GetDouble());
        using JsonDocument result = JsonDocument.Parse(lines[1]);
        Assert.Equal("song.dmf", result.RootElement.GetProperty("output").GetString());
        Assert.Equal(2, result.RootElement.GetProperty("notes").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("wav").ValueKind);
        Assert.Equal(1.5, result.RootElement.GetProperty("seconds").GetDouble());
    }

    private static string[] Lines(string text) => text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    private static string? Type(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("type").GetString();
    }

    private static PhaseUpdate Update(PhaseInfo phase, PhaseEvent phaseEvent, long done, double elapsed) =>
        new(phase, 9, phaseEvent, done, 10, TimeSpan.FromSeconds(elapsed), null, TimeSpan.FromSeconds(elapsed), TimeSpan.FromSeconds(12.5), false);

    private static LogEvent Row(ReportRow row)
    {
        LogEvent logEvent = new(DateTimeOffset.UnixEpoch, LogEventLevel.Information, null, new MessageTemplateParser().Parse("{ReportLine:l}"), []);
        row.Enrich(logEvent, null!); // ReportRow doesn't use the factory
        return logEvent;
    }
}
