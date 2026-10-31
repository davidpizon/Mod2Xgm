namespace Mod2Xgm.Render.Pcm;

/// <summary>
/// A model of what plays PCM on the Genesis through the YM3438's DAC (plan T7.4, docs/adr/0058-pcm-driver-models.md):
/// for every YM3438 output sample, the byte the player has in the DAC data register (<c>$2A</c>) and in the DAC enable
/// register (<c>$2B</c>). The Genesis renderer (T7.5) writes them into <see cref="Fm.Ym3438"/>, which turns them into
/// sound with channel 6's panning.
/// </summary>
internal interface IDacSource
{
    /// <summary>
    /// Plays one YM3438 sample per element of <paramref name="data"/> and <paramref name="enable"/> (the same length):
    /// <paramref name="data"/> gets register <c>$2A</c> (offset binary, <c>$80</c> = 0) and <paramref name="enable"/>
    /// register <c>$2B</c> (bit 7 on: the DAC replaces FM channel 6).
    /// </summary>
    void Generate(Span<byte> data, Span<byte> enable);
}
