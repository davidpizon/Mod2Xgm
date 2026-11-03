using Mod2Xgm.Output;
using Serilog.Events;
using Serilog.Parsing;
using Spectre.Console.Testing;

namespace Mod2Xgm.Tests.Output;

/// <summary>Tests of <c>--progress fancy</c>, the default (plan T17.7, docs/cli-output.md#fancy).</summary>
[Collection(ProgramState.Name)]
public sealed class FancyReporterTests
{
    private static readonly PhaseInfo Read = new(1, "read", "Read MOD", "bytes");
    private static readonly PhaseInfo Render = new(2, "render", "Render WAV", "ticks");

    /// <summary>
    /// A whole conversion with the output piped matches its golden file: the header, the report as tables, one static
    /// line per finished step, the timing table and the result, with names like <c>snare [2]</c> printed as they are.
    /// </summary>
    [Fact]
    public void PipedConversionMatchesGolden()
    {
        (int code, string output, string error) = OutputRuns.Convert("fancy");

        Assert.Equal((int)ExitCode.Success, code);
        Assert.Equal(string.Empty, error);
        Assert.DoesNotContain("\u001b[", output, StringComparison.Ordinal);
        Golden.Verify(Path.Combine("output", "fancy-conversion.txt"), output);
    }

    /// <summary>A failure prints the error on stderr in fancy mode too; stdout keeps what was shown before it.</summary>
    [Fact]
    public void FailureGoesToStderr()
    {
        (int code, string output, string error) = OutputRuns.Convert("fancy", "--output", "/no/such/folder/out.dmf");

        Assert.Equal((int)ExitCode.OutputNotWritable, code);
        Assert.StartsWith("error Output directory doesn't exist:", error, StringComparison.Ordinal);
        Assert.Contains("(exit 4)", error, StringComparison.Ordinal);
        Assert.StartsWith("── mod2xgm", output, StringComparison.Ordinal);
    }

    /// <summary>A usage error names the problem, the usage line and the help hint; a crash adds its stack.</summary>
    [Fact]
    public void ErrorsNameTheProblem()
    {
        TestConsole console = new();
        TestConsole error = new();
        using FancyReporter reporter = new(console, error, live: false);
        UsageException usage = new(["Required argument missing: <input.mod>."], ["input.mod"]);

        reporter.Failed(new RunFailure(ExitCode.UsageError, usage.Message, null, usage, [], TimeSpan.Zero));
        reporter.Failed(new RunFailure(ExitCode.InternalError, "boom", new InvalidOperationException("boom"), null, [], TimeSpan.Zero));

        Assert.Contains("error Required argument missing: <input.mod>.", error.Output, StringComparison.Ordinal);
        Assert.Contains("Usage: " + UsageException.UsageLine, error.Output, StringComparison.Ordinal);
        Assert.Contains(UsageException.HelpHint, error.Output, StringComparison.Ordinal);
        Assert.Contains("error boom (exit 5)", error.Output, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: boom", error.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, console.Output);
    }

    /// <summary>Notes, information and detail print as lines; markup characters in them are printed, not read.</summary>
    [Fact]
    public void LinesKeepTheirText()
    {
        TestConsole console = new();
        using FancyReporter reporter = new(console, new TestConsole(), live: false);

        reporter.Emit(Event(LogEventLevel.Warning, "Sample [2] dropped"));
        reporter.Emit(Event(LogEventLevel.Information, "Emulating [ProTracker]"));
        reporter.Emit(Event(LogEventLevel.Debug, "detail [x]"));

        Assert.Equal(
            "note Sample [2] dropped\nEmulating [ProTracker]\ndetail [x]\n",
            console.Output.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    /// <summary>On a console that can redraw, the steps show in a live area with a step bar and a total bar.</summary>
    [Fact]
    public void LiveAreaShowsTheBars()
    {
        TestConsole console = new();
        console.Interactive();
        console.EmitAnsiSequences();
        using FancyReporter reporter = new(console, new TestConsole(), live: true);

        reporter.RunStarted(new RunInfo("song.mod", "dmf", "normal", [Read, Render]));
        reporter.Phase(Update(Read, PhaseEvent.Start, 0, 0, null));
        reporter.Phase(Update(Read, PhaseEvent.End, 10, 10, TimeSpan.FromSeconds(3)));
        reporter.Emit(Event(LogEventLevel.Warning, "a note while the bars run"));
        reporter.Phase(Update(Render, PhaseEvent.Progress, 250, 1000, TimeSpan.FromSeconds(3)));
        reporter.Phase(Update(Render, PhaseEvent.End, 1000, 1000, TimeSpan.Zero));
        reporter.Completed(new RunOutcome(ExitCode.Success, [new PhaseTiming(Read, TimeSpan.FromSeconds(0.5), 10)], TimeSpan.FromSeconds(4)));

        string output = console.Output;
        Assert.Contains("[2/2] Render WAV", output, StringComparison.Ordinal);
        Assert.True(output.Contains("25%", StringComparison.Ordinal), output.Replace("001b", "ESC", StringComparison.Ordinal));
        Assert.Contains("Total", output, StringComparison.Ordinal);
        Assert.Contains("about 3s left", output, StringComparison.Ordinal);
        Assert.Contains("a note while the bars run", output, StringComparison.Ordinal);
        Assert.Contains("━", output, StringComparison.Ordinal);
        Assert.Contains("Done", output, StringComparison.Ordinal);
    }

    /// <summary>Piped, each finished step is one static line, and nothing is drawn for progress.</summary>
    [Fact]
    public void PipedStepsAreStaticLines()
    {
        TestConsole console = new();
        using FancyReporter reporter = new(console, new TestConsole(), live: false);

        reporter.Phase(Update(Read, PhaseEvent.Start, 0, 0, null));
        reporter.Phase(Update(Read, PhaseEvent.Progress, 5, 10, null));
        reporter.Phase(Update(Read, PhaseEvent.End, 10, 10, null));

        Assert.Equal("✓ [1/2] Read MOD 1.5 s\n", console.Output.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    /// <summary>A console made from the terminal probe draws live only when the terminal can.</summary>
    [Fact]
    public void ProbeDecidesLive()
    {
        using FancyReporter piped = FancyReporter.Create(TextWriter.Null, TextWriter.Null, TerminalProbe.From(true, true, 100, _ => null));
        using FancyReporter dumb = FancyReporter.Create(TextWriter.Null, TextWriter.Null, TerminalProbe.From(false, false, null, v => v == "TERM" ? "dumb" : null));

        piped.Phase(Update(Read, PhaseEvent.End, 1, 1, null));
        dumb.Phase(Update(Read, PhaseEvent.End, 1, 1, null));
    }

    private static PhaseUpdate Update(PhaseInfo phase, PhaseEvent phaseEvent, long done, long total, TimeSpan? remaining) =>
        new(phase, 2, phaseEvent, done, total, TimeSpan.FromSeconds(1.5), remaining, TimeSpan.FromSeconds(1.5), remaining, false);

    private static LogEvent Event(LogEventLevel level, string text) =>
        new(DateTimeOffset.UnixEpoch, level, null, new MessageTemplateParser().Parse(text), []);
}
