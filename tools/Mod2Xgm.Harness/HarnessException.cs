namespace Mod2Xgm.Harness;

/// <summary>An expected failure of a harness run (a missing tool, an unreadable WAV): the message says what to do.</summary>
internal sealed class HarnessException : Exception
{
    /// <summary>A failure described by <paramref name="message"/>.</summary>
    public HarnessException(string message)
        : base(message)
    {
    }

    /// <summary>A failure described by <paramref name="message"/>, caused by <paramref name="innerException"/>.</summary>
    public HarnessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// The failure's kind for <c>--progress json</c> and <c>text</c> (docs/cli-output.md#the-harness): <c>cannot-run</c>
    /// (a tool or input is missing), or <c>usage</c> for a bad command line.
    /// </summary>
    public string Kind { get; init; } = "cannot-run";

    /// <summary>A bad command line, described by <paramref name="message"/>.</summary>
    public static HarnessException Usage(string message) => new(message) { Kind = "usage" };
}
