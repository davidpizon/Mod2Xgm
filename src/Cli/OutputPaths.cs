namespace Mod2Xgm.Cli;

/// <summary>Output file naming (docs/cli.md#output-naming).</summary>
internal static class OutputPaths
{
    public static string ExtensionFor(OutputFormat format) => format switch
    {
        OutputFormat.Xgm => ".xgm",
        OutputFormat.Xgm2 => ".xgm2",
        _ => ".dmf",
    };

    /// <summary>
    /// The full output path: <c>--output</c> when given, otherwise the input's directory and base name with the
    /// format's extension.
    /// </summary>
    public static string Resolve(ConversionOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.OutputPath))
        {
            return Path.GetFullPath(options.OutputPath);
        }

        string input = Path.GetFullPath(options.InputPath);
        return Path.ChangeExtension(input, ExtensionFor(options.Format));
    }

    /// <summary>Whether two full paths name the same file on this platform's file system.</summary>
    public static bool SameFile(string first, string second) =>
        string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
