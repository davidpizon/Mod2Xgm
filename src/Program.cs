using System.Diagnostics;
using System.Text;
using Mod2Xgm.Cli;
using Mod2Xgm.Optimizer;
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

    /// <summary>
    /// The Serilog line format of the report (<c>INF …</c>, <c>WRN …</c>; docs/cli.md#console-output): the harness logs
    /// with it, and the report's golden tests render with it.
    /// </summary>
    internal const string ConsoleOutputTemplate = "{Level:u3} {Message:lj}{NewLine}{Exception}";

    /// <summary>How the console was attached when the run started (docs/cli-output.md#the-orchestration-layer-srcoutput).</summary>
    private static TerminalProbe Terminal { get; set; } = TerminalProbe.From(true, true, null, static _ => null);

    /// <summary>The steps of the current run, reported to its reporter (docs/cli-output.md#phases).</summary>
    private static IConversionProgress Progress { get; set; } = NoProgress.Instance;

    internal static int Main(string[] args) => Run(args, static a => CommandLineDefinition.Invoke(a, ConvertMod));

    /// <summary>Runs <paramref name="body"/> on the process's console; see <see cref="Run(string[], Func{string[], ExitCode}, TextWriter, TextWriter, TerminalProbe)"/>.</summary>
    internal static int Run(string[] args, Func<string[], ExitCode> body)
    {
        TerminalProbe terminal = TerminalProbe.Current();
        using TextWriter? output = terminal.OutputRedirected ? Utf8(Console.OpenStandardOutput()) : null;
        using TextWriter? error = terminal.ErrorRedirected ? Utf8(Console.OpenStandardError()) : null;
        return Run(args, body, output ?? Console.Out, error ?? Console.Error, terminal);
    }

    /// <summary>
    /// A redirected stream is written in UTF-8 without a byte-order mark (docs/cli-output.md#streams): the console's own
    /// encoding there is the OEM code page on Windows, which can't hold names like "é" or the drawing characters.
    /// The console's code page itself is left alone.
    /// </summary>
    private static StreamWriter Utf8(Stream stream) => new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };

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
        PhaseTracker tracker = new(reporter, TimeProvider.System, new EtaEstimator());
        Progress = tracker;

        try
        {
            ExitCode code = body(args);
            tracker.Estimator.History?.Save();
            Log.CloseAndFlush();
            reporter.Completed(new RunOutcome(code, tracker.Timings, clock.Elapsed));
            return (int)code;
        }
        catch (ConversionException ex)
        {
            tracker.Estimator.History?.Save();
            Log.CloseAndFlush();
            reporter.Failed(new RunFailure(ex.Code, ex.Message, null, ex as UsageException, tracker.Timings, clock.Elapsed));
            return (int)ex.Code;
        }
        catch (Exception ex)
        {
            tracker.Estimator.History?.Save();
            Log.CloseAndFlush();
            reporter.Failed(new RunFailure(ExitCode.InternalError, ex.Message, ex, null, tracker.Timings, clock.Elapsed));
            return (int)ExitCode.InternalError;
        }
        finally
        {
            // The reporter is disposed with this run; a later ConvertMod (tests call it directly) reports nowhere.
            Progress = NoProgress.Instance;
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
        if (Progress is PhaseTracker tracker && !options.NoCache)
        {
            // Earlier runs' timings, at the root of the candidate cache folder (docs/cli-output.md#time-estimate).
            tracker.Estimator.History = TimingHistory.Load(options.CacheDirectory ?? CandidateCache.DefaultRoot, Log.Logger);
        }

        return ConversionCommand.Execute(options, Log.Logger, null, Progress);
    }
}
