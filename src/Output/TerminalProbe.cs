namespace Mod2Xgm.Output;

/// <summary>
/// How the console is attached, read once at start-up. It never changes the output mode (David: no auto mode, ADR 0077);
/// it only tells the <c>fancy</c> console what it can draw, and is logged at <c>Debug</c>.
/// </summary>
/// <param name="OutputRedirected">stdout goes to a file or a pipe, not a terminal.</param>
/// <param name="ErrorRedirected">stderr goes to a file or a pipe, not a terminal.</param>
/// <param name="Width">The terminal's width in columns, or <c>null</c> when it has none.</param>
/// <param name="NoColor">The <c>NO_COLOR</c> environment variable is set (https://no-color.org).</param>
/// <param name="DumbTerminal"><c>TERM=dumb</c>: the terminal can't move the cursor.</param>
internal sealed record TerminalProbe(bool OutputRedirected, bool ErrorRedirected, int? Width, bool NoColor, bool DumbTerminal)
{
    /// <summary>Whether a live display can redraw in place: stdout is a terminal that can move the cursor.</summary>
    public bool Interactive => !OutputRedirected && !DumbTerminal;

    /// <summary>Reads the current process's console and environment.</summary>
    public static TerminalProbe Current() => From(
        Console.IsOutputRedirected,
        Console.IsErrorRedirected,
        ConsoleWidth(),
        Environment.GetEnvironmentVariable);

    /// <summary>A probe from the given facts and environment, for tests.</summary>
    public static TerminalProbe From(bool outputRedirected, bool errorRedirected, int? width, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return new TerminalProbe(
            outputRedirected,
            errorRedirected,
            outputRedirected ? null : width,
            !string.IsNullOrEmpty(environment("NO_COLOR")),
            string.Equals(environment("TERM"), "dumb", StringComparison.Ordinal));
    }

    private static int? ConsoleWidth()
    {
        try
        {
            return Console.IsOutputRedirected ? null : Console.WindowWidth;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
