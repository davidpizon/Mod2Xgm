namespace Mod2Xgm.Output;

/// <summary>One step of a run.</summary>
/// <param name="Number">Its place, from 1.</param>
/// <param name="Id">Its short name, the same in every mode (<c>read</c>, <c>map</c>…; docs/cli-output.md#phases).</param>
/// <param name="Name">Its name for people (<c>Read MOD</c>).</param>
/// <param name="Unit">What its work is counted in (<c>rows</c>, <c>orders</c>…), or empty when it isn't counted.</param>
internal sealed record PhaseInfo(int Number, string Id, string Name, string Unit);

/// <summary>What a run converts and the steps it takes.</summary>
/// <param name="Input">The input file's full path.</param>
/// <param name="Format">The output format's name (<c>dmf</c>, <c>xgm</c>, <c>xgm2</c>).</param>
/// <param name="Quality">The quality's name (<c>fast</c>, <c>normal</c>, <c>best</c>).</param>
/// <param name="Phases">Every step, in order.</param>
/// <param name="Jobs">How many steps run at once: 1 for a conversion, <c>--jobs</c> for the harness's songs.</param>
internal sealed record RunInfo(string Input, string Format, string Quality, IReadOnlyList<PhaseInfo> Phases, int Jobs = 1)
{
    /// <summary>The <see cref="Format"/> of a harness run (docs/cli-output.md#the-harness): its steps are songs.</summary>
    public const string HarnessFormat = "harness";

    /// <summary>The converter's version (the informational version, without the commit hash).</summary>
    public static string Version { get; } = (typeof(RunInfo).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion ?? "0.0.0").Split('+')[0];
}

/// <summary>What happened to a phase.</summary>
internal enum PhaseEvent
{
    /// <summary>It began.</summary>
    Start,

    /// <summary>It did more work.</summary>
    Progress,

    /// <summary>It ended.</summary>
    End,
}

/// <summary>The state of the running phase and the time estimates at one moment.</summary>
/// <param name="Phase">The phase.</param>
/// <param name="Of">How many phases the run has.</param>
/// <param name="Event">What happened.</param>
/// <param name="Done">Work done so far, in <see cref="PhaseInfo.Unit"/>.</param>
/// <param name="Total">All the phase's work, or 0 when unknown.</param>
/// <param name="Elapsed">Time spent in the phase.</param>
/// <param name="Remaining">Estimated time left in the phase, or <c>null</c> when unknown.</param>
/// <param name="TotalElapsed">Time spent in the run.</param>
/// <param name="TotalRemaining">Estimated time left in the run, or <c>null</c> when nothing is known.</param>
/// <param name="TotalPartial">Some later phase has no estimate, so <see cref="TotalRemaining"/> is a lower bound.</param>
internal sealed record PhaseUpdate(
    PhaseInfo Phase,
    int Of,
    PhaseEvent Event,
    long Done,
    long Total,
    TimeSpan Elapsed,
    TimeSpan? Remaining,
    TimeSpan TotalElapsed,
    TimeSpan? TotalRemaining,
    bool TotalPartial)
{
    /// <summary>The share of the phase done, 0–1, or <c>null</c> when its work isn't counted.</summary>
    public double? Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : null;
}

/// <summary>How long one finished phase took.</summary>
/// <param name="Phase">The phase.</param>
/// <param name="Elapsed">Its time.</param>
/// <param name="Done">The work it did, in <see cref="PhaseInfo.Unit"/>.</param>
internal sealed record PhaseTiming(PhaseInfo Phase, TimeSpan Elapsed, long Done);

/// <summary>How a run that didn't fail ended.</summary>
/// <param name="Code">The exit code.</param>
/// <param name="Timings">Every finished phase's time, in order.</param>
/// <param name="Elapsed">The whole run's time.</param>
internal sealed record RunOutcome(ExitCode Code, IReadOnlyList<PhaseTiming> Timings, TimeSpan Elapsed);

/// <summary>How a run failed.</summary>
/// <param name="Code">The exit code.</param>
/// <param name="Message">What went wrong, for people.</param>
/// <param name="Exception">The unexpected exception (exit 5 only), whose stack trace is shown.</param>
/// <param name="Usage">The command-line problem (exit 1 from parsing or binding only).</param>
/// <param name="Timings">Every phase that finished before the failure.</param>
/// <param name="Elapsed">The whole run's time.</param>
internal sealed record RunFailure(
    ExitCode Code,
    string Message,
    Exception? Exception,
    UsageException? Usage,
    IReadOnlyList<PhaseTiming> Timings,
    TimeSpan Elapsed)
{
    /// <summary>
    /// The kind of failure, a stable word per exit code for <c>json</c> (docs/cli-output.md#json); the harness, whose
    /// exit codes differ, sets its own.
    /// </summary>
    public string Kind { get; init; } = KindOf(Code);

    private static string KindOf(ExitCode code) => code switch
    {
        ExitCode.UsageError => "usage",
        ExitCode.InputNotFound => "input-not-found",
        ExitCode.InvalidInput => "invalid-input",
        ExitCode.OutputNotWritable => "output-not-writable",
        ExitCode.DoesNotFit => "does-not-fit",
        _ => "internal",
    };
}
