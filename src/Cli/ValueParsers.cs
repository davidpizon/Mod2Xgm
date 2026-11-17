using System.Globalization;
using System.Text.RegularExpressions;
using Mod2Xgm.Model.Window;

namespace Mod2Xgm.Cli;

/// <summary>
/// Parsers for option values whose syntax System.CommandLine doesn't provide. Each returns <c>null</c> on success
/// or an error message.
/// </summary>
internal static partial class ValueParsers
{
    /// <summary>
    /// Parses a sample list such as <c>3,7,12-14</c> or <c>0x1F</c> (hex with <c>0x</c>) into sample numbers
    /// between 1 and <paramref name="maxSample"/>.
    /// </summary>
    public static string? TryParseSampleList(string text, int maxSample, ISet<int> samples)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Sample list is empty.";
        }

        foreach (string rawPart in text.Split(','))
        {
            string part = rawPart.Trim();
            if (part.Length == 0)
            {
                return $"Sample list '{text}' has an empty entry.";
            }

            int dash = part.IndexOf('-', StringComparison.Ordinal);
            string firstText = dash < 0 ? part : part[..dash].Trim();
            string lastText = dash < 0 ? part : part[(dash + 1)..].Trim();

            if (!TryParseSampleNumber(firstText, out int first) || !TryParseSampleNumber(lastText, out int last))
            {
                return $"'{part}' is not a sample number or range (examples: 7, 12-14, 0x1F).";
            }

            if (first > last)
            {
                return $"Range '{part}' runs backwards.";
            }

            if (first < 1 || last > maxSample)
            {
                return $"Sample '{part}' is out of range; MOD samples are numbered 1 to {maxSample}.";
            }

            for (int sample = first; sample <= last; sample++)
            {
                samples.Add(sample);
            }
        }

        return null;
    }

    /// <summary>Parses a byte size such as <c>131072</c>, <c>128K</c> or <c>1M</c> (binary units).</summary>
    public static string? TryParseByteSize(string text, out long bytes)
    {
        bytes = 0;
        string value = text.Trim();
        long multiplier = 1;

        if (value.Length > 0)
        {
            char unit = char.ToUpperInvariant(value[^1]);
            if (unit == 'K')
            {
                multiplier = 1024;
            }
            else if (unit == 'M')
            {
                multiplier = 1024 * 1024;
            }

            if (multiplier != 1)
            {
                value = value[..^1];
            }
        }

        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long number) || number <= 0)
        {
            return $"'{text}' is not a size; use a positive number of bytes, optionally with K or M (e.g. 128K).";
        }

        if (number > long.MaxValue / multiplier)
        {
            return $"'{text}' is too large.";
        }

        bytes = number * multiplier;
        return null;
    }

    /// <summary>Parses a duration such as <c>90</c> or <c>90s</c>, <c>5m</c>, <c>1h</c>.</summary>
    public static string? TryParseDuration(string text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        string value = text.Trim();
        int secondsPerUnit = 1;

        if (value.Length > 0 && char.IsLetter(value[^1]))
        {
            secondsPerUnit = char.ToLowerInvariant(value[^1]) switch
            {
                's' => 1,
                'm' => 60,
                'h' => 3600,
                _ => 0,
            };
            value = value[..^1];
        }

        if (secondsPerUnit == 0
            || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            || number <= 0
            || number > int.MaxValue / secondsPerUnit)
        {
            return $"'{text}' is not a duration; use a positive number with s, m or h (e.g. 90s, 5m).";
        }

        duration = TimeSpan.FromSeconds((long)number * secondsPerUnit);
        return null;
    }

    /// <summary>Highest order a MOD has (128 orders, 0-based).</summary>
    private const int MaxOrder = 127;

    /// <summary>Highest row in a MOD pattern (64 rows, 0-based).</summary>
    private const int MaxRow = 63;

    /// <summary>
    /// Parses a place in the song (docs/cli.md#converting-part-of-a-song): a time (<c>125</c>, <c>125.5s</c>, <c>2:05</c>,
    /// <c>2:05.250</c>, <c>1:02:05</c>) or a tracker position (<c>o11r25</c>, <c>o3</c> for its row 0), both 0-based.
    /// </summary>
    public static string? TryParseSongPosition(string text, out SongPosition? position)
    {
        position = null;
        string value = text.Trim();
        Match tracker = TrackerPattern().Match(value);
        if (tracker.Success)
        {
            int order = int.Parse(tracker.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            int row = tracker.Groups[2].Success ? int.Parse(tracker.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture) : 0;
            if (order > MaxOrder || row > MaxRow)
            {
                return $"'{text}' is outside a MOD: orders are 0-{MaxOrder} and rows 0-{MaxRow}.";
            }

            position = new TrackerPosition(order, row);
            return null;
        }

        if (ParseTime(value) is not { } seconds)
        {
            return $"'{text}' is not a time or position; use seconds (125, 125.5s), m:ss (2:05, 2:05.250) or an order and row (o11r25).";
        }

        position = new TimePosition(seconds);
        return null;
    }

    /// <summary>Parses a length of time above 0: seconds (<c>4</c>, <c>2.5s</c>) or <c>m:ss</c> (<c>0:04</c>).</summary>
    public static string? TryParseSongTime(string text, out double seconds)
    {
        seconds = 0;
        if (ParseTime(text.Trim()) is not { } value || value <= 0)
        {
            return $"'{text}' is not a length of time; use seconds above 0 (4, 2.5s) or m:ss (0:04).";
        }

        seconds = value;
        return null;
    }

    /// <summary>Seconds from <c>s</c>, <c>s.fff</c>, an <c>s</c> suffix, <c>m:ss[.fff]</c> or <c>h:mm:ss[.fff]</c>; <c>null</c> otherwise.</summary>
    private static double? ParseTime(string value)
    {
        Match match = TimePattern().Match(value);
        if (!match.Success)
        {
            return null;
        }

        double seconds = double.Parse(match.Groups["s"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        double minutes = match.Groups["m"].Success ? double.Parse(match.Groups["m"].Value, NumberStyles.None, CultureInfo.InvariantCulture) : 0;
        double hours = match.Groups["h"].Success ? double.Parse(match.Groups["h"].Value, NumberStyles.None, CultureInfo.InvariantCulture) : 0;
        return (hours * 3600) + (minutes * 60) + seconds;
    }

    /// <summary>A tracker position: <c>o</c> and an order, then optionally <c>r</c> and a row.</summary>
    [GeneratedRegex(@"^[oO]([0-9]{1,3})(?:[rR]([0-9]{1,2}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex TrackerPattern();

    /// <summary>Seconds with an optional <c>s</c>, <c>m:ss</c> or <c>h:mm:ss</c>, each with optional decimals.</summary>
    [GeneratedRegex(@"^(?:(?:(?<h>[0-9]+):(?<m>[0-5][0-9])|(?<m>[0-9]+)):(?<s>[0-5][0-9](?:\.[0-9]+)?)|(?<s>[0-9]+(?:\.[0-9]+)?)s?)$", RegexOptions.CultureInvariant)]
    private static partial Regex TimePattern();

    private static bool TryParseSampleNumber(string text, out int number)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out number);
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }
}
