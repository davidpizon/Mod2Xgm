using System.Globalization;
using Mod2Xgm.Cli;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Mod2Xgm;

internal static class Program
{
    /// <summary>Minimum log level; raised to Debug by <c>--verbose</c>.</summary>
    internal static LoggingLevelSwitch LevelSwitch { get; } = new(); // starts at Information

    /// <summary>Console line format (docs/cli.md#console-output); golden tests render the report with it.</summary>
    internal const string ConsoleOutputTemplate = "{Level:u3} {Message:lj}{NewLine}{Exception}";

    internal static int Main(string[] args) => Run(args, static a => CommandLineDefinition.Invoke(a, ConvertMod));

    /// <summary>
    /// Sets up logging and runs <paramref name="body"/>. A <see cref="ConversionException"/> prints its message and
    /// exits with its code; any other exception is unexpected and exits with <see cref="ExitCode.InternalError"/>.
    /// Separated from <see cref="Main"/> so the error paths are testable.
    /// </summary>
    internal static int Run(string[] args, Func<string[], ExitCode> body)
    {
        LevelSwitch.MinimumLevel = LogEventLevel.Information;
        Log.Logger = CreateLogger(LevelSwitch);

        try
        {
            return (int)body(args);
        }
        catch (ConversionException ex)
        {
            Log.Error("{Message}", ex.Message);
            return (int)ex.Code;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Unexpected error");
            return (int)ExitCode.InternalError;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    internal static ILogger CreateLogger(LoggingLevelSwitch levelSwitch) =>
        new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .WriteTo.Console(
                outputTemplate: ConsoleOutputTemplate,
                formatProvider: CultureInfo.InvariantCulture,
                standardErrorFromLevel: LogEventLevel.Warning)
            .CreateLogger();

    internal static ExitCode ConvertMod(ConversionOptions options)
    {
        LevelSwitch.MinimumLevel = options.Verbose ? LogEventLevel.Debug : LogEventLevel.Information;
        return ConversionCommand.Execute(options, Log.Logger);
    }
}
