namespace Mod2Xgm.Cli;

/// <summary>Output file format (<c>--format</c>).</summary>
internal enum OutputFormat
{
    Dmf,
    Xgm,
    Xgm2,
}

/// <summary>
/// A machine's region (<c>--input-region</c>: the Amiga the MOD was made on; <c>--output-region</c>: the Genesis that
/// plays the result). PAL and NTSC machines run at different clocks and frame rates.
/// </summary>
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
