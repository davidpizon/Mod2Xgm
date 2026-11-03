namespace Mod2Xgm;

/// <summary>
/// A command line that can't be used (exit 1): every problem found and the arguments that are missing. The converter
/// never asks for what is missing (docs/cli-output.md#never-interactive); the reporter prints the problems with
/// <see cref="UsageLine"/>.
/// </summary>
internal sealed class UsageException : ConversionException
{
    /// <summary>The usage line printed with every usage error.</summary>
    public const string UsageLine = "mod2xgm <input.mod> [options]";

    /// <summary>Where to find every option, printed after <see cref="UsageLine"/>.</summary>
    public const string HelpHint = "Run 'mod2xgm --help' for every option.";

    /// <summary>A usage error listing <paramref name="problems"/>, of which <paramref name="missing"/> name missing arguments.</summary>
    public UsageException(IReadOnlyList<string> problems, IReadOnlyList<string> missing)
        : base(ExitCode.UsageError, string.Join(Environment.NewLine, problems))
    {
        Problems = problems;
        Missing = missing;
    }

    /// <summary>Every problem, one sentence each.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>The names of required arguments that weren't given (<c>input.mod</c>).</summary>
    public IReadOnlyList<string> Missing { get; }
}
