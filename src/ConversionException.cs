namespace Mod2Xgm;

/// <summary>
/// An expected failure with a specific exit code (bad input, unwritable output, …). <see cref="Program.Run(string[], Func{string[], ExitCode})"/>
/// prints the message without a stack trace and exits with <see cref="Code"/>. A command line that can't be used is a
/// <see cref="UsageException"/>.
/// </summary>
internal class ConversionException(ExitCode code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>The exit code the run ends with.</summary>
    public ExitCode Code { get; } = code;
}
