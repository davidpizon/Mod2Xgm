namespace Mod2Xgm.Render;

/// <summary>
/// Hears what <see cref="GenesisPlanPlayer"/> tells the Genesis's sound hardware, in the order it does it: YM3438 register
/// writes, PSG port writes and the PCM driver's plays and stops. The XGM2 writer records them to make the file
/// (docs/adr/0099-xgm2-writer-records-the-player.md), so the file plays what the WAV render plays.
/// </summary>
internal interface IChipRecorder
{
    /// <summary>A YM3438 register write on <paramref name="part"/> 0 or 1.</summary>
    void FmWrite(int part, byte register, byte value);

    /// <summary>A byte written to the PSG's port.</summary>
    void PsgWrite(byte value);

    /// <summary>
    /// A PCM play on driver channel <paramref name="channel"/>: <paramref name="data"/> is the stored copy (8-bit signed at
    /// the driver's rate), the same array every time the same copy plays; <paramref name="sample"/> is the song sample it
    /// was made from and <paramref name="name"/> says what it is.
    /// </summary>
    void PcmPlay(int channel, sbyte[] data, bool halfSpeed, int sample, string name);

    /// <summary>A PCM stop on driver channel <paramref name="channel"/>.</summary>
    void PcmStop(int channel);
}
