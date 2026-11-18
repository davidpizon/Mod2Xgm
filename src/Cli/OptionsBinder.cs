using System.CommandLine;
using Mod2Xgm.Audio;
using Mod2Xgm.Mod;
using Mod2Xgm.Model.Window;
using Mod2Xgm.Optimizer;
using Mod2Xgm.Output;

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
        HashSet<int> requireDac = SampleList(parseResult.GetValue(definition.RequireDac), "--require-dac", errors);
        HashSet<int> requirePsg = SampleList(parseResult.GetValue(definition.RequirePsg), "--require-psg", errors);
        AddConflict(requireFm, "--require-fm", requireDac, "--require-dac", "a sample can be played by FM or the DAC, not both", errors);
        AddConflict(requireFm, "--require-fm", requirePsg, "--require-psg", "a sample can be played by FM or the PSG, not both", errors);
        AddConflict(requireDac, "--require-dac", requirePsg, "--require-psg", "a sample can be played by the DAC or the PSG, not both", errors);
        bool noDac = parseResult.GetValue(definition.NoDac);
        if (noDac && requireDac.Count > 0)
        {
            errors.Add($"--no-dac and --require-dac both given (sample{(requireDac.Count == 1 ? string.Empty : "s")} {string.Join(", ", requireDac.Order())}); --no-dac keeps every sample off the DAC.");
        }

        bool noCache = parseResult.GetValue(definition.NoCache);
        string? cacheDirectory = parseResult.GetValue(definition.CacheDir);
        if (noCache && cacheDirectory is not null)
        {
            errors.Add("--no-cache and --cache-dir both given; --no-cache doesn't use a cache folder.");
        }
        else if (cacheDirectory is { Length: 0 })
        {
            errors.Add("--cache-dir: a folder, please.");
        }

        AddConflict(ignoreSamples, "--ignore-samples", requireFm, "--require-fm", "an ignored sample isn't converted at all", errors);
        AddConflict(ignoreSamples, "--ignore-samples", requireDac, "--require-dac", "an ignored sample isn't converted at all", errors);
        AddConflict(ignoreSamples, "--ignore-samples", requirePsg, "--require-psg", "an ignored sample isn't converted at all", errors);

        long? maxPcmBytes = null;
        if (parseResult.GetValue(definition.MaxPcmBytes) is { } sizeText)
        {
            if (ValueParsers.TryParseByteSize(sizeText, out long size) is { } sizeError)
            {
                errors.Add($"--max-pcm-bytes: {sizeError}");
            }
            else
            {
                maxPcmBytes = size;
            }
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

        SongWindowRequest? window = Window(definition, parseResult, errors);

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
            Resampler = parseResult.GetValue(definition.Resampler) == "amiga" ? ResamplingMode.Amiga : ResamplingMode.Smooth,
            Mapping = parseResult.GetValue(definition.Mapping),
            IgnoreSamples = ignoreSamples,
            RequireFm = requireFm,
            RequireDac = requireDac,
            RequirePsg = requirePsg,
            NoDac = noDac,
            NoConsolidate = parseResult.GetValue(definition.NoConsolidate),
            MaxPcmBytes = maxPcmBytes,
            Quality = quality,
            TimeBudget = timeBudget,
            Window = window,
            Seed = parseResult.GetValue(definition.Seed),
            NoCache = noCache,
            CacheDirectory = cacheDirectory,
            ReserveSfxChannels = reserveSfx,
            RenderWav = parseResult.GetResult(definition.RenderWav) is not null,
            RenderWavPath = parseResult.GetValue(definition.RenderWav),
            InfoOnly = parseResult.GetValue(definition.Info),
            Force = parseResult.GetValue(definition.Force),
            Verbose = parseResult.GetValue(definition.Verbose),
            Progress = ProgressModeScan.Names[parseResult.GetValue(definition.Progress) ?? "fancy"],
        };

        return new Result(options, errors);
    }

    /// <summary>
    /// <c>--start</c>, <c>--end</c>, <c>--loop-from</c>, <c>--fade-out</c> and <c>--trim-silence</c> (docs/cli.md#converting-part-of-a-song):
    /// <c>null</c> when none is given. Checks what can be checked without the song; times are compared here, tracker
    /// positions once the song is played.
    /// </summary>
    private static SongWindowRequest? Window(CommandLineDefinition definition, ParseResult parseResult, List<string> errors)
    {
        SongPosition? start = Position(parseResult.GetValue(definition.Start), "--start", errors);
        SongPosition? end = Position(parseResult.GetValue(definition.End), "--end", errors);
        SongPosition? loopFrom = Position(parseResult.GetValue(definition.LoopFrom), "--loop-from", errors);
        double? fade = null;
        if (parseResult.GetValue(definition.FadeOut) is { } fadeText)
        {
            if (ValueParsers.TryParseSongTime(fadeText, out double seconds) is { } fadeError)
            {
                errors.Add($"--fade-out: {fadeError}");
            }
            else
            {
                fade = seconds;
            }
        }

        if (fade is not null && loopFrom is not null)
        {
            errors.Add("--fade-out and --loop-from both given; a fade-out ends the song, so it can't loop.");
        }

        double startSeconds = start is TimePosition { Seconds: var s } ? s : 0;
        bool startKnown = start is null or TimePosition;
        if (startKnown && end is TimePosition { Seconds: var endSeconds })
        {
            if (endSeconds <= startSeconds)
            {
                errors.Add($"--end {end.Display} isn't after --start {start?.Display ?? "0:00.000"}.");
            }
            else if (fade is { } length && length >= endSeconds - startSeconds)
            {
                errors.Add($"--fade-out {SongTime.Format(length)} is as long as the part converted or longer.");
            }
        }

        if (start is TrackerPosition && start == end)
        {
            errors.Add($"--start and --end are both {start.Display}, so nothing would be converted.");
        }

        if (loopFrom is TimePosition { Seconds: var loopSeconds })
        {
            if (startKnown && loopSeconds < startSeconds)
            {
                errors.Add($"--loop-from {loopFrom.Display} is before --start {start?.Display}.");
            }
            else if (end is TimePosition { Seconds: var stop } && loopSeconds >= stop)
            {
                errors.Add($"--loop-from {loopFrom.Display} isn't before --end {end.Display}.");
            }
        }

        TrimSilence trim = parseResult.GetValue(definition.TrimSilence) switch
        {
            "start" => TrimSilence.Start,
            "end" => TrimSilence.End,
            "both" => TrimSilence.Both,
            _ => TrimSilence.None,
        };
        return start is null && end is null && loopFrom is null && fade is null && trim == TrimSilence.None
            ? null
            : new SongWindowRequest(start, end, loopFrom, fade, trim);
    }

    private static SongPosition? Position(string? text, string option, List<string> errors)
    {
        if (text is null)
        {
            return null;
        }

        if (ValueParsers.TryParseSongPosition(text, out SongPosition? position) is { } error)
        {
            errors.Add($"{option}: {error}");
            return null;
        }

        return position;
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
            OutputFormat.Xgm => PcmTargets.Voices(PcmTarget.Xgm),
            OutputFormat.Xgm2 => PcmTargets.Voices(PcmTarget.Xgm2),
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
