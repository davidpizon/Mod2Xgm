using Mod2Xgm.Cli;
using Mod2Xgm.Mod;
using Serilog;

namespace Mod2Xgm;

/// <summary>
/// Runs one conversion: checks the paths, reads the MOD, and (in later plan tasks) maps and writes the output.
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

        // Later stages (model, map, report section 3, write) are added by later plan tasks; they report lossy decisions to notes.

        logger.Information(
            "{Input} -> {Output} ({NoteCount} conversion notes)",
            Path.GetFileName(input),
            options.InfoOnly ? "(--info: no file written)" : output,
            notes.Count);
        return ExitCode.Success;
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
