using Mod2Xgm.Cli;
using Serilog;

namespace Mod2Xgm;

/// <summary>
/// Runs one conversion. For now it checks the input and output paths; the pipeline stages (read, map, write)
/// are added by later plan tasks.
/// </summary>
internal static class ConversionCommand
{
    public static ExitCode Execute(ConversionOptions options) => Execute(options, Log.Logger);

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

        // Pipeline stages (read, map, write) are added by later plan tasks; they report lossy decisions to notes.

        logger.Information(
            "{Input} -> {Output} ({NoteCount} conversion notes)",
            Path.GetFileName(input),
            options.InfoOnly ? "(--info: no file written)" : output,
            notes.Count);
        return ExitCode.Success;
    }

    private static void LogOptions(ConversionOptions options, ILogger logger)
    {
        logger.Debug("Format {Format}, region {Region}, quality {Quality}, seed {Seed}", options.Format, options.Region, options.Quality, options.Seed);
        logger.Debug(
            "PCM budget {MaxPcmBytes} bytes, reserved SFX channels {ReserveSfx}, consolidate {Consolidate}, cache {Cache}",
            options.MaxPcmBytes,
            options.ReserveSfxChannels,
            !options.NoConsolidate,
            !options.NoCache);
        LogSampleList(logger, "Ignoring samples {Samples}", options.IgnoreSamples);
        LogSampleList(logger, "Samples required as FM {Samples}", options.RequireFm);
        LogSampleList(logger, "Samples required as PCM {Samples}", options.RequirePcm);
    }

    private static void LogSampleList(ILogger logger, string messageTemplate, IReadOnlySet<int> samples)
    {
        if (samples.Count > 0)
        {
            logger.Debug(messageTemplate, string.Join(",", samples.Order()));
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
