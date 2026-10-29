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
}
