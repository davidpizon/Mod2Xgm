namespace Mod2Xgm.Output;

/// <summary>Makes the reporter for an <see cref="OutputMode"/> (docs/cli-output.md).</summary>
internal static class Reporters
{
    /// <summary>
    /// The reporter for <paramref name="mode"/>, writing to <paramref name="output"/> (stdout) and <paramref name="error"/>
    /// (stderr). Until their tasks land (plan T17.3, T17.4, T17.7), <c>fancy</c>, <c>text</c> and <c>json</c> print the
    /// log lines the converter has always printed.
    /// </summary>
    public static IConversionReporter Create(OutputMode mode, TextWriter output, TextWriter error, TerminalProbe terminal)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(terminal);
        return mode switch
        {
            OutputMode.Silent => new SilentReporter(),
            _ => new LogLinesReporter(output, error),
        };
    }
}
