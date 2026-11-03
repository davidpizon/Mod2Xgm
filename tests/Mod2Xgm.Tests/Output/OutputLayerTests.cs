using Mod2Xgm.Output;
using Serilog.Events;
using Serilog.Parsing;

namespace Mod2Xgm.Tests.Output;

/// <summary>Tests of the <c>--progress</c> pre-scan, the terminal probe and the reporters of plan T17.2.</summary>
public sealed class OutputLayerTests
{
    /// <summary>The pre-scan finds every spelling of <c>--progress</c>, the last one wins, and <c>--</c> ends options.</summary>
    [Theory]
    [InlineData(nameof(OutputMode.Fancy))]
    [InlineData(nameof(OutputMode.Json), "song.mod", "--progress", "json")]
    [InlineData(nameof(OutputMode.Text), "--progress=text", "song.mod")]
    [InlineData(nameof(OutputMode.Silent), "--progress:silent")]
    [InlineData(nameof(OutputMode.Silent), "--progress", "json", "--progress", "silent")]
    [InlineData(nameof(OutputMode.Fancy), "--progress", "auto")]
    [InlineData(nameof(OutputMode.Fancy), "--progress")]
    [InlineData(nameof(OutputMode.Fancy), "--progressive", "json")]
    [InlineData(nameof(OutputMode.Fancy), "--", "--progress", "json")]
    public void ScanFindsTheMode(string expected, params string[] args) =>
        Assert.Equal(Enum.Parse<OutputMode>(expected), ProgressModeScan.Find(args));

    /// <summary>The probe reads NO_COLOR and TERM=dumb, and a redirected stdout has no width and isn't interactive.</summary>
    [Fact]
    public void ProbeReadsTheEnvironment()
    {
        Dictionary<string, string> environment = new() { ["NO_COLOR"] = "1", ["TERM"] = "dumb" };

        TerminalProbe dumb = TerminalProbe.From(false, false, 120, name => environment.GetValueOrDefault(name));
        TerminalProbe piped = TerminalProbe.From(true, false, 120, _ => null);
        TerminalProbe terminal = TerminalProbe.From(false, true, 120, _ => null);

        Assert.True(dumb.NoColor);
        Assert.True(dumb.DumbTerminal);
        Assert.False(dumb.Interactive);
        Assert.Null(piped.Width);
        Assert.False(piped.Interactive);
        Assert.Equal(120, terminal.Width);
        Assert.True(terminal.Interactive);
        Assert.False(terminal.NoColor);
    }

    /// <summary>The probe of the test process can be read.</summary>
    [Fact]
    public void ProbeOfThisProcess() => Assert.NotNull(TerminalProbe.Current());

    /// <summary>The silent reporter takes every call and prints nothing.</summary>
    [Fact]
    public void SilentTakesEverything()
    {
        using SilentReporter reporter = new();

        reporter.Emit(Event(LogEventLevel.Warning, "dropped"));
        reporter.RunStarted(new RunInfo("song.mod", "dmf", "normal", []));
        reporter.Phase(Update());
        reporter.Completed(new RunOutcome(ExitCode.Success, [], TimeSpan.Zero));
        reporter.Failed(new RunFailure(ExitCode.InternalError, "boom", null, null, [], TimeSpan.Zero));
    }

    /// <summary>Each mode gets its reporter.</summary>
    [Theory]
    [InlineData(nameof(OutputMode.Fancy), typeof(FancyReporter))]
    [InlineData(nameof(OutputMode.Text), typeof(TextReporter))]
    [InlineData(nameof(OutputMode.Json), typeof(JsonReporter))]
    [InlineData(nameof(OutputMode.Silent), typeof(SilentReporter))]
    public void FactoryPicksTheReporter(string mode, Type expected)
    {
        using IConversionReporter reporter = Reporters.Create(Enum.Parse<OutputMode>(mode), TextWriter.Null, TextWriter.Null, TerminalProbe.From(true, true, null, _ => null));
        Assert.IsType(expected, reporter);
    }

    /// <summary>Each failure's exit code has a stable kind word for <c>json</c>.</summary>
    [Theory]
    [InlineData(nameof(ExitCode.UsageError), "usage")]
    [InlineData(nameof(ExitCode.InputNotFound), "input-not-found")]
    [InlineData(nameof(ExitCode.InvalidInput), "invalid-input")]
    [InlineData(nameof(ExitCode.OutputNotWritable), "output-not-writable")]
    [InlineData(nameof(ExitCode.DoesNotFit), "does-not-fit")]
    [InlineData(nameof(ExitCode.InternalError), "internal")]
    public void FailureKinds(string code, string kind) =>
        Assert.Equal(kind, new RunFailure(Enum.Parse<ExitCode>(code), "x", null, null, [], TimeSpan.Zero).Kind);

    /// <summary>A phase update knows the share done, and none when the work isn't counted.</summary>
    [Fact]
    public void UpdateFraction()
    {
        Assert.Null(Update().Fraction);
        Assert.Equal(0.25, Update() with { Done = 1, Total = 4 } is { Fraction: { } f } ? f : -1);
        Assert.Equal(1.0, (Update() with { Done = 9, Total = 4 }).Fraction);
    }

    /// <summary>A started phase with nothing done.</summary>
    private static PhaseUpdate Update() =>
        new(new PhaseInfo(1, "read", "Read MOD", "bytes"), 1, PhaseEvent.Start, 0, 0, TimeSpan.Zero, null, TimeSpan.Zero, null, false);

    /// <summary>A log event at <paramref name="level"/> whose message is <paramref name="text"/>.</summary>
    private static LogEvent Event(LogEventLevel level, string text) =>
        new(DateTimeOffset.UnixEpoch, level, null, new MessageTemplateParser().Parse(text), []);
}
