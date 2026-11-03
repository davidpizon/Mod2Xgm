using Serilog.Core;

namespace Mod2Xgm.Output;

/// <summary>
/// Everything the converter shows its caller goes through one reporter, chosen by <c>--progress</c>
/// (docs/cli-output.md). Log events arrive through <see cref="ILogEventSink.Emit"/>: conversion notes at
/// <c>Warning</c>, detail at <c>Debug</c>, report rows and other lines at <c>Information</c>. Phases, the outcome and
/// failures arrive through the other methods.
/// </summary>
internal interface IConversionReporter : ILogEventSink, IDisposable
{
    /// <summary>The run's input and steps are known: the phase list for "step n of N".</summary>
    void RunStarted(RunInfo run);

    /// <summary>A phase began, made progress or ended (<see cref="PhaseUpdate.Event"/> says which).</summary>
    void Phase(PhaseUpdate update);

    /// <summary>The run ended with <paramref name="outcome"/>'s exit code: printed last.</summary>
    void Completed(RunOutcome outcome);

    /// <summary>The run failed: printed last, in place of <see cref="Completed"/>.</summary>
    void Failed(RunFailure failure);
}
