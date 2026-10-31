namespace Mod2Xgm.Render.Fm;

/// <summary>
/// The Genesis FM chip as the YM3438 (the CMOS OPN2 in later Mega Drive / Genesis models): a C# port of ymfm's
/// <c>ym3438</c> (<c>src/ymfm_opn.cpp</c> and <c>src/ymfm_fm.ipp</c>, commit 81aec25, BSD-3-Clause, THIRD-PARTY-NOTICES.md;
/// plan T7.2, docs/adr/0056-ym3438-model.md), changed where the chip, as Nuked-OPN2 models it, differs: the clipping
/// sum, operator timing, SSG-EG turns and holds, and the envelope timer (plan T7.7, docs/adr/0063-ym3438-chip-differences.md).
/// Tests check it sample for sample against ymfm where no difference reaches and against Nuked-OPN2 everywhere
/// (tests/golden/ym3438/README.md).
/// <para>The YM3438 has the YM2612's channels, registers and 9-bit DAC, but not its "ladder effect" (the extra step
/// around zero that makes quiet sounds crunchy on a Model 1).</para>
/// <para>It makes one stereo sample every 144 master clocks (<see cref="SampleRateHz"/>: about 53.3 kHz on NTSC).
/// Writes take effect before the next sample; the chip's busy time, timers, status reads and CSM mode are not modeled.</para>
/// </summary>
internal sealed class Ym3438
{
    /// <summary>The NTSC Genesis's master clock, which the YM2612 / YM3438 runs on (docs/genesis-audio.md).</summary>
    public const double NtscClockHz = 7_670_453;

    /// <summary>The PAL Mega Drive's master clock (docs/genesis-audio.md).</summary>
    public const double PalClockHz = 7_600_489;

    /// <summary>Master clocks per output sample: prescale 6 × 24 operator slots.</summary>
    private const int ClocksPerSample = 144;

    /// <summary>
    /// The largest output value: six channels at the bottom of the 9-bit clip (−256) scaled by 128 / 6; one step past
    /// <see cref="short.MaxValue"/>.
    /// </summary>
    public const int FullScale = 32_768;

    /// <summary>The DAC data register (bits 8–1 of the 9-bit DAC, offset binary).</summary>
    private const int RegDacData = 0x2a;

    /// <summary>The DAC enable register (bit 7: channel 6 plays the DAC).</summary>
    private const int RegDacEnable = 0x2b;

    /// <summary>The test register whose bit 3 is the DAC's lowest bit.</summary>
    private const int RegDacLowBit = 0x2c;

    /// <summary>Channel 6's register offset: its panning also pans the DAC.</summary>
    private const uint Channel6Offset = 0x102;

    /// <summary>Each channel's 9-bit output is the sum of its carriers, each >> 5, clipped to −256…255 after every carrier.</summary>
    private const int OutputShift = 5;

    /// <summary>The top of the 9-bit clip; the bottom is one further (Nuked-OPN2; ymfm uses ±256).</summary>
    private const int OutputClip = 255;

    private readonly FmEngine _fm = new();
    private ushort _address;
    private ushort _dacData;
    private byte _dacEnable;

    /// <summary>Creates the chip in its reset state.</summary>
    public Ym3438() => Reset();

    /// <summary>Output samples per second at master clock <paramref name="clockHz"/>.</summary>
    public static double SampleRateHz(double clockHz) => clockHz / ClocksPerSample;

    /// <summary>Resets the FM registers, channels and operators (the DAC registers and address latch keep their values, as in ymfm).</summary>
    public void Reset() => _fm.Reset();

    /// <summary>
    /// A write to one of the chip's four ports, as the 68000 or Z80 does it: 0 = address (port 0), 1 = data (port 0),
    /// 2 = address (port 1), 3 = data (port 1).
    /// </summary>
    public void Write(int port, byte data)
    {
        switch (port & 3)
        {
            case 0:
                _address = data;
                break;
            case 1:
                WriteData(data);
                break;
            case 2:
                _address = (ushort)(0x100 | data);
                break;
            default:
                WriteDataHi(data);
                break;
        }
    }

    /// <summary>Writes <paramref name="value"/> to <paramref name="register"/> on <paramref name="part"/> 0 or 1: an address write, then a data write.</summary>
    public void WriteRegister(int part, byte register, byte value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(part);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(part, 1);
        Write(part * 2, register);
        Write((part * 2) + 1, value);
    }

    /// <summary>
    /// Plays one sample per element of <paramref name="left"/> and <paramref name="right"/> (the same length): integers
    /// up to about ±<see cref="FullScale"/>.
    /// </summary>
    public void Generate(Span<int> left, Span<int> right)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("Both channels need the same length.", nameof(right));
        }

        OpnRegisters regs = _fm.Registers;
        for (int i = 0; i < left.Length; i++)
        {
            _fm.Clock(OpnRegisters.AllChannels);
            int l = 0;
            int r = 0;
            if (_dacEnable == 0)
            {
                _fm.Output(ref l, ref r, OutputShift, OutputClip, OpnRegisters.AllChannels);
            }
            else
            {
                // The DAC takes channel 6's place, with channel 6's panning.
                int dac = (short)(_dacData << 7) >> 7;
                l = regs.ChOutput0(Channel6Offset) != 0 ? dac : 0;
                r = regs.ChOutput1(Channel6Offset) != 0 ? dac : 0;
                _fm.Output(ref l, ref r, OutputShift, OutputClip, OpnRegisters.AllChannels ^ (1u << 5));
            }

            // The outputs are multiplexed, one channel at a time, rather than mixed; averaging over the six is the same sound.
            left[i] = l * 128 / 6;
            right[i] = r * 128 / 6;
        }
    }

    private void WriteData(byte data)
    {
        if (FmTables.Bitfield(_address, 8) != 0)
        {
            return;
        }

        switch (_address)
        {
            case RegDacData:
                _dacData = (ushort)((_dacData & ~0x1fe) | ((data ^ 0x80) << 1));
                break;
            case RegDacEnable:
                _dacEnable = (byte)FmTables.Bitfield(data, 7);
                break;
            case RegDacLowBit:
                _dacData = (ushort)((_dacData & ~1) | (int)FmTables.Bitfield(data, 3));
                break;
            default:
                _fm.Write(_address, data);
                break;
        }
    }

    private void WriteDataHi(byte data)
    {
        if (FmTables.Bitfield(_address, 8) == 0)
        {
            return;
        }

        _fm.Write(_address, data);
    }
}
