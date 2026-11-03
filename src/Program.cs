using System.Diagnostics;
using Mod2Xgm.Cli;
using Mod2Xgm.Output;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Mod2Xgm;

/// <summary>The entry point: picks the reporter for <c>--progress</c>, runs the command line and reports how it ended.</summary>
internal static class Program
{
    /// <summary>Minimum log level; raised to Debug by <c>--verbose</c>.</summary>
    internal static LoggingLevelSwitch LevelSwitch { get; } = new(); // starts at Information

    /// <summary>Console line format (docs/cli.md#console-output); golden tests render the report with it.</summary>
    internal const string ConsoleOutputTemplate = LogLinesReporter.OutputTemplate;

    /// <summary>How the console was attached when the run started (docs/cli-output.md#the-orchestration-layer-srcoutput-proposed).</summary>
    private static TerminalProbe Terminal { get; set; } = TerminalProbe.From(true, true, null, static _ => null);

    internal static int Main(string[] args) => Run(args, static a => CommandLineDefinition.Invoke(a, ConvertMod));

    /// <summary>Runs <paramref name="body"/> on the process's console; see <see cref="Run(string[], Func{string[], ExitCode}, TextWriter, TextWriter, TerminalProbe)"/>.</summary>
    internal static int Run(string[] args, Func<string[], ExitCode> body) =>
        Run(args, body, Console.Out, Console.Error, TerminalProbe.Current());

    /// <summary>
    /// Makes the reporter <c>--progress</c> asks for (found before parsing, so parse errors use it too), logs through it
    /// and runs <paramref name="body"/>. A <see cref="ConversionException"/> is reported as a failure with its code; any
    /// other exception is unexpected and exits with <see cref="ExitCode.InternalError"/>. Never reads stdin.
    /// </summary>
    internal static int Run(string[] args, Func<string[], ExitCode> body, TextWriter output, TextWriter error, TerminalProbe terminal)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(body);
        LevelSwitch.MinimumLevel = LogEventLevel.Information;
        Terminal = terminal;
        Stopwatch clock = Stopwatch.StartNew();
        using IConversionReporter reporter = Reporters.Create(ProgressModeScan.Find(args), output, error, terminal);
        Log.Logger = CreateLogger(LevelSwitch, reporter);

        try
        {
            ExitCode code = body(args);
            Log.CloseAndFlush();
            reporter.Completed(new RunOutcome(code, [], clock.Elapsed));
            return (int)code;
        }
        catch (ConversionException ex)
        {
            Log.CloseAndFlush();
            reporter.Failed(new RunFailure(ex.Code, ex.Message, null, ex as UsageException, [], clock.Elapsed));
            return (int)ex.Code;
        }
        catch (Exception ex)
        {
            Log.CloseAndFlush();
            reporter.Failed(new RunFailure(ExitCode.InternalError, ex.Message, ex, null, [], clock.Elapsed));
            return (int)ExitCode.InternalError;
        }
    }

    /// <summary>A logger at <paramref name="levelSwitch"/>'s level writing only to <paramref name="sink"/> (the reporter).</summary>
    internal static Logger CreateLogger(LoggingLevelSwitch levelSwitch, ILogEventSink sink) =>
        new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .WriteTo.Sink(sink)
            .CreateLogger();

    internal static ExitCode ConvertMod(ConversionOptions options)
    {
        LevelSwitch.MinimumLevel = options.Verbose ? LogEventLevel.Debug : LogEventLevel.Information;
        Log.Debug(
            "Terminal: stdout {Stdout:l}, stderr {Stderr:l}, width {Width}, NO_COLOR {NoColor}, TERM=dumb {Dumb}",
            Terminal.OutputRedirected ? "redirected" : "terminal",
            Terminal.ErrorRedirected ? "redirected" : "terminal",
            Terminal.Width,
            Terminal.NoColor,
            Terminal.DumbTerminal);
        return ConversionCommand.Execute(options, Log.Logger);
    }
}
