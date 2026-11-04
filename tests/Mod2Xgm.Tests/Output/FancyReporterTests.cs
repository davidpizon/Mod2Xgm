using Mod2Xgm.Output;
using Serilog.Events;
using Serilog.Parsing;
using Spectre.Console.Testing;

namespace Mod2Xgm.Tests.Output;

/// <summary>Tests of <see cref="FancyReporter"/>, <c>--progress fancy</c>, the default (plan T17.7, docs/cli-output.md#fancy). Whole runs are in <see cref="ReporterGoldenTests"/>.</summary>
public sealed class FancyReporterTests
{
    private static readonly PhaseInfo Read = new(1, "read", "Read MOD", "bytes");
    private static readonly PhaseInfo Render = new(2, "render", "Render WAV", "ticks");

    /// <summary>A usage error names the problem, the usage line and the help hint on stderr, and nothing on stdout.</summary>
    [Fact]
    public void Failed_WithUsageError_WritesProblemUsageAndHintToStderr()
    {
        // Arrange
        TestConsole console = new();
        TestConsole error = new();
        using FancyReporter sut = new(console, error, live: false);
        UsageException usage = new(["Required argument missing: <input.mod>."], ["input.mod"]);

        // Act
        sut.Failed(new RunFailure(ExitCode.UsageError, usage.Message, null, usage, [], TimeSpan.Zero));

        // Assert
        Assert.Contains("error Required argument missing: <input.mod>.", error.Output, StringComparison.Ordinal);
        Assert.Contains("Usage: " + UsageException.UsageLine, error.Output, StringComparison.Ordinal);
        Assert.Contains(UsageException.HelpHint, error.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, console.Output);
    }

    /// <summary>A crash writes its message, exit code and stack on stderr.</summary>
    [Fact]
    public void Failed_WithCrash_WritesMessageAndStackToStderr()
    {
        // Arrange
        TestConsole error = new();
        using FancyReporter sut = new(new TestConsole(), error, live: false);

        // Act
        sut.Failed(new RunFailure(ExitCode.InternalError, "boom", new InvalidOperationException("boom"), null, [], TimeSpan.Zero));

        // Assert
        Assert.Contains("error boom (exit 5)", error.Output, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: boom", error.Output, StringComparison.Ordinal);
    }

    /// <summary>Notes, information and detail print as lines; markup characters in them are printed, not read.</summary>
    [Fact]
    public void Emit_WithMarkupCharacters_PrintsTextAsIs()
    {
        // Arrange
        TestConsole console = new();
        using FancyReporter sut = new(console, new TestConsole(), live: false);

        // Act
        sut.Emit(Event(LogEventLevel.Warning, "Sample [2] dropped"));
        sut.Emit(Event(LogEventLevel.Information, "Emulating [ProTracker]"));
        sut.Emit(Event(LogEventLevel.Debug, "detail [x]"));

        // Assert
        Assert.Equal(
            "note Sample [2] dropped\nEmulating [ProTracker]\ndetail [x]\n",
            console.Output.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    /// <summary>On a console that can redraw, the steps show in a live area with a step bar and a total bar, and notes print above it.</summary>
    [Fact]
    public void Phase_OnLiveConsole_DrawsStepAndTotalBars()
    {
        // Arrange
        TestConsole console = LiveConsole();
        using FancyReporter sut = new(console, new TestConsole(), live: true);

        // Act
        sut.RunStarted(new RunInfo("song.mod", "dmf", "normal", [Read, Render]));
        sut.Phase(Update(Read, PhaseEvent.Start, 0, 0, null));
        sut.Phase(Update(Read, PhaseEvent.End, 10, 10, TimeSpan.FromSeconds(3)));
        sut.Emit(Event(LogEventLevel.Warning, "a note while the bars run"));
        sut.Phase(Update(Render, PhaseEvent.Progress, 250, 1000, TimeSpan.FromSeconds(3)));
        sut.Phase(Update(Render, PhaseEvent.End, 1000, 1000, TimeSpan.Zero));
        sut.Completed(new RunOutcome(ExitCode.Success, [new PhaseTiming(Read, TimeSpan.FromSeconds(0.5), 10)], TimeSpan.FromSeconds(4)));

        // Assert
        string output = console.Output;
        Assert.Contains("[2/2] Render WAV", output, StringComparison.Ordinal);
        Assert.True(output.Contains("25%", StringComparison.Ordinal), output.Replace("001b", "ESC", StringComparison.Ordinal));
        Assert.Contains("Total", output, StringComparison.Ordinal);
        Assert.Contains("about 3s left", output, StringComparison.Ordinal);
        Assert.Contains("a note while the bars run", output, StringComparison.Ordinal);
        Assert.Contains("━", output, StringComparison.Ordinal);
        Assert.Contains("Done", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A harness run names its songs and jobs in the header, and the live area shows a row for every song running at
    /// once, then closes when the last one ends, whichever it is.
    /// </summary>
    [Fact]
    public void Phase_WithParallelSongs_ShowsRowPerRunningSong()
    {
        // Arrange
        PhaseInfo first = new(1, "song:first", "first", string.Empty);
        PhaseInfo second = new(2, "song:second", "second", string.Empty);
        TestConsole console = LiveConsole();
        using FancyReporter sut = new(console, new TestConsole(), live: true);

        // Act
        sut.RunStarted(new RunInfo("corpus", RunInfo.HarnessFormat, "normal", [first, second], Jobs: 2));
        sut.Phase(Update(second, PhaseEvent.Start, 0, 0, null));
        sut.Phase(Update(first, PhaseEvent.Start, 0, 0, TimeSpan.FromSeconds(4)));
        sut.Phase(Update(second, PhaseEvent.End, 0, 0, TimeSpan.Zero));
        sut.Phase(Update(first, PhaseEvent.End, 0, 0, TimeSpan.Zero));
        sut.Emit(Event(LogEventLevel.Information, "after the bars"));

        // Assert
        string output = console.Output;
        Assert.Contains("mod2xgm harness ", output, StringComparison.Ordinal);
        Assert.Contains("2 songs, 2 at a time", output, StringComparison.Ordinal);
        Assert.Contains("[1/2] first", output, StringComparison.Ordinal);
        Assert.Contains("[2/2] second", output, StringComparison.Ordinal);
        Assert.Contains("after the bars", output, StringComparison.Ordinal);
    }

    /// <summary>Piped, each finished step is one static line, and nothing is drawn for progress.</summary>
    [Fact]
    public void Phase_WhenPiped_WritesOneStaticLinePerFinishedStep()
    {
        // Arrange
        TestConsole console = new();
        using FancyReporter sut = new(console, new TestConsole(), live: false);

        // Act
        sut.Phase(Update(Read, PhaseEvent.Start, 0, 0, null));
        sut.Phase(Update(Read, PhaseEvent.Progress, 5, 10, null));
        sut.Phase(Update(Read, PhaseEvent.End, 10, 10, null));

        // Assert
        Assert.Equal("✓ [1/2] Read MOD 1.5 s\n", console.Output.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    /// <summary>A reporter made from the terminal probe works for a piped and a dumb terminal alike.</summary>
    [Theory]
    [InlineData(true, true, 100, null)]
    [InlineData(false, false, null, "dumb")]
    public void Create_FromProbe_AcceptsPhases(bool redirected, bool interactive, int? width, string? term)
    {
        // Arrange
        TerminalProbe probe = TerminalProbe.From(redirected, interactive, width, v => v == "TERM" ? term : null);
        using FancyReporter sut = FancyReporter.Create(TextWriter.Null, TextWriter.Null, probe);

        // Act
        Exception? error = Record.Exception(() => sut.Phase(Update(Read, PhaseEvent.End, 1, 1, null)));

        // Assert
        Assert.Null(error);
    }

    /// <summary>A test console that can redraw and writes escape codes, like a real terminal.</summary>
    private static TestConsole LiveConsole()
    {
        TestConsole console = new();
        console.Interactive();
        console.EmitAnsiSequences();
        return console;
    }

    /// <summary>A phase update with 1.5 s elapsed and the given estimate.</summary>
    private static PhaseUpdate Update(PhaseInfo phase, PhaseEvent phaseEvent, long done, long total, TimeSpan? remaining) =>
        new(phase, 2, phaseEvent, done, total, TimeSpan.FromSeconds(1.5), remaining, TimeSpan.FromSeconds(1.5), remaining, false);

    /// <summary>A log event at <paramref name="level"/> whose message is <paramref name="text"/>.</summary>
    private static LogEvent Event(LogEventLevel level, string text) =>
        new(DateTimeOffset.UnixEpoch, level, null, new MessageTemplateParser().Parse(text), []);
}
