namespace Mod2Xgm.Mod;

/// <summary>
/// Whose rules decide how a MOD file is read and what its effects mean (<c>--player</c>, docs/cli.md#players). Players
/// disagree on the same bytes: Ultimate Soundtracker stores loop starts in bytes, keeps the tempo in byte 471 and
/// knows only arpeggio and pitch bend, where ProTracker reads words, a restart position and sixteen effects.
/// </summary>
internal enum ModPlayer
{
    /// <summary>ProTracker 2.3, as pt2-clone reproduces it; the default for 31-sample files.</summary>
    ProTracker2,

    /// <summary>Ultimate Soundtracker 1.x, as OpenMPT's Soundtracker loader reads it; the default for 15-sample files.</summary>
    UltimateSoundtracker,
}

/// <summary>Defaults and facts about each <see cref="ModPlayer"/>.</summary>
internal static class ModPlayers
{
    /// <summary>Ultimate Soundtracker's default tempo byte (0x78): the 50 Hz screen tick, played as 125 BPM.</summary>
    private const int DefaultTempoByte = 0x78;

    /// <summary>Tempo bytes from 240 up would make the timer value zero or negative.</summary>
    private const int TempoByteLimit = 240;

    /// <summary>ProTracker's tempo when nothing sets one.</summary>
    private const double DefaultBpm = 125;

    /// <summary>The player a file is read with when <c>--player</c> isn't given: Ultimate Soundtracker for 15-sample files, ProTracker 2 otherwise (David, 2026-10-27).</summary>
    public static ModPlayer DefaultFor(ModFormat format) =>
        format == ModFormat.Soundtracker15 ? ModPlayer.UltimateSoundtracker : ModPlayer.ProTracker2;

    /// <summary>The player's name for messages.</summary>
    public static string DisplayName(ModPlayer player) => player switch
    {
        ModPlayer.UltimateSoundtracker => "Ultimate Soundtracker",
        _ => "ProTracker 2",
    };

    /// <summary>
    /// Ultimate Soundtracker's tempo from byte 471 (docs/formats/mod.md#players), converted to BPM the way OpenMPT does:
    /// 0 and 0x78 mean the 50 Hz default (125 BPM); any other value b sets the Amiga timer to (240 − b) × 122 ticks of its
    /// 709 379 Hz clock, which is 709 379 × 125 / 50 / ((240 − b) × 122) BPM.
    /// </summary>
    /// <param name="tempoByte">Byte 471 as stored.</param>
    /// <returns>The BPM, or <c>null</c> when the byte is 240 or more and has no meaning (the caller notes it and uses 125).</returns>
    public static double? UltimateSoundtrackerBpm(int tempoByte) => tempoByte switch
    {
        0 or DefaultTempoByte => DefaultBpm,
        >= TempoByteLimit => null,
        _ => 709_379.0 * 125.0 / 50.0 / ((TempoByteLimit - tempoByte) * 122.0),
    };
}
