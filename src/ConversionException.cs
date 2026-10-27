namespace Mod2Xgm;

/// <summary>
/// An expected failure with a specific exit code (bad input, unwritable output, …). <see cref="Program.Run"/>
/// prints the message without a stack trace and exits with <see cref="Code"/>.
/// </summary>
internal sealed class ConversionException(ExitCode code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public ExitCode Code { get; } = code;
}
