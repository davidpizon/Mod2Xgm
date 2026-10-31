namespace Mod2Xgm.Render.Psg;

/// <summary>
/// The Genesis PSG: Sega's SN76489 inside the VDP, three square-wave tone channels and one noise channel, sample for
/// sample what MAME's <c>segapsg</c> plays (plan T7.3, docs/adr/0057-sn76489-model.md): a C# port of MAME's
/// <c>src/devices/sound/sn76496.cpp</c> (commit d066f16, BSD-3-Clause, THIRD-PARTY-NOTICES.md) in its Sega VDP setup,
/// checked bit-exact against MAME and against Nuked-PSG by tests (tests/golden/sn76489/README.md).
/// <para>Sega's version differs from TI's: a 16-bit noise shift register tapped at bits 0 and 3, a tone period of 0
/// that stays 0 (TI's acts as <c>$400</c>), all four channels silent at power-on, and the register latch starting on
/// channel 2's volume.</para>
/// <para>It makes one mono sample every 16 PSG clocks (<see cref="SampleRateHz"/>: about 223.7 kHz on NTSC), the rate
/// the chip's counters step at. Writes take effect before the next sample; the READY line is not modeled.</para>
/// </summary>
internal sealed class Sn76489
{
    /// <summary>The NTSC Genesis's PSG clock: the Z80's, the system clock ÷ 15 (docs/genesis-audio.md).</summary>
    public const double NtscClockHz = 3_579_545;

    /// <summary>The PAL Mega Drive's PSG clock (docs/genesis-audio.md).</summary>
    public const double PalClockHz = 3_546_893;

    /// <summary>PSG clocks per output sample: the ÷16 the counters run at (MAME: a stream at clock ÷ 2, stepped every 8).</summary>
    private const int ClocksPerSample = 16;

    /// <summary>
    /// The largest output value: all four channels high at the loudest volume, 4 × 8191 (MAME gives each channel a
    /// quarter of the 16-bit range). The output is negated, so it runs from −<see cref="FullScale"/> to 0.
    /// </summary>
    public const int FullScale = 4 * MaxChannelLevel;

    /// <summary>A channel's level at volume 0 (the loudest): MAME's <c>MAX_OUTPUT / 4</c>.</summary>
    private const int MaxChannelLevel = 0x7fff / 4;

    /// <summary>One volume step: 2 dB, as a ratio (MAME's constant, 10^(2/20)).</summary>
    private const double VolumeStep = 1.258925412;

    /// <summary>The bit the noise shift register's feedback goes into: a 16-bit register (Sega).</summary>
    private const uint FeedbackMask = 0x8000;

    /// <summary>The white-noise taps: bits 0 and 3 (Sega).</summary>
    private const uint WhiteNoiseTap1 = 0x01;

    private const uint WhiteNoiseTap2 = 0x08;

    /// <summary>The noise register (register 6): bits 0–1 the rate, bit 2 white (1) or periodic (0) noise.</summary>
    private const int RegNoise = 6;

    /// <summary>The noise rate that follows tone channel 3 (index 2) instead of a fixed divider.</summary>
    private const int NoiseRateTone3 = 3;

    /// <summary>The noise register's white-noise bit.</summary>
    private const int NoiseWhite = 0x04;

    /// <summary>The register the latch points at after reset on Sega's chip: channel 2's volume (MAME, from hardware tests).</summary>
    private const int ResetLatch = 3;

    /// <summary>Channel levels by volume register value, 0 (loudest) to 15 (off), as MAME builds them.</summary>
    private static readonly int[] VolumeTable = BuildVolumeTable();

    private readonly int[] _registers = new int[8];
    private readonly int[] _volume = new int[4];
    private readonly int[] _period = new int[4];
    private readonly int[] _count = new int[4];
    private readonly int[] _output = new int[4];
    private int _lastRegister;
    private uint _rng;

    /// <summary>Creates the chip in its power-on state.</summary>
    public Sn76489() => Reset();

    /// <summary>Output samples per second at PSG clock <paramref name="clockHz"/>.</summary>
    public static double SampleRateHz(double clockHz) => clockHz / ClocksPerSample;

    /// <summary>
    /// The power-on state, as MAME starts the Sega PSG: periods 0, every channel at volume 15 (off), the latch on
    /// register 3 and the noise shift register at its feedback bit.
    /// </summary>
    public void Reset()
    {
        _lastRegister = ResetLatch;
        for (int i = 0; i < 8; i += 2)
        {
            _registers[i] = 0;
            _registers[i + 1] = 0xf;
        }

        for (int i = 0; i < 4; i++)
        {
            _output[i] = 0;
            _count[i] = 0;
            _period[i] = 0;
            _volume[i] = VolumeTable[_registers[(i * 2) + 1]];
        }

        _rng = FeedbackMask;
        _output[3] = (int)(_rng & 1);
    }

    /// <summary>
    /// A byte written to the PSG port. With bit 7 set it latches register bits 6–4 and writes the low 4 bits to it; with
    /// bit 7 clear it writes to the latched register: the high 6 bits of a tone period, or the low 4 bits of anything else.
    /// Any write to the noise register restarts the noise.
    /// </summary>
    public void Write(byte data)
    {
        int r;
        if ((data & 0x80) != 0)
        {
            r = (data & 0x70) >> 4;
            _lastRegister = r;
            _registers[r] = (_registers[r] & 0x3f0) | (data & 0x0f);
        }
        else
        {
            r = _lastRegister;
        }

        int c = r >> 1;
        switch (r)
        {
            case 0:
            case 2:
            case 4:
                if ((data & 0x80) == 0)
                {
                    _registers[r] = (_registers[r] & 0x0f) | ((data & 0x3f) << 4);
                }

                // Sega's chip keeps a period of 0 as 0 (TI's acts as $400).
                _period[c] = _registers[r];
                if (r == 4 && (_registers[RegNoise] & 3) == NoiseRateTone3)
                {
                    _period[3] = _period[2] << 1;
                }

                break;
            case RegNoise:
                if ((data & 0x80) == 0)
                {
                    _registers[r] = (_registers[r] & 0x3f0) | (data & 0x0f);
                }

                // N/512, N/1024, N/2048, or tone 3's output.
                int n = _registers[RegNoise];
                _period[3] = (n & 3) == NoiseRateTone3 ? _period[2] << 1 : 1 << (5 + (n & 3));
                _rng = FeedbackMask;
                break;
            default:
                _volume[c] = VolumeTable[data & 0x0f];
                if ((data & 0x80) == 0)
                {
                    _registers[r] = (_registers[r] & 0x3f0) | (data & 0x0f);
                }

                break;
        }
    }

    /// <summary>
    /// Plays one sample per element of <paramref name="output"/>: integers from −<see cref="FullScale"/> to 0 (MAME
    /// negates the Sega PSG's output, so a channel that is high pulls the output down).
    /// </summary>
    public void Generate(Span<int> output)
    {
        for (int i = 0; i < output.Length; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                if (--_count[c] <= 0)
                {
                    _output[c] ^= 1;
                    _count[c] = _period[c];
                }
            }

            if (--_count[3] <= 0)
            {
                // White noise XORs both taps; periodic noise holds the lower one at 0, so the register just rotates.
                bool feedback = ((_rng & WhiteNoiseTap1) != 0) != ((_rng & WhiteNoiseTap2) != 0 && (_registers[RegNoise] & NoiseWhite) != 0);
                _rng >>= 1;
                if (feedback)
                {
                    _rng |= FeedbackMask;
                }

                _output[3] = (int)(_rng & 1);
                _count[3] = _period[3];
            }

            int sum = 0;
            for (int c = 0; c < 4; c++)
            {
                if (_output[c] != 0)
                {
                    sum += _volume[c];
                }
            }

            output[i] = -sum;
        }
    }

    /// <summary>MAME's table: 8191 at volume 0, 2 dB less per step, truncated to integers; volume 15 is silent.</summary>
    private static int[] BuildVolumeTable()
    {
        int[] table = new int[16];
        double level = MaxChannelLevel;
        for (int i = 0; i < 15; i++)
        {
            table[i] = level > MaxChannelLevel ? MaxChannelLevel : (int)level;
            level /= VolumeStep;
        }

        table[15] = 0;
        return table;
    }
}
