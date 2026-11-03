namespace Mod2Xgm.Output;

/// <summary>
/// Finds <c>--progress</c> in the raw arguments before they are parsed, so even a parse error is reported in the mode
/// the caller asked for, and <c>silent</c> stays silent (docs/cli-output.md#never-interactive).
/// </summary>
internal static class ProgressModeScan
{
    /// <summary>The option's name, as the parser knows it too (<see cref="Cli.CommandLineDefinition.Progress"/>).</summary>
    public const string OptionName = "--progress";

    /// <summary>The modes by their command-line names.</summary>
    public static IReadOnlyDictionary<string, OutputMode> Names { get; } = new Dictionary<string, OutputMode>(StringComparer.Ordinal)
    {
        ["fancy"] = OutputMode.Fancy,
        ["text"] = OutputMode.Text,
        ["json"] = OutputMode.Json,
        ["silent"] = OutputMode.Silent,
    };

    /// <summary>
    /// The mode the last <c>--progress</c> in <paramref name="args"/> names (<c>--progress json</c>, <c>--progress=json</c>
    /// or <c>--progress:json</c>), or <see cref="OutputMode.Fancy"/> when there is none or its value is unknown (the parser
    /// then reports the bad value). Arguments after <c>--</c> are file names, not options.
    /// </summary>
    public static OutputMode Find(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        OutputMode mode = OutputMode.Fancy;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg == "--")
            {
                break;
            }

            string? value = null;
            if (arg == OptionName)
            {
                value = i + 1 < args.Count ? args[++i] : string.Empty;
            }
            else if (arg.Length > OptionName.Length
                && arg.StartsWith(OptionName, StringComparison.Ordinal)
                && arg[OptionName.Length] is '=' or ':')
            {
                value = arg[(OptionName.Length + 1)..];
            }

            if (value is not null)
            {
                mode = Names.GetValueOrDefault(value, OutputMode.Fancy);
            }
        }

        return mode;
    }
}
