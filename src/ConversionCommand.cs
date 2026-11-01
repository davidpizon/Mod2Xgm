using System.Diagnostics;
using Mod2Xgm.Analysis;
using Mod2Xgm.Cli;
using Mod2Xgm.Dmf;
using Mod2Xgm.Mapping;
using Mod2Xgm.Mod;
using Mod2Xgm.Model;
using Mod2Xgm.Render;
using Serilog;
using Serilog.Events;

namespace Mod2Xgm;

/// <summary>
/// Runs one conversion: checks the paths, reads the MOD, prints the report, maps the song with the simple mapper and
/// writes the DMF (docs/architecture.md); with <c>--render-wav</c> it also renders the plan on the Genesis model and
/// writes the WAV (docs/adr/0059-genesis-renderer.md). Every stage reports its lossy decisions to the conversion notes.
/// </summary>
internal static class ConversionCommand
{
    /// <summary>Largest input accepted. Real MODs are a few MB at most; anything bigger isn't a MOD.</summary>
    public const long MaxInputBytes = 64L * 1024 * 1024;

    /// <summary>Runs one conversion, logging to the global Serilog logger.</summary>
    public static ExitCode Execute(ConversionOptions options) => Execute(options, Log.Logger);

    /// <summary>Runs one conversion, logging to <paramref name="logger"/>; expected failures throw <see cref="ConversionException"/>.</summary>
    public static ExitCode Execute(ConversionOptions options, ILogger logger) => Execute(options, logger, null);

    /// <summary>
    /// Runs one conversion, logging to <paramref name="logger"/>, and hands the song, its playback, the sample analysis and
    /// how many notes the song sounds at once to <paramref name="analyzed"/> when it is given (the accuracy harness writes them to its CSVs
    /// without analyzing twice); expected failures throw <see cref="ConversionException"/>.
    /// </summary>
    public static ExitCode Execute(ConversionOptions options, ILogger logger, Action<SongAnalysis>? analyzed)
    {
        LogOptions(options, logger);

        string input = Path.GetFullPath(options.InputPath);
        CheckInput(input);

        string output = OutputPaths.Resolve(options);
        bool writesSong = !options.InfoOnly && (options.Format == OutputFormat.Dmf || !options.RenderWav);
        if (writesSong)
        {
            CheckOutput(input, output, options.Force);
        }

        string? wav = options.RenderWav ? OutputPaths.ResolveRenderWav(options, output) : null;
        if (wav is not null)
        {
            CheckOutput(input, wav, options.Force);
            if (writesSong && OutputPaths.SameFile(wav, output))
            {
                throw new ConversionException(ExitCode.OutputNotWritable, $"The WAV would overwrite the output file: {wav}");
            }
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
        Playback playback = ModTickPlayer.Play(module, options.AmigaClockHz, options.AmigaTickRateScale, notes);
        MappingOptions mapping = MappingOptions.From(options);
        bool verbose = logger.IsEnabled(LogEventLevel.Debug);
        if (verbose || analyzed is not null)
        {
            // Nothing uses the analysis yet (the optimizer will, plan M9); --verbose shows it (docs/adr/0068-sample-analysis.md).
            IReadOnlyList<SampleAnalysis> analyses = SampleAnalyzer.AnalyzeSong(song, playback, mapping.Ignore);
            if (verbose)
            {
                SampleAnalysisReport.Log(logger, analyses);
            }

            analyzed?.Invoke(new SongAnalysis(song, playback, analyses, SongVoices.Count(song, playback)));
        }

        GenesisPlan plan = new SimpleMapper().Map(song, playback, mapping, notes);
        logger.Debug(
            "Mapped {Orders} orders: {FmPatches} FM patches, {DacSamples} DAC samples",
            plan.Patterns.Count,
            plan.FmPatches.Count,
            plan.DacSamples.Count);
        if (options.Format == OutputFormat.Dmf)
        {
            WriteDmf(options, output, song, plan, notes, logger);
        }

        if (wav is not null)
        {
            RenderWav(options, wav, playback, song, plan, notes, logger);
        }

        logger.Information(
            "{Input} -> {Output} ({NoteCount} conversion notes)",
            Path.GetFileName(input),
            options.Format != OutputFormat.Dmf ? "(no song file: only the WAV render)" : options.InfoOnly ? "(--info: no file written)" : output,
            notes.Count);
        if (options.Format != OutputFormat.Dmf)
        {
            throw new ConversionException(ExitCode.UsageError, $"{FormatUnavailable(options.Format)} Only the WAV render was written: {wav}");
        }

        return ExitCode.Success;
    }

    /// <summary>Plans the DMF's timing, builds it, prints report section 3 and writes the file (unless <c>--info</c>).</summary>
    private static void WriteDmf(ConversionOptions options, string output, Song song, GenesisPlan plan, ConversionNotes notes, ILogger logger)
    {
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
    }

    /// <summary>
    /// Plays the song with the tick player, renders the plan on the Genesis model with the format's PCM driver and writes
    /// the WAV (<c>--render-wav</c>; written with <c>--info</c> too), saying how long the render took.
    /// </summary>
    private static void RenderWav(ConversionOptions options, string wav, Playback playback, Song song, GenesisPlan plan, ConversionNotes notes, ILogger logger)
    {
        PcmDriver driver = options.Format switch
        {
            OutputFormat.Xgm => PcmDriver.Xgm,
            OutputFormat.Xgm2 => PcmDriver.Xgm2,
            _ => PcmDriver.Dmf,
        };
        Stopwatch clock = Stopwatch.StartNew();
        StereoAudio audio = GenesisRenderer.Render(plan, song, playback, new GenesisRenderOptions(driver, options.OutputRegion, RenderWavRate, options.Resampler), notes);
        clock.Stop();
        int clipped = WavEncoder.ClippedSamples(audio);
        if (clipped > 0)
        {
            notes.Add("The WAV render goes past full scale on {Count} samples, which are clipped", clipped);
        }

        WriteOutput(wav, WavEncoder.Encode(audio), options.Force);
        logger.Information(
            "Rendered {Seconds:0.0} s of Genesis sound ({Driver} PCM) in {Elapsed:0.0} s: {Wav}",
            audio.Seconds,
            driver,
            clock.Elapsed.TotalSeconds,
            wav);
    }

    /// <summary>The <c>--render-wav</c> file's sample rate.</summary>
    private const int RenderWavRate = 44_100;

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

        if (options.RenderWav)
        {
            // The renderer has the format's PCM driver already: the WAV is made, then the run fails for the missing writer.
            logger.Information("{Message:l} With --render-wav, the Genesis render is still made.", FormatUnavailable(options.Format));
            return;
        }

        throw new ConversionException(ExitCode.UsageError, FormatUnavailable(options.Format));
    }

    /// <summary>Why <paramref name="format"/> can't be written yet.</summary>
    private static string FormatUnavailable(OutputFormat format) =>
        $"--format {(format == OutputFormat.Xgm2 ? "xgm2" : "xgm")} isn't available yet: this version writes DMF only (XGM2 comes with plan M10, XGM with M11).";

    /// <summary>The line every <c>--format xgm2</c> run prints, verbatim (docs/cli.md#console-output).</summary>
    private const string Xgm2ExtensionNote = "NOTE: SGDK expects XGM2 files to use the .XGM file extension.";

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
