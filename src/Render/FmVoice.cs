using Mod2Xgm.Mapping;
using Mod2Xgm.Render.Fm;

namespace Mod2Xgm.Render;

/// <summary>
/// Writes an <see cref="FmPatch"/> and a pitch to one YM3438 channel (docs/genesis-audio.md): what the plan player
/// (<see cref="GenesisPlanPlayer"/>) and the FM fitter's note renderer (<c>Optimizer.FmNoteRenderer</c>) share, so a
/// fitted patch sounds in the song as it did when it was fitted.
/// </summary>
internal static class FmVoice
{
    /// <summary>F-numbers are 11 bits.</summary>
    private const int MaxFnum = 2047;

    private const int MaxBlock = 7;

    // YM3438 operator and channel registers, channel 1's addresses (docs/genesis-audio.md).
    private const byte RegDetuneMult = 0x30;
    private const byte RegTl = 0x40;
    private const byte RegRsAr = 0x50;
    private const byte RegAmDr = 0x60;
    private const byte RegD2R = 0x70;
    private const byte RegSlRr = 0x80;
    private const byte RegSsgEg = 0x90;
    private const byte RegFeedbackAlgorithm = 0xb0;

    /// <summary>Register offsets of OP1–OP4 (Yamaha order) within a channel's operator registers: OP1, OP3, OP2, OP4 in address order.</summary>
    public static IReadOnlyList<int> SlotOffsets { get; } = [0, 8, 4, 12];

    /// <summary>Writes every operator register of <paramref name="patch"/>, SSG-EG included, and its feedback and algorithm, to FM channel <paramref name="channel"/> (0–5).</summary>
    public static void LoadPatch(Ym3438 chip, int channel, FmPatch patch)
    {
        ArgumentNullException.ThrowIfNull(chip);
        ArgumentNullException.ThrowIfNull(patch);
        for (int op = 0; op < 4; op++)
        {
            Mapping.FmOperator o = patch.Operators[op];
            int slot = SlotOffsets[op];
            int detune = o.Detune >= 0 ? o.Detune : 4 - o.Detune;
            Write(chip, channel, (byte)(RegDetuneMult + slot), (byte)((detune << 4) | o.Mult));
            Write(chip, channel, (byte)(RegTl + slot), (byte)o.Tl);
            Write(chip, channel, (byte)(RegRsAr + slot), (byte)((o.Rs << 6) | o.Ar));
            Write(chip, channel, (byte)(RegAmDr + slot), (byte)((o.Am ? 0x80 : 0) | o.Dr));
            Write(chip, channel, (byte)(RegD2R + slot), (byte)o.D2R);
            Write(chip, channel, (byte)(RegSlRr + slot), (byte)((o.Sl << 4) | o.Rr));
            Write(chip, channel, (byte)(RegSsgEg + slot), (byte)o.SsgEg);
        }

        Write(chip, channel, RegFeedbackAlgorithm, (byte)((patch.Feedback << 3) | patch.Algorithm));
    }

    /// <summary>
    /// The block (bits 13–11) and F-number (bits 10–0) closest to <paramref name="hz"/> on a chip making
    /// <paramref name="chipRateHz"/> samples a second: the lowest block that holds it, for the finest steps.
    /// </summary>
    public static int BlockFnum(double hz, double chipRateHz)
    {
        // f = fnum × 2^(block − 1) × rate / 2^20 (docs/genesis-audio.md).
        double fnumAtBlock0 = hz * (1 << 21) / chipRateHz;
        for (int block = 0; block <= MaxBlock; block++)
        {
            int fnum = (int)Math.Round(fnumAtBlock0 / (1 << block));
            if (fnum <= MaxFnum)
            {
                return (block << 11) | fnum;
            }
        }

        return (MaxBlock << 11) | MaxFnum;
    }

    /// <summary>Writes an FM channel's register: <paramref name="register"/> is the channel 1 address, offset by the channel within its part.</summary>
    public static void Write(Ym3438 chip, int channel, byte register, byte value)
    {
        ArgumentNullException.ThrowIfNull(chip);
        chip.WriteRegister(channel / 3, (byte)(register + (channel % 3)), value);
    }
}
