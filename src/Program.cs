using System.Globalization;
using Serilog;
using Serilog.Events;

namespace Mod2Xgm;

internal static class Program
{
    private static int Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(
                outputTemplate: "{Level:u3} {Message:lj}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture,
                standardErrorFromLevel: LogEventLevel.Warning)
            .CreateLogger();

        try
        {
            Log.Information("Mod2Xgm: {ArgumentCount} argument(s)", args.Length);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Unexpected error");
            return 5;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
