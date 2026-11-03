using System.Globalization;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Mod2Xgm.Output;

/// <summary>
/// Prints the Serilog lines the converter has always printed (<c>INF …</c>, <c>WRN …</c>; docs/cli.md#console-output):
/// <c>Information</c> and <c>Debug</c> on stdout, notes and errors on stderr. Phases aren't shown.
/// </summary>
internal sealed class LogLinesReporter(TextWriter output, TextWriter error) : IConversionReporter
{
    /// <summary>Console line format (docs/cli.md#console-output); golden tests render the report with it.</summary>
    public const string OutputTemplate = "{Level:u3} {Message:lj}{NewLine}{Exception}";

    private readonly MessageTemplateTextFormatter _formatter = new(OutputTemplate, CultureInfo.InvariantCulture);
    private readonly Lock _lock = new();

    /// <inheritdoc/>
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        lock (_lock)
        {
            _formatter.Format(logEvent, logEvent.Level >= LogEventLevel.Warning ? error : output);
        }
    }

    /// <inheritdoc/>
    public void RunStarted(RunInfo run)
    {
    }

    /// <inheritdoc/>
    public void Phase(PhaseUpdate update)
    {
    }

    /// <inheritdoc/>
    public void Completed(RunOutcome outcome)
    {
    }

    /// <inheritdoc/>
    public void Failed(RunFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_lock)
        {
            if (failure.Exception is not null)
            {
                error.WriteLine("FTL Unexpected error");
                error.WriteLine(failure.Exception.ToString());
            }
            else if (failure.Usage is not null)
            {
                foreach (string problem in failure.Usage.Problems)
                {
                    error.WriteLine("ERR " + problem);
                }

                error.WriteLine("Usage: " + UsageException.UsageLine);
                error.WriteLine(UsageException.HelpHint);
            }
            else
            {
                error.WriteLine("ERR " + failure.Message);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        output.Flush();
        error.Flush();
    }
}
