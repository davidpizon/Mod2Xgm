using System.CommandLine;
using Mod2Xgm.Mod;

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
        Region inputRegion = ParseRegion(parseResult.GetValue(definition.InputRegion));
        Region outputRegion = ParseRegion(parseResult.GetValue(definition.OutputRegion));
        Quality quality = ParseQuality(parseResult.GetValue(definition.Quality));

        HashSet<int> ignoreSamples = SampleList(parseResult.GetValue(definition.IgnoreSamples), "--ignore-samples", errors);
        HashSet<int> requireFm = SampleList(parseResult.GetValue(definition.RequireFm), "--require-fm", errors);
        HashSet<int> requirePcm = SampleList(parseResult.GetValue(definition.RequirePcm), "--require-pcm", errors);
        AddConflict(requireFm, "--require-fm", requirePcm, "--require-pcm", "a sample can be FM or PCM, not both", errors);
        AddConflict(ignoreSamples, "--ignore-samples", requireFm, "--require-fm", "an ignored sample isn't converted at all", errors);
        AddConflict(ignoreSamples, "--ignore-samples", requirePcm, "--require-pcm", "an ignored sample isn't converted at all", errors);

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
            InputRegion = inputRegion,
            OutputRegion = outputRegion,
            Player = ParsePlayer(parseResult.GetValue(definition.Player)),
            Mapping = parseResult.GetValue(definition.Mapping),
            IgnoreSamples = ignoreSamples,
            RequireFm = requireFm,
            RequirePcm = requirePcm,
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

    private static HashSet<int> SampleList(string[]? lists, string optionName, List<string> errors)
    {
        HashSet<int> samples = [];
        foreach (string list in lists ?? [])
        {
            if (ValueParsers.TryParseSampleList(list, ConversionDefaults.MaxModSamples, samples) is { } error)
            {
                errors.Add($"{optionName}: {error}");
            }
        }

        return samples;
    }

    /// <summary>Reports samples that appear in two option lists that exclude each other.</summary>
    internal static void AddConflict(
        IReadOnlySet<int> first,
        string firstName,
        IReadOnlySet<int> second,
        string secondName,
        string reason,
        List<string> errors)
    {
        int[] both = [.. first.Intersect(second).Order()];
        if (both.Length > 0)
        {
            string samples = string.Join(", ", both);
            errors.Add($"{firstName} and {secondName} both list sample{(both.Length == 1 ? string.Empty : "s")} {samples}; {reason}.");
        }
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

    private static Region ParseRegion(string? text) => text == "ntsc" ? Region.Ntsc : Region.Pal;

    private static ModPlayer? ParsePlayer(string? text) => text switch
    {
        "protracker" => ModPlayer.ProTracker2,
        "ust" => ModPlayer.UltimateSoundtracker,
        _ => null,
    };

    private static Quality ParseQuality(string? text) => text switch
    {
        "fast" => Quality.Fast,
        "best" => Quality.Best,
        _ => Quality.Normal,
    };
}
