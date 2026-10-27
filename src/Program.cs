using System.Globalization;
using Mod2Xgm.Cli;
using Serilog;
using Serilog.Events;

namespace Mod2Xgm;

internal static class Program
{
    internal static int Main(string[] args) => Run(args, static a => CommandLineDefinition.Invoke(a, ConvertMod));

    /// <summary>
    /// Sets up logging, runs <paramref name="body"/>, and turns an unexpected exception into
    /// <see cref="ExitCode.InternalError"/>. Separated from <see cref="Main"/> so the error path is testable.
    /// </summary>
    internal static int Run(string[] args, Func<string[], ExitCode> body)
    {
        Log.Logger = CreateLogger();

        try
        {
            return (int)body(args);
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

    // Placeholder until the conversion pipeline exists (plan M2+).
    internal static ExitCode ConvertMod(ConversionOptions options)
    {
        Log.Information("Mod2Xgm: {Input} -> {Format}", options.InputPath, options.Format);
        return ExitCode.Success;
    }
}
