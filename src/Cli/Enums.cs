namespace Mod2Xgm.Cli;

/// <summary>Output file format (<c>--format</c>).</summary>
internal enum OutputFormat
{
    Dmf,
    Xgm,
    Xgm2,
}

/// <summary>Genesis region the music is timed for (<c>--region</c>).</summary>
internal enum Region
{
    Pal,
    Ntsc,
}

/// <summary>How hard the converter tries to match the original sound (<c>--quality</c>).</summary>
internal enum Quality
{
    Fast,
    Normal,
    Best,
}
