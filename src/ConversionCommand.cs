using Mod2Xgm.Cli;
using Serilog;

namespace Mod2Xgm;

/// <summary>
/// Runs one conversion. For now it checks the input and output paths; the pipeline stages (read, map, write)
/// are added by later plan tasks.
/// </summary>
internal static class ConversionCommand
{
    public static ExitCode Execute(ConversionOptions options)
    {
        string input = Path.GetFullPath(options.InputPath);
        CheckInput(input);

        string output = OutputPaths.Resolve(options);
        if (!options.InfoOnly)
        {
            CheckOutput(input, output, options.Force);
        }

        Log.Information("{Input} -> {Output}", input, options.InfoOnly ? "(--info: no file written)" : output);
        return ExitCode.Success;
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
