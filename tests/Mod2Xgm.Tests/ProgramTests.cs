using Mod2Xgm.Cli;
using Mod2Xgm.Output;
using Serilog.Core;
using Serilog.Events;

namespace Mod2Xgm.Tests;

/// <summary>Tests for <see cref="Program"/>: exit codes, the streams each mode writes, and the log level.</summary>
[Collection(ProgramState.Name)]
[Trait("Category", "Integration")]
public sealed class ProgramTests
{
    /// <summary>The runs below, with their exit codes, for the silent and stdin tests.</summary>
    public static TheoryData<string, int> EveryExit() => new()
    {
        { "success", 0 },
        { "usage", 1 },
        { "missing-input", 1 },
        { "not-found", 2 },
        { "conversion-exception", 4 },
        { "crash", 5 },
    };

    /// <summary>Every run of <see cref="EveryExit"/> in every <c>--progress</c> mode.</summary>
    public static TheoryData<string, string, int> EveryExitInEveryMode()
    {
        TheoryData<string, string, int> data = [];
        string[] modes = ["fancy", "text", "json", "silent"];
        foreach (string mode in modes)
        {
            data.Add("success", mode, 0);
            data.Add("usage", mode, 1);
            data.Add("missing-input", mode, 1);
            data.Add("not-found", mode, 2);
            data.Add("conversion-exception", mode, 4);
            data.Add("crash", mode, 5);
        }

        return data;
    }

    /// <summary>An existing input converts and exits with Success.</summary>
    [Fact]
    public void Main_WithExistingInput_ReturnsSuccess()
    {
        // Arrange
        using TempDirectory temp = new();
        string input = temp.CreateFile("song.mod");

        // Act
        int code = Program.Main([input]);

        // Assert
        Assert.Equal((int)ExitCode.Success, code);
    }

    /// <summary>A path that doesn't exist exits with InputNotFound.</summary>
    [Fact]
    public void Main_WithMissingInput_ReturnsInputNotFound()
    {
        // Arrange
        using TempDirectory temp = new();
        string input = temp.Combine("missing.mod");

        // Act
        int code = Program.Main([input]);

        // Assert
        Assert.Equal((int)ExitCode.InputNotFound, code);
    }

    /// <summary>No arguments at all is a usage error.</summary>
    [Fact]
    public void Main_WithoutArguments_ReturnsUsageError()
    {
        // Act
        int code = Program.Main([]);

        // Assert
        Assert.Equal((int)ExitCode.UsageError, code);
    }

    /// <summary>The body's own exit code is passed through.</summary>
    [Fact]
    public void Run_WhenBodyReturnsCode_ReturnsThatCode()
    {
        // Act
        int code = Program.Run([], _ => ExitCode.InvalidInput);

        // Assert
        Assert.Equal((int)ExitCode.InvalidInput, code);
    }

    /// <summary>A <see cref="ConversionException"/> exits with its own code.</summary>
    [Fact]
    public void Run_WhenBodyThrowsConversionException_ReturnsItsCode()
    {
        // Act
        int code = Program.Run([], _ => throw new ConversionException(ExitCode.OutputNotWritable, "exists"));

        // Assert
        Assert.Equal((int)ExitCode.OutputNotWritable, code);
    }

    /// <summary>Any other exception is a bug and exits with InternalError.</summary>
    [Fact]
    public void Run_WhenBodyThrowsUnexpectedException_ReturnsInternalError()
    {
        // Act
        int code = Program.Run([], _ => throw new InvalidOperationException("boom"));

        // Assert
        Assert.Equal((int)ExitCode.InternalError, code);
    }

    /// <summary><c>--progress silent</c> prints nothing on either stream, whatever the exit code, a crash included (ADR 0077).</summary>
    [Theory]
    [MemberData(nameof(EveryExit))]
    public void Run_WithSilentProgress_PrintsNothing(string name, int expected)
    {
        // Act
        (int code, string output, string error) = RunCase(name, "silent");

        // Assert
        Assert.Equal(expected, code);
        Assert.Equal(string.Empty, output);
        Assert.Equal(string.Empty, error);
    }

    /// <summary>No mode ever reads stdin: a reader that throws on any read stays untouched (docs/cli-output.md#never-interactive).</summary>
    [Theory]
    [MemberData(nameof(EveryExitInEveryMode))]
    public void Run_InAnyMode_NeverReadsStdin(string name, string mode, int expected)
    {
        // Arrange
        TextReader stdin = Console.In;
        Console.SetIn(new ThrowingReader());
        try
        {
            // Act
            (int code, _, _) = RunCase(name, mode);

            // Assert
            Assert.Equal(expected, code);
        }
        finally
        {
            Console.SetIn(stdin);
        }
    }

    /// <summary>A usage error prints its problem and the usage line instead of the whole help.</summary>
    [Fact]
    public void Run_WithUsageError_PrintsProblemAndUsageLine()
    {
        // Act
        (int code, string output, string error) = RunCase("missing-input", "text");

        // Assert
        Assert.Equal((int)ExitCode.UsageError, code);
        Assert.Equal(string.Empty, output);
        Assert.Contains("Required argument missing: <input.mod>.", error, StringComparison.Ordinal);
        Assert.Contains(UsageException.UsageLine, error, StringComparison.Ordinal);
        Assert.DoesNotContain("--render-wav", error, StringComparison.Ordinal);
    }

    /// <summary>A crash prints its stack trace on stderr when the mode isn't silent.</summary>
    [Fact]
    public void Run_WhenBodyCrashes_PrintsStackTraceOnStderr()
    {
        // Act
        (int code, _, string error) = RunCase("crash", "fancy");

        // Assert
        Assert.Equal((int)ExitCode.InternalError, code);
        Assert.Contains("boom", error, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), error, StringComparison.Ordinal);
    }

    /// <summary>The logger is disabled at Debug while the switch is at Information (its default).</summary>
    [Fact]
    public void CreateLogger_WithSwitchAtInformation_DisablesDebug()
    {
        // Arrange
        LoggingLevelSwitch levelSwitch = new();

        // Act
        Serilog.ILogger sut = Program.CreateLogger(levelSwitch, new CollectingSink());

        // Assert
        Assert.False(sut.IsEnabled(LogEventLevel.Debug));
    }

    /// <summary>Lowering the switch after the logger is made enables Debug: the logger follows the switch.</summary>
    [Fact]
    public void CreateLogger_WhenSwitchLoweredLater_EnablesDebug()
    {
        // Arrange
        LoggingLevelSwitch levelSwitch = new();
        Serilog.ILogger sut = Program.CreateLogger(levelSwitch, new CollectingSink());

        // Act
        levelSwitch.MinimumLevel = LogEventLevel.Debug;

        // Assert
        Assert.True(sut.IsEnabled(LogEventLevel.Debug));
    }

    /// <summary><c>--verbose</c> sets the level switch to Debug; without it, Information.</summary>
    [Theory]
    [InlineData(true, LogEventLevel.Debug)]
    [InlineData(false, LogEventLevel.Information)]
    public void ConvertMod_WithVerboseOption_SetsLogLevel(bool verbose, LogEventLevel expected)
    {
        // Arrange
        using TempDirectory temp = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod"), Verbose = verbose };

        // Act
        Program.ConvertMod(options);

        // Assert
        Assert.Equal(expected, Program.LevelSwitch.MinimumLevel);
    }

    /// <summary>Runs one of <see cref="EveryExit"/>'s cases with <paramref name="mode"/> on string writers.</summary>
    private static (int Code, string Output, string Error) RunCase(string name, string mode)
    {
        using TempDirectory temp = new();
        string[] progress = ["--progress", mode];
        (string[] Args, Func<string[], ExitCode> Body) run = name switch
        {
            "success" => ([temp.CreateFile("song.mod"), .. progress], Convert),
            "usage" => (["song.mod", "--bogus", .. progress], Convert),
            "missing-input" => (progress, Convert),
            "not-found" => ([temp.Combine("missing.mod"), .. progress], Convert),
            "conversion-exception" => (progress, _ => throw new ConversionException(ExitCode.OutputNotWritable, "exists")),
            _ => (progress, _ => throw new InvalidOperationException("boom")),
        };
        using StringWriter output = new();
        using StringWriter error = new();
        int code = Program.Run(run.Args, run.Body, output, error, TerminalProbe.From(true, true, null, _ => null));
        return (code, output.ToString(), error.ToString());

        static ExitCode Convert(string[] args) => CommandLineDefinition.Invoke(args, Program.ConvertMod, TextWriter.Null, TextWriter.Null);
    }

    /// <summary>A TextReader that fails the test if anything reads from it.</summary>
    private sealed class ThrowingReader : TextReader
    {
        /// <inheritdoc/>
        public override int Peek() => throw new InvalidOperationException("stdin was read");

        /// <inheritdoc/>
        public override int Read() => throw new InvalidOperationException("stdin was read");
    }
}
