using System.Globalization;

namespace Mod2Xgm.Cli;

/// <summary>
/// Parsers for option values whose syntax System.CommandLine doesn't provide. Each returns <c>null</c> on success
/// or an error message.
/// </summary>
internal static class ValueParsers
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

    private static bool TryParseSampleNumber(string text, out int number)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out number);
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }
}
