namespace Mod2Xgm.Output;

/// <summary>Makes the reporter for an <see cref="OutputMode"/> (docs/cli-output.md).</summary>
internal static class Reporters
{
    /// <summary>
    /// The reporter for <paramref name="mode"/>, writing to <paramref name="output"/> (stdout) and <paramref name="error"/>
    /// (stderr). Until its task lands (plan T17.7), <c>fancy</c> prints the log lines the converter has
    /// always printed.
    /// </summary>
    public static IConversionReporter Create(OutputMode mode, TextWriter output, TextWriter error, TerminalProbe terminal)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(terminal);
        return mode switch
        {
            OutputMode.Silent => new SilentReporter(),
            OutputMode.Text => new TextReporter(output, error),
            OutputMode.Json => new JsonReporter(output),
            _ => new LogLinesReporter(output, error),
        };
    }
}
