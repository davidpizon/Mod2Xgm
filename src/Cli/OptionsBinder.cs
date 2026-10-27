using System.CommandLine;

namespace Mod2Xgm.Cli;

/// <summary>
/// Turns a successful <see cref="ParseResult"/> into <see cref="ConversionOptions"/>, converting text values and
/// checking rules that span options. Collects every error instead of stopping at the first.
/// </summary>
internal static class OptionsBinder
{
    internal sealed record Result(ConversionOptions? Options, IReadOnlyList<string> Errors);

    public static Result Bind(CommandLineDefinition definition, ParseResult parseResult)
    {
        List<string> errors = [];

        string input = parseResult.GetValue(definition.Input) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            errors.Add("An input MOD file is required.");
        }

        OutputFormat format = ParseFormat(parseResult.GetValue(definition.Format));
        Region region = parseResult.GetValue(definition.Region) == "ntsc" ? Region.Ntsc : Region.Pal;
        Quality quality = ParseQuality(parseResult.GetValue(definition.Quality));

        HashSet<int> ignoreSamples = [];
        foreach (string list in parseResult.GetValue(definition.IgnoreSamples) ?? [])
        {
            if (ValueParsers.TryParseSampleList(list, ConversionDefaults.MaxModSamples, ignoreSamples) is { } error)
            {
                errors.Add($"--ignore-samples: {error}");
            }
        }

        long maxPcmBytes = ConversionDefaults.MaxPcmBytes;
        if (parseResult.GetValue(definition.MaxPcmBytes) is { } sizeText
            && ValueParsers.TryParseByteSize(sizeText, out maxPcmBytes) is { } sizeError)
        {
            errors.Add($"--max-pcm-bytes: {sizeError}");
        }

        TimeSpan? timeBudget = null;
        if (parseResult.GetValue(definition.TimeBudget) is { } durationText)
        {
            if (ValueParsers.TryParseDuration(durationText, out TimeSpan duration) is { } durationError)
            {
                errors.Add($"--time-budget: {durationError}");
            }
            else
            {
                timeBudget = duration;
            }
        }

        int reserveSfx = parseResult.GetValue(definition.ReserveSfxChannels);
        if (ReserveSfxError(format, reserveSfx) is { } reserveError)
        {
            errors.Add($"--reserve-sfx-channels: {reserveError}");
        }

        if (errors.Count > 0)
        {
            return new Result(null, errors);
        }

        ConversionOptions options = new()
        {
            InputPath = input,
            Format = format,
            OutputPath = parseResult.GetValue(definition.Output),
            Region = region,
            Mapping = parseResult.GetValue(definition.Mapping),
            IgnoreSamples = ignoreSamples,
            NoConsolidate = parseResult.GetValue(definition.NoConsolidate),
            MaxPcmBytes = maxPcmBytes,
            Quality = quality,
            TimeBudget = timeBudget,
            Seed = parseResult.GetValue(definition.Seed),
            NoCache = parseResult.GetValue(definition.NoCache),
            ReserveSfxChannels = reserveSfx,
            InfoOnly = parseResult.GetValue(definition.Info),
            Force = parseResult.GetValue(definition.Force),
            Verbose = parseResult.GetValue(definition.Verbose),
        };

        return new Result(options, errors);
    }

    internal static string? ReserveSfxError(OutputFormat format, int channels)
    {
        int max = format switch
        {
            OutputFormat.Xgm => ConversionDefaults.XgmPcmChannels,
            OutputFormat.Xgm2 => ConversionDefaults.Xgm2PcmChannels,
            _ => 0,
        };

        if (channels < 0)
        {
            return "must not be negative.";
        }

        if (channels > max)
        {
            return format == OutputFormat.Dmf
                ? "only applies to --format xgm or xgm2."
                : $"{format.ToString().ToLowerInvariant()} has {max} PCM channels; reserve at most {max}.";
        }

        return null;
    }

    private static OutputFormat ParseFormat(string? text) => text switch
    {
        "xgm" => OutputFormat.Xgm,
        "xgm2" => OutputFormat.Xgm2,
        _ => OutputFormat.Dmf,
    };

    private static Quality ParseQuality(string? text) => text switch
    {
        "fast" => Quality.Fast,
        "best" => Quality.Best,
        _ => Quality.Normal,
    };
}
