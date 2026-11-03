using Mod2Xgm.Cli;
using Mod2Xgm.Output;
using Serilog.Core;
using Serilog.Events;

namespace Mod2Xgm.Tests;

[Collection(ProgramState.Name)]
public sealed class ProgramTests
{
    [Fact]
    public void MainReturnsSuccessForAnExistingInput()
    {
        using TempDirectory temp = new();
        Assert.Equal((int)ExitCode.Success, Program.Main([temp.CreateFile("song.mod")]));
    }

    [Fact]
    public void MainReturnsInputNotFoundForAMissingInput()
    {
        using TempDirectory temp = new();
        Assert.Equal((int)ExitCode.InputNotFound, Program.Main([temp.Combine("missing.mod")]));
    }

    [Fact]
    public void MainReturnsUsageErrorWithoutArguments() =>
        Assert.Equal((int)ExitCode.UsageError, Program.Main([]));

    [Fact]
    public void RunReturnsTheBodysExitCode() =>
        Assert.Equal((int)ExitCode.InvalidInput, Program.Run([], _ => ExitCode.InvalidInput));

    [Fact]
    public void RunReturnsTheCodeOfAConversionException()
    {
        Assert.Equal(
            (int)ExitCode.OutputNotWritable,
            Program.Run([], _ => throw new ConversionException(ExitCode.OutputNotWritable, "exists")));
    }

    [Fact]
    public void RunTurnsAnUnexpectedExceptionIntoInternalError()
    {
        Assert.Equal(
            (int)ExitCode.InternalError,
            Program.Run([], _ => throw new InvalidOperationException("boom")));
    }

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

    /// <summary><c>--progress silent</c> prints nothing on either stream, whatever the exit code, a crash included (ADR 0077).</summary>
    [Theory]
    [MemberData(nameof(EveryExit))]
    public void SilentPrintsNothing(string name, int expected)
    {
        (int code, string output, string error) = RunCase(name, "silent");

        Assert.Equal(expected, code);
        Assert.Equal(string.Empty, output);
        Assert.Equal(string.Empty, error);
    }

    /// <summary>No mode ever reads stdin: a reader that throws on any read stays untouched (docs/cli-output.md#never-interactive).</summary>
    [Theory]
    [MemberData(nameof(EveryExit))]
    public void NoModeReadsStdin(string name, int expected)
    {
        TextReader stdin = Console.In;
        Console.SetIn(new ThrowingReader());
        try
        {
            foreach (string mode in new[] { "fancy", "text", "json", "silent" })
            {
                Assert.Equal(expected, RunCase(name, mode).Code);
            }
        }
        finally
        {
            Console.SetIn(stdin);
        }
    }

    /// <summary>A usage error prints its problem and the usage line instead of the whole help.</summary>
    [Fact]
    public void UsageErrorsPrintTheUsageLine()
    {
        (int code, string output, string error) = RunCase("missing-input", "text");

        Assert.Equal((int)ExitCode.UsageError, code);
        Assert.Equal(string.Empty, output);
        Assert.Contains("Required argument missing: <input.mod>.", error, StringComparison.Ordinal);
        Assert.Contains(UsageException.UsageLine, error, StringComparison.Ordinal);
        Assert.DoesNotContain("--render-wav", error, StringComparison.Ordinal);
    }

    /// <summary>A crash prints its stack trace on stderr when the mode isn't silent.</summary>
    [Fact]
    public void CrashesPrintTheStackTrace()
    {
        (int code, _, string error) = RunCase("crash", "fancy");

        Assert.Equal((int)ExitCode.InternalError, code);
        Assert.Contains("boom", error, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), error, StringComparison.Ordinal);
    }

    /// <summary>A TextReader that fails the test if anything reads from it.</summary>
    private sealed class ThrowingReader : TextReader
    {
        /// <inheritdoc/>
        public override int Peek() => throw new InvalidOperationException("stdin was read");

        /// <inheritdoc/>
        public override int Read() => throw new InvalidOperationException("stdin was read");
    }

    [Fact]
    public void CreateLoggerFollowsTheLevelSwitch()
    {
        LoggingLevelSwitch levelSwitch = new(); // starts at Information
        Serilog.ILogger logger = Program.CreateLogger(levelSwitch, new CollectingSink());

        Assert.False(logger.IsEnabled(LogEventLevel.Debug));
        levelSwitch.MinimumLevel = LogEventLevel.Debug;
        Assert.True(logger.IsEnabled(LogEventLevel.Debug));
    }

    [Theory]
    [InlineData(true, LogEventLevel.Debug)]
    [InlineData(false, LogEventLevel.Information)]
    public void VerboseSetsTheLogLevel(bool verbose, LogEventLevel expected)
    {
        using TempDirectory temp = new();

        Program.ConvertMod(new ConversionOptions { InputPath = temp.CreateFile("song.mod"), Verbose = verbose });

        Assert.Equal(expected, Program.LevelSwitch.MinimumLevel);
    }

    [Fact]
    public void ConversionExceptionKeepsItsInnerException()
    {
        IOException inner = new("disk");
        ConversionException error = new(ExitCode.InputNotFound, "unreadable", inner);

        Assert.Same(inner, error.InnerException);
        Assert.Equal(ExitCode.InputNotFound, error.Code);
    }
}
