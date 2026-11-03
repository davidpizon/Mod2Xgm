namespace Mod2Xgm.Output;

/// <summary>How the converter talks to its caller (<c>--progress</c>, docs/cli-output.md, ADR 0077).</summary>
internal enum OutputMode
{
    /// <summary>For a person: Spectre.Console tables, progress bars and colors (the default).</summary>
    Fancy,

    /// <summary>For AI agents: compact lines, one fact per line, no color or animation.</summary>
    Text,

    /// <summary>For programs: one JSON object per line (NDJSON) on stdout.</summary>
    Json,

    /// <summary>Nothing at all; only the exit code says what happened.</summary>
    Silent,
}
