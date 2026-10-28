using System.Buffers.Binary;

namespace Mod2Xgm.Tests.Dmf;

/// <summary>
/// What DefleMask actually plays for each probe (plan T4.3), read from its own VGM exports in
/// <c>tests/golden/dmf-probes/vgm/</c>: the chip writes that prove each rule in docs/formats/dmf.md, so the results
/// don't rest on listening alone.
/// </summary>
public sealed class DmfProbeVgmTests
{
    /// <summary>Allowed pitch error: 1 %, far below a semitone (6 %), well above F-number rounding.</summary>
    private const double PitchTolerance = 0.01;

    private static VgmLog Vgm(string probe) =>
        VgmLog.Read(File.ReadAllBytes(Path.Combine(RepoPaths.Root, "tests", "golden", "dmf-probes", "vgm", probe + ".vgm")));

    /// <summary>A stream command's bytes (every stream event has them).</summary>
    private static byte[] Data(VgmEvent e) => e.Data ?? throw new InvalidOperationException("A stream event without data.");

    private static void AssertHz(double expected, double actual) =>
        Assert.InRange(actual, expected * (1 - PitchTolerance), expected * (1 + PitchTolerance));

    /// <summary>Times (seconds, to the millisecond) of the YM2612 key-ons for FM channel <paramref name="channel"/> (0-based, port 0).</summary>
    private static double[] KeyOns(VgmLog vgm, int channel) =>
        [.. vgm.Ym(0x28).Where(e => (e.Value & 0x07) == channel && (e.Value & 0xF0) != 0).Select(e => Math.Round(e.Seconds, 3))];

    /// <summary>
    /// File operator 2 is the register slot at +8 (OP2): DefleMask writes TL 0 there and 127 to the other three, under
    /// algorithm 4. A-4 plays at 440 Hz and C-5 (note 12 of octave 4) at 523 Hz, 0.72 s (6 rows) later.
    /// </summary>
    [Fact]
    public void FmNoteUsesRegisterOrderAndTheCRule()
    {
        VgmLog vgm = Vgm("fm-note");

        Assert.Equal(0, vgm.Ym(0x48).Last().Value);
        int[] silent = [0x40, 0x44, 0x4C];
        Assert.All(silent, register => Assert.Equal(0x7F, vgm.Ym(register).Last().Value));
        Assert.Equal(4, vgm.Ym(0xB0).Last().Value & 7);
        Assert.Equal([0.0, 0.72], KeyOns(vgm, 0));
        AssertHz(440, vgm.FmHz(0xA4, 0xA0, 0));
        AssertHz(523.25, vgm.FmHz(0xA4, 0xA0, (int)(0.72 * VgmLog.SampleRate)));
    }

    /// <summary>
    /// DefleMask's PSG A-4 is period 64 at the 3.58 MHz PSG clock: 1748 Hz, <b>two octaves above</b> the FM A-4. The
    /// noise channel then gets white noise at a fixed rate (<c>2001</c>).
    /// </summary>
    [Fact]
    public void PsgNotesSoundTwoOctavesAboveTheirName()
    {
        VgmLog vgm = Vgm("psg-note");
        int[] psg = [.. vgm.Events.Where(e => e.Chip == VgmChip.Sn76489).Select(e => e.Register)];

        int latch = Array.IndexOf(psg, 0x80); // channel 0 tone, low 4 bits 0
        int period = ((psg[latch + 1] & 0x3F) << 4) | (psg[latch] & 0x0F);
        Assert.Equal(64, period);
        AssertHz(4 * 440, vgm.Sn76489ClockHz / 32.0 / period);
        Assert.Contains(psg, b => (b & 0xF0) == 0xE0 && (b & 0x04) != 0); // noise control: white
    }

    /// <summary>
    /// <c>1701</c> turns on the DAC (register 2B = 80). C-3 and C-5 both play sample 0 at 32 kHz (the octave is ignored);
    /// C#3 plays sample 1 at 8 kHz: rate codes 5 and 1. DefleMask stores the samples as 8-bit, one byte each.
    /// </summary>
    [Fact]
    public void DacNotesPickSamplesAtTheirRate()
    {
        VgmLog vgm = Vgm("dac-sample");

        Assert.Contains(vgm.Ym(0x2B), e => e.Value == 0x80);
        Assert.Equal([12800, 3200], vgm.DataBlockSizes);
        Assert.Equal([32000, 32000, 8000], vgm.Stream(0x92).Select(e => BinaryPrimitives.ReadInt32LittleEndian(Data(e).AsSpan(1))));
        Assert.Equal([0, 0, 1], vgm.Stream(0x95).Select(e => (int)BinaryPrimitives.ReadUInt16LittleEndian(Data(e).AsSpan(1))));
        Assert.Equal([0.0, 0.48, 0.96], vgm.Stream(0x95).Select(e => Math.Round(e.Seconds, 3)));
    }

    /// <summary>Custom Hz "25" with speed 5: a row every 0.2 s, so the beeps on rows 0, 5, 10, 15 key on once a second.</summary>
    [Fact]
    public void CustomHzSetsTheTickRate()
    {
        VgmLog vgm = Vgm("custom-hz");

        Assert.Equal([0.0, 1.0, 2.0, 3.0], KeyOns(vgm, 0));
        Assert.Equal(4 * (int)VgmLog.SampleRate, vgm.TotalSamples);
    }

    /// <summary>
    /// System 0x42 turns on CH3 special mode (register 27 = 40); the OP1–OP4 channels set operators 1–4's own frequencies
    /// (AD/A9, AE/AA, AC/A8, A6/A2) to C-4, E-4, G-4, C-5 and key them on one by one.
    /// </summary>
    [Fact]
    public void ExtendedChannel3PlaysOneNotePerOperator()
    {
        VgmLog vgm = Vgm("ext-ch3");
        int Row(int row) => (int)(row * 0.12 * VgmLog.SampleRate);

        Assert.Contains(vgm.Ym(0x27), e => e.Value == 0x40);
        AssertHz(261.63, vgm.FmHz(0xAD, 0xA9, Row(0)));
        AssertHz(329.63, vgm.FmHz(0xAE, 0xAA, Row(2)));
        AssertHz(392.00, vgm.FmHz(0xAC, 0xA8, Row(4)));
        AssertHz(523.25, vgm.FmHz(0xA6, 0xA2, Row(6)));
        Assert.Equal([0x12, 0x32, 0x72, 0xF2], vgm.Ym(0x28).Where(e => (e.Value & 0x0F) == 2 && e.Time < Row(12)).Select(e => e.Value).Distinct().Where(v => v != 0x02));
    }
}
