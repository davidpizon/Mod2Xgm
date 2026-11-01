namespace Mod2Xgm.Render.Psg;

/// <summary>
/// The Genesis PSG's sixteen volume levels, as Nuked-PSG (a model of the YM7101 VDP's PSG, made from the die) plays them
/// (docs/adr/0067-psg-volume-levels.md): measured from its recorded output in tests/golden/sn76489/volumes.nuked.gz, not
/// read from its source. They fall off faster than the TI datasheet's even 2 dB steps that MAME uses: 2.2 dB a step at
/// the loud end, 6.5 dB at the quiet end, so volume 14 is 41 dB down rather than 28. Volume register values count down:
/// 0 is the loudest, 15 is off.
/// </summary>
internal static class PsgLevels
{
    /// <summary>The volume register value that silences a channel.</summary>
    public const int Off = 15;

    /// <summary>
    /// A channel's level at each volume register value: Nuked-PSG's output × 8192, truncated as the reference tool records
    /// it, with the loudest kept at MAME's 8191 so the chip's full scale doesn't move. The origin of Nuked's levels is
    /// not documented (**Verify** against a real Mega Drive).
    /// </summary>
    public static ReadOnlySpan<int> Channel => [8191, 6324, 5095, 3973, 3129, 2375, 1875, 1425, 1081, 786, 589, 417, 278, 155, 73, 0];

    /// <summary>The level of volume register value <paramref name="volume"/> relative to the loudest (1 at 0, 0 at <see cref="Off"/>).</summary>
    public static double Gain(int volume) => (double)Channel[volume] / Channel[0];

    /// <summary>
    /// The volume register value for linear level <paramref name="gain"/> (1 the loudest), unrounded: between two levels
    /// it moves evenly in decibels, so rounding it picks the nearer level in decibels; past the quietest level it moves
    /// evenly in amplitude down to off, so it rounds to off below half the quietest level.
    /// </summary>
    public static double Attenuation(double gain)
    {
        if (gain >= 1)
        {
            return 0;
        }

        int quietest = Off - 1;
        if (gain <= 0)
        {
            return Off;
        }

        if (gain < Gain(quietest))
        {
            return quietest + 1 - (gain / Gain(quietest));
        }

        int volume = 0;
        while (Gain(volume + 1) > gain)
        {
            volume++;
        }

        double louder = Math.Log(Gain(volume));
        double quieter = Math.Log(Gain(volume + 1));
        return volume + ((louder - Math.Log(gain)) / (louder - quieter));
    }

    /// <summary>The volume register value whose level is nearest linear level <paramref name="gain"/> (see <see cref="Attenuation"/>).</summary>
    public static int Nearest(double gain) => (int)Math.Round(Attenuation(gain), MidpointRounding.AwayFromZero);
}
