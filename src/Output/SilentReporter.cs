using Serilog.Events;

namespace Mod2Xgm.Output;

/// <summary>
/// <c>--progress silent</c>: prints nothing at all, errors and crashes included; only the exit code says what happened
/// (David, ADR 0077).
/// </summary>
internal sealed class SilentReporter : IConversionReporter
{
    /// <inheritdoc/>
    public void Emit(LogEvent logEvent)
    {
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
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}
