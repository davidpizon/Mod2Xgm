namespace Mod2Xgm.Audio;

/// <summary>Bits per PCM point in an output file.</summary>
internal enum PcmDepth
{
    /// <summary>8-bit signed: XGM and XGM2 (docs/formats/xgm.md).</summary>
    Bits8,

    /// <summary>16-bit signed: what the DMF writer stores (docs/formats/dmf.md#9-pcm-samples).</summary>
    Bits16,
}
