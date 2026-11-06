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
    public void Find_WithArguments_ReturnsLastProgressMode(string expected, params string[] args)
    {
        // Act
        OutputMode mode = ProgressModeScan.Find(args);

        // Assert
        Assert.Equal(Enum.Parse<OutputMode>(expected), mode);
    }

    /// <summary>The probe reads NO_COLOR and TERM=dumb, and a dumb terminal isn't interactive.</summary>
    [Fact]
    public void From_WithNoColorAndDumbTerm_ReportsBothAndNotInteractive()
    {
        // Arrange
        Dictionary<string, string> environment = new() { ["NO_COLOR"] = "1", ["TERM"] = "dumb" };

        // Act
        TerminalProbe sut = TerminalProbe.From(false, false, 120, environment.GetValueOrDefault);

        // Assert
        Assert.True(sut.NoColor);
        Assert.True(sut.DumbTerminal);
        Assert.False(sut.Interactive);
    }

    /// <summary>A redirected stdout has no width and isn't interactive.</summary>
    [Fact]
    public void From_WithRedirectedOutput_HasNoWidthAndIsNotInteractive()
    {
        // Act
        TerminalProbe sut = TerminalProbe.From(true, false, 120, _ => null);

        // Assert
        Assert.Null(sut.Width);
        Assert.False(sut.Interactive);
    }

    /// <summary>A real terminal keeps its width, is interactive and has colour.</summary>
    [Fact]
    public void From_WithTerminal_KeepsWidthAndIsInteractive()
    {
        // Act
        TerminalProbe sut = TerminalProbe.From(false, true, 120, _ => null);

        // Assert
        Assert.Equal(120, sut.Width);
        Assert.True(sut.Interactive);
        Assert.False(sut.NoColor);
    }

    /// <summary>The probe of the test process can be read (it reads the real console and environment).</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void Current_InTestProcess_ReturnsProbe()
    {
        // Act
        TerminalProbe sut = TerminalProbe.Current();

        // Assert
        Assert.NotNull(sut);
    }

    /// <summary>The silent reporter takes every call without failing (it has no writer to print to).</summary>
    [Fact]
    public void SilentReporter_WhenGivenEveryCall_AcceptsThem()
    {
        // Act
        Exception? error = Record.Exception(() =>
        {
            using SilentReporter sut = new();
            sut.Emit(Event(LogEventLevel.Warning, "dropped"));
            sut.RunStarted(new RunInfo("song.mod", "dmf", "normal", []));
            sut.Phase(Update());
            sut.Completed(new RunOutcome(ExitCode.Success, [], TimeSpan.Zero));
            sut.Failed(new RunFailure(ExitCode.InternalError, "boom", null, null, [], TimeSpan.Zero));
        });

        // Assert
        Assert.Null(error);
    }

    /// <summary>Each mode gets its reporter.</summary>
    [Theory]
    [InlineData(nameof(OutputMode.Fancy), typeof(FancyReporter))]
    [InlineData(nameof(OutputMode.Text), typeof(TextReporter))]
    [InlineData(nameof(OutputMode.Json), typeof(JsonReporter))]
    [InlineData(nameof(OutputMode.Silent), typeof(SilentReporter))]
    public void Create_WithMode_ReturnsItsReporter(string mode, Type expected)
    {
        // Act
        using IConversionReporter reporter = Reporters.Create(Enum.Parse<OutputMode>(mode), TextWriter.Null, TextWriter.Null, TerminalProbe.From(true, true, null, _ => null));

        // Assert
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
    public void Kind_WithExitCode_ReturnsStableWord(string code, string kind)
    {
        // Arrange
        RunFailure sut = new(Enum.Parse<ExitCode>(code), "x", null, null, [], TimeSpan.Zero);

        // Act
        string actual = sut.Kind;

        // Assert
        Assert.Equal(kind, actual);
    }

    /// <summary>A phase update whose work isn't counted has no share done.</summary>
    [Fact]
    public void Fraction_WithNoTotal_ReturnsNull()
    {
        // Arrange
        PhaseUpdate sut = Update();

        // Act
        double? fraction = sut.Fraction;

        // Assert
        Assert.Null(fraction);
    }

    /// <summary>A phase update knows the share done, clamped to 1 when it overshoots.</summary>
    [Theory]
    [InlineData(1, 4, 0.25)]
    [InlineData(9, 4, 1.0)]
    public void Fraction_WithDoneAndTotal_ReturnsClampedShare(long done, long total, double expected)
    {
        // Arrange
        PhaseUpdate sut = Update() with { Done = done, Total = total };

        // Act
        double? fraction = sut.Fraction;

        // Assert
        Assert.Equal(expected, fraction);
    }

    /// <summary>A started phase with nothing done.</summary>
    private static PhaseUpdate Update() =>
        new(new PhaseInfo(1, "read", "Read MOD", "bytes"), 1, PhaseEvent.Start, 0, 0, TimeSpan.Zero, null, TimeSpan.Zero, null, false);

    /// <summary>A log event at <paramref name="level"/> whose message is <paramref name="text"/>.</summary>
    private static LogEvent Event(LogEventLevel level, string text) =>
        new(DateTimeOffset.UnixEpoch, level, null, new MessageTemplateParser().Parse(text), []);
}
