using System.Text.Json;
using Mod2Xgm.Output;
using Serilog.Events;
using Serilog.Parsing;

namespace Mod2Xgm.Tests.Output;

/// <summary>Tests of <see cref="JsonReporter"/>, <c>--progress json</c> (plan T17.4, docs/cli-output.md#json). Whole runs are in <see cref="ReporterGoldenTests"/>.</summary>
public sealed class JsonReporterTests : IDisposable
{
    private readonly StringWriter _output = new();
    private readonly JsonReporter _sut;

    /// <summary>A reporter writing to a string.</summary>
    public JsonReporterTests() => _sut = new JsonReporter(_output);

    /// <summary>Disposes the reporter and its writer.</summary>
    public void Dispose()
    {
        _sut.Dispose();
        _output.Dispose();
    }

    /// <summary>A run with steps at once (the harness) carries <c>jobs</c> on its run object.</summary>
    [Fact]
    public void RunStarted_WithJobs_WritesJobs()
    {
        // Act
        _sut.RunStarted(new RunInfo("corpus", RunInfo.HarnessFormat, "normal", [new PhaseInfo(1, "song:a", "a", string.Empty)], Jobs: 2));

        // Assert
        using JsonDocument run = JsonDocument.Parse(Lines()[0]);
        Assert.Equal(2, run.RootElement.GetProperty("jobs").GetInt32());
    }

    /// <summary>A failure whose kind was set (the harness's <c>cannot-run</c>) keeps it and its code.</summary>
    [Fact]
    public void Failed_WithKindSet_WritesThatKind()
    {
        // Act
        _sut.Failed(new RunFailure((ExitCode)2, "libopenmpt not found", null, null, [], TimeSpan.Zero) { Kind = "cannot-run" });

        // Assert
        using JsonDocument error = JsonDocument.Parse(Lines()[^1]);
        Assert.Equal("cannot-run", error.RootElement.GetProperty("error").GetString());
        Assert.Equal(2, error.RootElement.GetProperty("code").GetInt32());
    }

    /// <summary>A usage error lists its problems, the missing arguments and the usage line, and no stack.</summary>
    [Fact]
    public void Failed_WithUsageError_WritesMissingArgumentAndUsageLine()
    {
        // Arrange
        UsageException usage = new(["Required argument missing: <input.mod>."], ["input.mod"]);

        // Act
        _sut.Failed(new RunFailure(ExitCode.UsageError, usage.Message, null, usage, [], TimeSpan.Zero));

        // Assert
        using JsonDocument document = JsonDocument.Parse(_output.ToString());
        JsonElement root = document.RootElement;
        Assert.Equal("usage", root.GetProperty("error").GetString());
        Assert.Equal("input.mod", root.GetProperty("missing")[0].GetString());
        Assert.Equal(UsageException.UsageLine, root.GetProperty("usage").GetString());
        Assert.False(root.TryGetProperty("stack", out _));
    }

    /// <summary>A crash carries its stack trace.</summary>
    [Fact]
    public void Failed_WithCrash_WritesStack()
    {
        // Act
        _sut.Failed(new RunFailure(ExitCode.InternalError, "boom", new InvalidOperationException("boom"), null, [], TimeSpan.Zero));

        // Assert
        using JsonDocument document = JsonDocument.Parse(_output.ToString());
        Assert.Equal("internal", document.RootElement.GetProperty("error").GetString());
        Assert.StartsWith("System.InvalidOperationException: boom", document.RootElement.GetProperty("stack").GetString(), StringComparison.Ordinal);
    }

    /// <summary>Progress objects come at most once a second per phase; start and end always come.</summary>
    [Fact]
    public void Phase_WithProgressEveryHalfSecond_WritesOneProgressPerSecond()
    {
        // Arrange
        PhaseInfo render = new(7, "render", "Render", "ticks");

        // Act
        _sut.Phase(Update(render, PhaseEvent.Start, 0, 0));
        _sut.Phase(Update(render, PhaseEvent.Progress, 1, 0.5));
        _sut.Phase(Update(render, PhaseEvent.Progress, 2, 1.0));
        _sut.Phase(Update(render, PhaseEvent.Progress, 3, 1.5));
        _sut.Phase(Update(render, PhaseEvent.End, 4, 1.6));

        // Assert
        string[] lines = Lines();
        Assert.Equal(3, lines.Length);
        using JsonDocument progress = JsonDocument.Parse(lines[1]);
        JsonElement root = progress.RootElement;
        Assert.Equal("progress", root.GetProperty("event").GetString());
        Assert.Equal(2, root.GetProperty("done").GetInt64());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("eta_s").ValueKind);
        Assert.Equal(12.5, root.GetProperty("total_eta_s").GetDouble());
    }

    /// <summary>A note keeps its template and its typed properties.</summary>
    [Fact]
    public void Emit_WithWarning_WritesNoteWithTemplateAndTypedProperties()
    {
        // Arrange
        MessageTemplate template = new MessageTemplateParser().Parse("Sample {Sample} truncated to {Length} bytes");
        LogEvent note = new(
            DateTimeOffset.UnixEpoch,
            LogEventLevel.Warning,
            null,
            template,
            [new LogEventProperty("Sample", new ScalarValue("snare \"2\"")), new LogEventProperty("Length", new ScalarValue(4096))]);

        // Act
        _sut.Emit(note);

        // Assert
        using JsonDocument document = JsonDocument.Parse(Lines()[0]);
        Assert.Equal("note", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("Sample {Sample} truncated to {Length} bytes", document.RootElement.GetProperty("template").GetString());
        Assert.Equal("snare \"2\"", document.RootElement.GetProperty("properties").GetProperty("Sample").GetString());
        Assert.Equal(4096, document.RootElement.GetProperty("properties").GetProperty("Length").GetInt32());
    }

    /// <summary>A Debug line becomes a <c>debug</c> object.</summary>
    [Fact]
    public void Emit_WithDebug_WritesDebugObject()
    {
        // Act
        _sut.Emit(new LogEvent(DateTimeOffset.UnixEpoch, LogEventLevel.Debug, null, new MessageTemplateParser().Parse("detail"), []));

        // Assert
        using JsonDocument document = JsonDocument.Parse(Lines()[0]);
        Assert.Equal("debug", document.RootElement.GetProperty("type").GetString());
    }

    /// <summary>Report rows become objects of their kind with typed fields; a part row writes nothing.</summary>
    [Fact]
    public void Emit_WithReportRow_WritesObjectOfItsKind()
    {
        // Act
        _sut.Emit(Row(new ReportRow("instrument", ("kind", "DAC"), ("n", 1), ("from", new[] { 3, 4 }), ("volume_db", -6.5))));
        _sut.Emit(Row(ReportRow.Part));

        // Assert
        using JsonDocument instrument = JsonDocument.Parse(Assert.Single(Lines()));
        Assert.Equal("instrument", instrument.RootElement.GetProperty("type").GetString());
        Assert.Equal(4, instrument.RootElement.GetProperty("from")[1].GetInt32());
        Assert.Equal(-6.5, instrument.RootElement.GetProperty("volume_db").GetDouble());
    }

    /// <summary>The result row feeds the result object written on completion, with the run's time.</summary>
    [Fact]
    public void Completed_AfterResultRow_WritesResultObject()
    {
        // Arrange
        _sut.Emit(Row(new ReportRow("result", ("input", "song.mod"), ("output", "song.dmf"), ("notes", 2))));

        // Act
        _sut.Completed(new RunOutcome(ExitCode.Success, [], TimeSpan.FromSeconds(1.5)));

        // Assert
        using JsonDocument result = JsonDocument.Parse(Assert.Single(Lines()));
        Assert.Equal("song.dmf", result.RootElement.GetProperty("output").GetString());
        Assert.Equal(2, result.RootElement.GetProperty("notes").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("wav").ValueKind);
        Assert.Equal(1.5, result.RootElement.GetProperty("seconds").GetDouble());
    }

    /// <summary>The non-empty lines written so far.</summary>
    private string[] Lines() => _output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A phase update of a 10-unit phase with a 12.5 s run estimate.</summary>
    private static PhaseUpdate Update(PhaseInfo phase, PhaseEvent phaseEvent, long done, double elapsed) =>
        new(phase, 9, phaseEvent, done, 10, TimeSpan.FromSeconds(elapsed), null, TimeSpan.FromSeconds(elapsed), TimeSpan.FromSeconds(12.5), false);

    /// <summary>An Information event carrying <paramref name="row"/>.</summary>
    private static LogEvent Row(ReportRow row)
    {
        LogEvent logEvent = new(DateTimeOffset.UnixEpoch, LogEventLevel.Information, null, new MessageTemplateParser().Parse("{ReportLine:l}"), []);
        row.Enrich(logEvent, null!); // ReportRow doesn't use the factory
        return logEvent;
    }
}
