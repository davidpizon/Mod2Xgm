using System.Globalization;
using Mod2Xgm.Cli;
using Serilog;
using Serilog.Events;

namespace Mod2Xgm;

internal static class Program
{
    internal static int Main(string[] args) => Run(args, static a => CommandLineDefinition.Invoke(a, ConvertMod));

    /// <summary>
    /// Sets up logging and runs <paramref name="body"/>. A <see cref="ConversionException"/> prints its message and
    /// exits with its code; any other exception is unexpected and exits with <see cref="ExitCode.InternalError"/>.
    /// Separated from <see cref="Main"/> so the error paths are testable.
    /// </summary>
    internal static int Run(string[] args, Func<string[], ExitCode> body)
    {
        Log.Logger = CreateLogger();

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

    internal static Serilog.ILogger CreateLogger() =>
        new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(
                outputTemplate: "{Level:u3} {Message:lj}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture,
                standardErrorFromLevel: LogEventLevel.Warning)
            .CreateLogger();

    internal static ExitCode ConvertMod(ConversionOptions options) => ConversionCommand.Execute(options);
}
