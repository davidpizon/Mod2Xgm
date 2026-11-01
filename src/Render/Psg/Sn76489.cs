namespace Mod2Xgm.Render.Psg;

/// <summary>
/// The Genesis PSG: Sega's SN76489 inside the VDP, three square-wave tone channels and one noise channel (plan T7.3,
/// docs/adr/0057-sn76489-model.md): a C# port of MAME's <c>src/devices/sound/sn76496.cpp</c> (commit d066f16,
/// BSD-3-Clause, THIRD-PARTY-NOTICES.md) in its Sega VDP setup, changed where MAME differs from the chip as Nuked-PSG
/// models it (plan T7.8, docs/adr/0065-sn76489-chip-differences.md), and checked against both by tests
/// (tests/golden/sn76489/README.md).
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

    /// <summary>The counters are 10 bits wide, like the tone periods (docs/adr/0065-sn76489-chip-differences.md).</summary>
    private const int CounterMask = 0x3ff;

    /// <summary>The bit the noise shift register's feedback goes into: a 16-bit register (Sega).</summary>
    private const uint FeedbackMask = 0x8000;

    /// <summary>The white-noise taps: bits 0 and 3 (Sega).</summary>
    private const uint WhiteNoiseTap1 = 0x01;

    private const uint WhiteNoiseTap2 = 0x08;

    /// <summary>The noise register (register 6): bits 0–1 the rate, bit 2 white (1) or periodic (0) noise.</summary>
    private const int RegNoise = 6;

    /// <summary>The noise channel's index in the per-channel arrays.</summary>
    private const int NoiseChannel = 3;

    /// <summary>The tone channel the noise follows at rate 3: tone 3 (index 2).</summary>
    private const int Tone3 = 2;

    /// <summary>The noise rate that follows tone channel 3 instead of a fixed divider.</summary>
    private const int NoiseRateTone3 = 3;

    /// <summary>The noise register's white-noise bit.</summary>
    private const int NoiseWhite = 0x04;

    /// <summary>The register the latch points at after reset on Sega's chip: channel 2's volume (MAME, from hardware tests).</summary>
    private const int ResetLatch = 3;

    /// <summary>
    /// Channel levels by volume register value, 0 (loudest) to 15 (off): Nuked-PSG's, where MAME's are even 2 dB steps
    /// (docs/adr/0067-psg-volume-levels.md).
    /// </summary>
    private static readonly int[] VolumeTable = PsgLevels.Channel.ToArray();

    private readonly int[] _registers = new int[8];
    private readonly int[] _volume = new int[4];

    /// <summary>What each counter counts up to: the tone periods, and 16, 32 or 64 for the noise's own counter.</summary>
    private readonly int[] _period = new int[4];

    /// <summary>The up-counters, one per channel (docs/adr/0065-sn76489-chip-differences.md, difference 2).</summary>
    private readonly int[] _count = new int[4];

    /// <summary>The flip-flops the counters toggle: the tones' outputs, and for the noise the clock of its shift register.</summary>
    private readonly int[] _toggle = new int[4];

    /// <summary>Counters that reached their period on the last sample: the chip shows the flip one sample later.</summary>
    private readonly bool[] _reached = new bool[4];

    private int _lastRegister;
    private uint _rng;

    /// <summary>The level of the noise's clock (its own flip-flop, or tone 3) on the last sample: it shifts on a rise.</summary>
    private int _noiseClock;

    /// <summary>A write to the noise register waiting for the next sample, which clears the shift register.</summary>
    private bool _noiseRestart;

    /// <summary>Creates the chip in its power-on state.</summary>
    public Sn76489() => Reset();

    /// <summary>Output samples per second at PSG clock <paramref name="clockHz"/>.</summary>
    public static double SampleRateHz(double clockHz) => clockHz / ClocksPerSample;

    /// <summary>
    /// The power-on state: periods 0, every channel at volume 15 (off), the latch on register 3 (as MAME starts the Sega
    /// PSG), and the noise at rate 0 with an empty shift register. The chip clears its flip-flops on reset and then, with
    /// every period 0, flips them on every step, so where they stand at the first write depends on the time since reset;
    /// this is where the reference tool's pre-roll leaves Nuked-PSG: every flip-flop high and every counter at 1
    /// (docs/adr/0065-sn76489-chip-differences.md).
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
            _toggle[i] = 1;
            _count[i] = 1;
            _reached[i] = false;
            _period[i] = 0;
            _volume[i] = VolumeTable[_registers[(i * 2) + 1]];
        }

        _period[NoiseChannel] = NoisePeriod(_registers[RegNoise]);
        _rng = FeedbackMask;
        _noiseClock = 0;
        _noiseRestart = false;
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

                // Sega's chip keeps a period of 0 as 0 (TI's acts as $400). The counter keeps its count: the new period
                // counts from where the old one got to (ADR 0065, difference 2).
                _period[c] = _registers[r];
                break;
            case RegNoise:
                if ((data & 0x80) == 0)
                {
                    _registers[r] = (_registers[r] & 0x3f0) | (data & 0x0f);
                }

                _period[NoiseChannel] = NoisePeriod(_registers[RegNoise]);
                _noiseRestart = true;
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
            // Every counter counts up and flips its flip-flop when the count reaches the period, compared with the
            // period as it is now: a shorter period flips at once if the count is already past it, a longer one stretches
            // the half-wave. The flip shows a sample after the count reaches it (ADR 0065, difference 2). MAME counts
            // the old period down first.
            for (int c = 0; c < 4; c++)
            {
                if (_reached[c])
                {
                    _toggle[c] ^= 1;
                }

                _reached[c] = _count[c] >= _period[c];
                if (_reached[c])
                {
                    _count[c] = 0;
                }

                _count[c] = (_count[c] + 1) & CounterMask;
            }

            // The noise shifts on the rise of its clock: its own flip-flop, or tone 3's output at rate 3; so once every
            // 32, 64 or 128 samples, or every second flip of tone 3, in step with it. A write to the noise register
            // empties the shift register on the next sample instead, so the output goes low at once; the bit MAME loads
            // into bit 15 is the one the chip shifts in first (ADR 0065, difference 3).
            int clock = (_registers[RegNoise] & 3) == NoiseRateTone3 ? _toggle[Tone3] : _toggle[NoiseChannel];
            if (_noiseRestart)
            {
                _rng = FeedbackMask;
                _noiseRestart = false;
            }
            else if (clock != 0 && _noiseClock == 0)
            {
                // White noise XORs both taps; periodic noise holds the lower one at 0, so the register just rotates.
                bool feedback = ((_rng & WhiteNoiseTap1) != 0) != ((_rng & WhiteNoiseTap2) != 0 && (_registers[RegNoise] & NoiseWhite) != 0);
                _rng >>= 1;
                if (feedback)
                {
                    _rng |= FeedbackMask;
                }
            }

            _noiseClock = clock;

            int sum = 0;
            for (int c = 0; c < 3; c++)
            {
                if (_toggle[c] != 0)
                {
                    sum += _volume[c];
                }
            }

            if ((_rng & 1) != 0)
            {
                sum += _volume[NoiseChannel];
            }

            output[i] = -sum;
        }
    }

    /// <summary>
    /// What the noise's own counter counts to for the noise register <paramref name="noise"/>: 16, 32 or 64 per flip at
    /// rates 0–2, so a shift every 32, 64 or 128 samples (clock / 512, / 1024, / 2048); 0 at rate 3, where tone 3
    /// clocks the noise instead.
    /// </summary>
    private static int NoisePeriod(int noise) => (noise & 3) == NoiseRateTone3 ? 0 : 16 << (noise & 3);
}
