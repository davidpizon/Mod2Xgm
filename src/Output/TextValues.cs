using System.Globalization;
using System.Text;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Mod2Xgm.Output;

/// <summary>
/// How the <c>text</c> mode writes values (docs/cli-output.md#text): numbers in invariant culture, text quoted when it
/// holds a space, quote, <c>=</c> or <c>|</c> (or is empty; a bare path keeps its <c>\</c>), with <c>\"</c> and <c>\\</c> escapes.
/// </summary>
internal static class TextValues
{
    // Strings in a message are written as they are, not in quotes, as in the log lines (Program.ConsoleOutputTemplate).
    private static readonly MessageTemplateTextFormatter MessageFormatter = new("{Message:l}", CultureInfo.InvariantCulture);

    /// <summary>The message of <paramref name="logEvent"/>, with its strings unquoted.</summary>
    public static string Message(LogEvent logEvent)
    {
        using StringWriter text = new(CultureInfo.InvariantCulture);
        MessageFormatter.Format(logEvent, text);
        return text.ToString();
    }

    /// <summary><paramref name="value"/> as it appears after <c>key=</c>.</summary>
    public static string Format(object? value) => value switch
    {
        null => "-",
        string text => NeedsQuotes(text) ? Quote(text) : text,
        bool flag => flag ? "true" : "false",
        double number => number.ToString("0.##", CultureInfo.InvariantCulture),
        float number => number.ToString("0.##", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => Format(value.ToString()),
    };

    /// <summary><paramref name="text"/> in quotes, with <c>"</c> and <c>\</c> escaped and control characters shown as <c>·</c>.</summary>
    public static string Quote(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder quoted = new(text.Length + 2);
        quoted.Append('"');
        foreach (char c in text)
        {
            quoted.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                _ when char.IsControl(c) => "·",
                _ => c.ToString(),
            });
        }

        return quoted.Append('"').ToString();
    }

    /// <summary>A time in seconds with one decimal: <c>4.0s</c>.</summary>
    public static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

    /// <summary>
    /// A time estimate in whole seconds, rounded up: <c>12s</c>; <c>12s+</c> when it is a lower bound; <c>?</c> when it is
    /// unknown.
    /// </summary>
    public static string Eta(TimeSpan? remaining, bool partial) => remaining is { } r
        ? Math.Ceiling(Math.Max(0, r.TotalSeconds)).ToString("0", CultureInfo.InvariantCulture) + "s" + (partial ? "+" : string.Empty)
        : "?";

    /// <summary>A whole percentage: <c>40%</c>.</summary>
    public static string Percent(double fraction) => (Math.Floor(fraction * 100)).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static bool NeedsQuotes(string text) =>
        text.Length == 0 || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '=' or '|');
}
