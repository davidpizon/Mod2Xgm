using Mod2Xgm.Cli;
using Mod2Xgm.Dmf;
using Mod2Xgm.Mapping;
using Mod2Xgm.Mod;
using Mod2Xgm.Model;
using Serilog;

namespace Mod2Xgm;

/// <summary>
/// Runs one conversion: checks the paths, reads the MOD, prints the report, maps the song with the simple mapper and
/// writes the DMF (docs/architecture.md). Every stage reports its lossy decisions to the conversion notes.
/// </summary>
internal static class ConversionCommand
{
    /// <summary>Largest input accepted. Real MODs are a few MB at most; anything bigger isn't a MOD.</summary>
    public const long MaxInputBytes = 64L * 1024 * 1024;

    /// <summary>Runs one conversion, logging to the global Serilog logger.</summary>
    public static ExitCode Execute(ConversionOptions options) => Execute(options, Log.Logger);

    /// <summary>Runs one conversion, logging to <paramref name="logger"/>; expected failures throw <see cref="ConversionException"/>.</summary>
    public static ExitCode Execute(ConversionOptions options, ILogger logger)
    {
        LogOptions(options, logger);

        string input = Path.GetFullPath(options.InputPath);
        CheckInput(input);

        string output = OutputPaths.Resolve(options);
        if (!options.InfoOnly)
        {
            CheckOutput(input, output, options.Force);
        }

        ConversionNotes notes = new(logger);
        ModModule module = ModReader.Read(ReadInput(input), notes, options.Player);

        // Report sections 1–2 come right after reading, before anything that can fail later (docs/cli.md#console-report).
        ModReport.LogMetadata(logger, module, Path.GetFileName(input));
        LogPlayer(options, module, logger);
        logger.Debug(
            "Read {Format} MOD ({Tag}): {Channels} channels, {Patterns} patterns, {SongLength} orders",
            module.Format,
            module.Tag,
            module.ChannelCount,
            module.Patterns.Count,
            module.SongLength);

        SampleUsage usage = SampleUsageScanner.Scan(module, notes);
        logger.Debug(
            "Usage scan: {Rows} rows played, {Used} samples used, loops back: {LoopsBack}",
            usage.RowsPlayed,
            usage.Samples.Count(s => s.Status == SampleStatus.Used),
            usage.LoopsBack);
        ModReport.LogSamples(logger, module, usage, options.IgnoreSamples);
        CheckSampleNumbers(options, module);
        CheckFormat(options, logger);
        LogQuality(options, logger);

        Song song = ModToSong.Convert(module, options.AmigaClockHz, notes);
        GenesisPlan plan = new SimpleMapper().Map(song, MappingOptions.From(options), notes);
        logger.Debug(
            "Mapped {Orders} orders: {FmPatches} FM patches, {DacSamples} DAC samples",
            plan.Patterns.Count,
            plan.FmPatches.Count,
            plan.DacSamples.Count);
        DmfTimingPlan timing = DmfTimingPlanner.Plan(plan, options.AmigaTickRateScale, options.OutputRegion, notes);
        logger.Debug(
            "DMF timing: {TickRateHz} Hz, {TicksPerSourceTick} DMF ticks per MOD tick, length off by {LengthError:P2}",
            timing.Timing.TickRateHz,
            timing.TicksPerSourceTick,
            timing.LengthError);
        DmfSong dmf = DmfSongBuilder.Build(plan, song, timing, new DmfBuildSettings(options.AmigaClockHz, options.Resampler), notes);

        // Report section 3 comes after mapping, before the file is written (docs/cli.md#console-report).
        DmfReport.Log(logger, dmf, song);
        byte[] bytes = DmfWriter.Write(dmf.File, notes);
        if (!options.InfoOnly)
        {
            WriteOutput(output, bytes, options.Force);
        }

        logger.Information(
            "{Input} -> {Output} ({NoteCount} conversion notes)",
            Path.GetFileName(input),
            options.InfoOnly ? "(--info: no file written)" : output,
            notes.Count);
        return ExitCode.Success;
    }

    /// <summary>
    /// Only DMF can be written yet (XGM2 is plan M10, XGM M11). Runs after report sections 1–2, so they are printed for
    /// every format; <c>--format xgm2</c> still prints its extension note (docs/cli.md#console-output, ADR 0045).
    /// </summary>
    private static void CheckFormat(ConversionOptions options, ILogger logger)
    {
        if (options.Format == OutputFormat.Dmf)
        {
            return;
        }

        if (options.Format == OutputFormat.Xgm2)
        {
            logger.Information(Xgm2ExtensionNote);
        }

        string name = options.Format == OutputFormat.Xgm2 ? "xgm2" : "xgm";
        throw new ConversionException(
            ExitCode.UsageError,
            $"--format {name} isn't available yet: this version writes DMF only (XGM2 comes with plan M10, XGM with M11).");
    }

    /// <summary>The line every <c>--format xgm2</c> run prints, verbatim (docs/cli.md#console-output).</summary>
    internal const string Xgm2ExtensionNote = "NOTE: SGDK expects XGM2 files to use the .XGM file extension.";

    /// <summary>The optimizer (plan M9) isn't built yet, so every quality uses the simple mapper; a run asking for more says so (ADR 0045).</summary>
    private static void LogQuality(ConversionOptions options, ILogger logger)
    {
        if (options.Quality != Quality.Fast)
        {
            logger.Information(
                "--quality {Quality:l}: the optimizer isn't built yet (plan M9), so the simple mapper is used",
                options.Quality == Quality.Best ? "best" : "normal");
        }
    }

    /// <summary>Writes the file; without <c>--force</c> it is never created over one that appeared since the checks.</summary>
    private static void WriteOutput(string output, byte[] bytes, bool force)
    {
        try
        {
            using FileStream stream = new(output, force ? FileMode.Create : FileMode.CreateNew, FileAccess.Write);
            stream.Write(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConversionException(ExitCode.OutputNotWritable, $"Output file can't be written: {output} ({ex.Message})", ex);
        }
    }

    /// <summary>Says whose rules the file is read and played by, and why (David, 2026-10-27; docs/cli.md#players).</summary>
    private static void LogPlayer(ConversionOptions options, ModModule module, ILogger logger)
    {
        string reason = options.Player is not null
            ? "chosen with --player"
            : module.Format == ModFormat.Soundtracker15
                ? "the default for 15-sample files; --player protracker changes it"
                : "the default for 31-sample files; --player ust changes it";
        logger.Information("Emulating {Player:l} ({Reason:l})", ModPlayers.DisplayName(module.Player), reason);
    }

    private static void LogOptions(ConversionOptions options, ILogger logger)
    {
        logger.Debug(
            "Format {Format}, input region {InputRegion} (Amiga clock {AmigaClockHz} Hz), output region {OutputRegion}, quality {Quality}, seed {Seed}",
            options.Format,
            options.InputRegion,
            options.AmigaClockHz,
            options.OutputRegion,
            options.Quality,
            options.Seed);
        logger.Debug(
            "PCM budget {MaxPcmBytes} bytes, reserved SFX channels {ReserveSfx}, consolidate {Consolidate}, cache {Cache}",
            options.MaxPcmBytes,
            options.ReserveSfxChannels,
            !options.NoConsolidate,
            !options.NoCache);
        LogSampleList(logger, "Ignoring samples {Samples}", options.IgnoreSamples);
        LogSampleList(logger, "Samples required as FM {Samples}", options.RequireFm);
        LogSampleList(logger, "Samples required as DAC {Samples}", options.RequireDac);
        LogSampleList(logger, "Samples required as PSG {Samples}", options.RequirePsg);
    }

    private static void LogSampleList(ILogger logger, string messageTemplate, IReadOnlySet<int> samples)
    {
        if (samples.Count > 0)
        {
            logger.Debug(messageTemplate, string.Join(",", samples.Order()));
        }
    }

    /// <summary>
    /// Sample lists are checked against 1–31 when parsed; this rejects numbers the module doesn't have (16–31 in a
    /// 15-sample file). Runs after the report, so the table shows which slots exist. Every problem is reported at once.
    /// </summary>
    private static void CheckSampleNumbers(ConversionOptions options, ModModule module)
    {
        List<string> problems = [];
        AddUnknown(problems, "--ignore-samples", options.IgnoreSamples, module.Samples.Count);
        AddUnknown(problems, "--require-fm", options.RequireFm, module.Samples.Count);
        AddUnknown(problems, "--require-dac", options.RequireDac, module.Samples.Count);
        AddUnknown(problems, "--require-psg", options.RequirePsg, module.Samples.Count);
        if (problems.Count > 0)
        {
            throw new ConversionException(ExitCode.UsageError, string.Join(Environment.NewLine, problems));
        }
    }

    private static void AddUnknown(List<string> problems, string option, IReadOnlySet<int> samples, int sampleCount)
    {
        int[] unknown = [.. samples.Where(s => s > sampleCount).Order()];
        if (unknown.Length > 0)
        {
            problems.Add(FormattableString.Invariant($"{option} {string.Join(",", unknown)}: this module has samples 1-{sampleCount} only."));
        }
    }

    private static byte[] ReadInput(string input)
    {
        try
        {
            long length = new FileInfo(input).Length;
            if (length > MaxInputBytes)
            {
                throw new ConversionException(
                    ExitCode.InvalidInput,
                    $"Not a valid MOD file: {length} bytes is larger than any MOD ({MaxInputBytes} bytes maximum): {input}");
            }

            return File.ReadAllBytes(input);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConversionException(ExitCode.InputNotFound, $"Input file can't be read: {input} ({ex.Message})", ex);
        }
    }

    private static void CheckInput(string input)
    {
        if (Directory.Exists(input))
        {
            throw new ConversionException(ExitCode.InputNotFound, $"Input is a directory, not a MOD file: {input}");
        }

        if (!File.Exists(input))
        {
            throw new ConversionException(ExitCode.InputNotFound, $"Input file not found: {input}");
        }
    }

    private static void CheckOutput(string input, string output, bool force)
    {
        if (OutputPaths.SameFile(input, output))
        {
            throw new ConversionException(ExitCode.OutputNotWritable, $"Output would overwrite the input file: {output}");
        }

        if (Directory.Exists(output))
        {
            throw new ConversionException(ExitCode.OutputNotWritable, $"Output is a directory: {output}");
        }

        string? directory = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            throw new ConversionException(ExitCode.OutputNotWritable, $"Output directory doesn't exist: {directory}");
        }

        if (File.Exists(output) && !force)
        {
            throw new ConversionException(ExitCode.OutputNotWritable, $"Output file already exists (use --force to overwrite): {output}");
        }
    }
}
