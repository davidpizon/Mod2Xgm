namespace Mod2Xgm.Render.Fm;

/// <summary>
/// The OPN2's register file and the values derived from it: ymfm's <c>opn_registers_base&lt;true&gt;</c> (OPNA layout,
/// <c>src/ymfm_opn.h</c> / <c>src/ymfm_opn.cpp</c>, BSD-3-Clause, THIRD-PARTY-NOTICES.md), plus the LFO, which lives here
/// in ymfm too.
/// <para>Registers <c>$000–$0FF</c> are port 0 (channels 1–3 and the system registers), <c>$100–$1FF</c> port 1
/// (channels 4–6). A channel's or operator's "offset" is added to its register's base address.</para>
/// </summary>
internal sealed class OpnRegisters
{
    /// <summary>FM channels.</summary>
    public const int Channels = 6;

    /// <summary>A mask with every channel's bit set.</summary>
    public const uint AllChannels = (1u << Channels) - 1;

    /// <summary>Operators: four per channel.</summary>
    public const int Operators = Channels * 4;

    /// <summary>The mode register: CH3 special mode and the timers.</summary>
    public const int RegMode = 0x27;

    /// <summary>The key on/off register.</summary>
    private const int RegKeyOn = 0x28;

    /// <summary>The envelope generator steps once every this many samples.</summary>
    public const uint EgClockDivider = 3;

    /// <summary>The width of the envelope timer that counts those steps (docs/adr/0064-ym3438-chip-differences.md).</summary>
    public const int EgTimerBits = 12;

    /// <summary>Register bytes: two ports of 256.</summary>
    private const int RegisterCount = 0x200;

    /// <summary>
    /// LFO divider per rate setting (<c>$22</c> bits 0–2), from the application manual's frequencies, assuming a 7-bit
    /// LFO value.
    /// </summary>
    private static ReadOnlySpan<byte> LfoMaxCount => [109, 78, 72, 68, 63, 45, 9, 6];

    private readonly byte[] _regdata = new byte[RegisterCount];
    private uint _lfoCounter;
    private byte _lfoAm;

    /// <summary>Clears every register, then sets each channel's output to both speakers (the chip's power-on state).</summary>
    public void Reset()
    {
        Array.Clear(_regdata);
        _regdata[0xb4] = _regdata[0xb5] = _regdata[0xb6] = 0xc0;
        _regdata[0x1b4] = _regdata[0x1b5] = _regdata[0x1b6] = 0xc0;
    }

    /// <summary>The register offset of channel <paramref name="chnum"/> (0–5).</summary>
    public static uint ChannelOffset(int chnum) => (uint)((chnum % 3) + (0x100 * (chnum / 3)));

    /// <summary>The register offset of operator <paramref name="opnum"/> (0–23, in register order within each port).</summary>
    public static uint OperatorOffset(int opnum) => (uint)((opnum % 12) + ((opnum % 12) / 3) + (0x100 * (opnum / 12)));

    /// <summary>
    /// Stores a register write. The F-number pairs (<c>$A0–$AF</c>) latch: the high byte (<c>$A4</c>…) waits in an unused
    /// register (<c>$B8–$BF</c>) until the low byte is written, so both change at once.
    /// </summary>
    /// <param name="index">Register address, with <c>$100</c> for port 1.</param>
    /// <param name="data">The value.</param>
    /// <param name="channel">For a key on/off write, the channel (0–5).</param>
    /// <param name="opmask">For a key on/off write, which operators are keyed on (bit 0 = operator 1).</param>
    /// <returns>True for a key on/off write to a real channel.</returns>
    public bool Write(int index, byte data, out int channel, out uint opmask)
    {
        channel = 0;
        opmask = 0;
        if ((index & 0xf0) == 0xa0)
        {
            if (Bitfield((uint)index, 0, 2) == 3)
            {
                return false;
            }

            int latchIndex = 0xb8 | (int)Bitfield((uint)index, 3);
            if (Bitfield((uint)index, 2) != 0)
            {
                _regdata[latchIndex] = (byte)(data & 0x3f);
            }
            else
            {
                _regdata[index] = data;
                _regdata[index | 4] = _regdata[latchIndex];
            }

            return false;
        }

        if ((index & 0xf8) == 0xb8)
        {
            return false;
        }

        _regdata[index] = data;
        if (index == RegKeyOn)
        {
            channel = (int)Bitfield(data, 0, 2);
            if (channel == 3)
            {
                return false;
            }

            channel += (int)Bitfield(data, 2) * 3;
            opmask = Bitfield(data, 4, 4);
            return true;
        }

        return false;
    }

    /// <summary>Steps the LFO one sample and returns its vibrato value (signed, 5 bits); the tremolo value is kept for <see cref="LfoAmOffset"/>.</summary>
    public int ClockNoiseAndLfo()
    {
        if (LfoEnable == 0)
        {
            // With the LFO off its counter stays at 0, where the AM value is 0x3f: an operator with AM on is still
            // attenuated (Venom on the Mega Drive relies on it).
            _lfoCounter = 0;
            _lfoAm = 0x3f;
            return 0;
        }

        uint subcount = (byte)_lfoCounter++;

        // Crossing the divider count adds enough to carry into bit 8; the 7-bit value lives in bits 8–14. Hardware and
        // Nuked-OPN2 show an off-by-one that makes the top rate faster than published.
        if (subcount >= LfoMaxCount[(int)LfoRate])
        {
            _lfoCounter += 0x101 - subcount;
        }

        _lfoAm = (byte)Bitfield(_lfoCounter, 8, 6);
        if (Bitfield(_lfoCounter, 8 + 6) == 0)
        {
            _lfoAm ^= 0x3f;
        }

        int pm = (int)Bitfield(_lfoCounter, 10, 3);
        if (Bitfield(_lfoCounter, 10 + 3) != 0)
        {
            pm ^= 7;
        }

        return Bitfield(_lfoCounter, 10 + 4) != 0 ? -pm : pm;
    }

    /// <summary>The LFO's tremolo for channel <paramref name="choffs"/>, as attenuation, after its AM depth (0, 1.4, 5.9, 11.8 dB).</summary>
    public uint LfoAmOffset(uint choffs)
    {
        int amShift = (1 << (int)(ChLfoAmSens(choffs) ^ 3)) - 1;
        return (uint)((_lfoAm << 1) >> amShift);
    }

    /// <summary>Fills <paramref name="cache"/> with what the operator needs while it sounds: phase step, levels and envelope rates.</summary>
    public void CacheOperatorData(uint choffs, uint opoffs, OperatorCache cache)
    {
        uint blockFreq = cache.BlockFreq = ChBlockFreq(choffs);

        // CH3 special mode: operators 1–3 of channel 3 take their own frequencies.
        if (MultiFreq != 0 && choffs == 2)
        {
            if (opoffs == 2)
            {
                blockFreq = cache.BlockFreq = MultiBlockFreq(1);
            }
            else if (opoffs == 10)
            {
                blockFreq = cache.BlockFreq = MultiBlockFreq(2);
            }
            else if (opoffs == 6)
            {
                blockFreq = cache.BlockFreq = MultiBlockFreq(0);
            }
        }

        // The 5-bit key code: block and the top F-number bit, plus a low bit from the next three F-number bits
        // (YM2608 manual: F11 & (F10 | F9 | F8) | !F11 & F10 & F9 & F8), looked up in a 16-bit constant.
        uint keycode = Bitfield(blockFreq, 10, 4) << 1;
        keycode |= Bitfield(0xfe80, (int)Bitfield(blockFreq, 7, 4));

        cache.Detune = FmTables.DetuneAdjustment(OpDetune(opoffs), keycode);

        // The multiple as an x.1 value: 0 means 0.5.
        cache.Multiple = OpMultiple(opoffs) * 2;
        if (cache.Multiple == 0)
        {
            cache.Multiple = 1;
        }

        cache.PhaseStep = LfoEnable == 0 || ChLfoPmSens(choffs) == 0
            ? ComputePhaseStep(choffs, cache, 0)
            : OperatorCache.PhaseStepDynamic;

        cache.TotalLevel = OpTotalLevel(opoffs) << 3;

        // A 4-bit sustain level where 15 means 31.
        cache.EgSustain = OpSustainLevel(opoffs);
        cache.EgSustain |= (cache.EgSustain + 1) & 0x10;
        cache.EgSustain <<= 5;

        uint ksrval = keycode >> (int)(OpKsr(opoffs) ^ 3);
        cache.EgRate[(int)EnvelopeState.Attack] = EffectiveRate(OpAttackRate(opoffs) * 2, ksrval);
        cache.EgRate[(int)EnvelopeState.Decay] = EffectiveRate(OpDecayRate(opoffs) * 2, ksrval);
        cache.EgRate[(int)EnvelopeState.Sustain] = EffectiveRate(OpSustainRate(opoffs) * 2, ksrval);
        cache.EgRate[(int)EnvelopeState.Release] = EffectiveRate((OpReleaseRate(opoffs) * 4) + 2, ksrval);
    }

    /// <summary>The phase step for one sample, from the cached frequency, detune and multiple, and the LFO's vibrato.</summary>
    public uint ComputePhaseStep(uint choffs, OperatorCache cache, int lfoRawPm)
    {
        uint fnum = Bitfield(cache.BlockFreq, 0, 11) << 1;
        uint pmSensitivity = ChLfoPmSens(choffs);
        if (pmSensitivity != 0)
        {
            fnum += (uint)FmTables.LfoPmPhaseAdjustment(Bitfield(cache.BlockFreq, 4, 7), pmSensitivity, lfoRawPm);
            fnum &= 0xfff;
        }

        int block = (int)Bitfield(cache.BlockFreq, 11, 3);
        uint phaseStep = (fnum << block) >> 2;
        phaseStep += (uint)cache.Detune;

        // Keep 17 bits in case the detune overflows.
        phaseStep &= 0x1ffff;
        return (phaseStep * cache.Multiple) >> 1;
    }

    /// <summary>LFO on (<c>$22</c> bit 3).</summary>
    private uint LfoEnable => Bits(0x22, 3, 1);

    /// <summary>LFO rate (<c>$22</c> bits 0–2).</summary>
    private uint LfoRate => Bits(0x22, 0, 3);

    /// <summary>CH3 special (or CSM) mode: <c>$27</c> bits 6–7 not 0.</summary>
    private uint MultiFreq => Bits(RegMode, 6, 2) != 0 ? 1u : 0u;

    /// <summary>Block and F-number of CH3 special frequency <paramref name="num"/> (<c>$AC</c>/<c>$A8</c>…).</summary>
    private uint MultiBlockFreq(uint num) => Word(0xac, 0, 6, 0xa8, 0, 8, num);

    /// <summary>Block and F-number of a channel (<c>$A4</c>/<c>$A0</c>).</summary>
    private uint ChBlockFreq(uint choffs) => Word(0xa4, 0, 6, 0xa0, 0, 8, choffs);

    /// <summary>Operator 1's self-feedback (<c>$B0</c> bits 3–5).</summary>
    public uint ChFeedback(uint choffs) => Bits(0xb0, 3, 3, choffs);

    /// <summary>The algorithm (<c>$B0</c> bits 0–2).</summary>
    public uint ChAlgorithm(uint choffs) => Bits(0xb0, 0, 3, choffs);

    /// <summary>Either speaker on (<c>$B4</c> bits 6–7).</summary>
    public uint ChOutputAny(uint choffs) => Bits(0xb4, 6, 2, choffs);

    /// <summary>Left speaker on (<c>$B4</c> bit 7).</summary>
    public uint ChOutput0(uint choffs) => Bits(0xb4, 7, 1, choffs);

    /// <summary>Right speaker on (<c>$B4</c> bit 6).</summary>
    public uint ChOutput1(uint choffs) => Bits(0xb4, 6, 1, choffs);

    /// <summary>LFO tremolo depth (<c>$B4</c> bits 4–5).</summary>
    private uint ChLfoAmSens(uint choffs) => Bits(0xb4, 4, 2, choffs);

    /// <summary>LFO vibrato depth (<c>$B4</c> bits 0–2).</summary>
    private uint ChLfoPmSens(uint choffs) => Bits(0xb4, 0, 3, choffs);

    /// <summary>Detune (<c>$30</c> bits 4–6).</summary>
    private uint OpDetune(uint opoffs) => Bits(0x30, 4, 3, opoffs);

    /// <summary>Frequency multiple (<c>$30</c> bits 0–3).</summary>
    private uint OpMultiple(uint opoffs) => Bits(0x30, 0, 4, opoffs);

    /// <summary>Total level (<c>$40</c> bits 0–6).</summary>
    private uint OpTotalLevel(uint opoffs) => Bits(0x40, 0, 7, opoffs);

    /// <summary>Key scale rate (<c>$50</c> bits 6–7).</summary>
    private uint OpKsr(uint opoffs) => Bits(0x50, 6, 2, opoffs);

    /// <summary>Attack rate (<c>$50</c> bits 0–4).</summary>
    private uint OpAttackRate(uint opoffs) => Bits(0x50, 0, 5, opoffs);

    /// <summary>Decay rate (<c>$60</c> bits 0–4).</summary>
    private uint OpDecayRate(uint opoffs) => Bits(0x60, 0, 5, opoffs);

    /// <summary>LFO tremolo on for this operator (<c>$60</c> bit 7).</summary>
    public uint OpLfoAmEnable(uint opoffs) => Bits(0x60, 7, 1, opoffs);

    /// <summary>Sustain rate (<c>$70</c> bits 0–4).</summary>
    private uint OpSustainRate(uint opoffs) => Bits(0x70, 0, 5, opoffs);

    /// <summary>Sustain level (<c>$80</c> bits 4–7).</summary>
    private uint OpSustainLevel(uint opoffs) => Bits(0x80, 4, 4, opoffs);

    /// <summary>Release rate (<c>$80</c> bits 0–3).</summary>
    private uint OpReleaseRate(uint opoffs) => Bits(0x80, 0, 4, opoffs);

    /// <summary>SSG-EG on (<c>$90</c> bit 3).</summary>
    public uint OpSsgEgEnable(uint opoffs) => Bits(0x90, 3, 1, opoffs);

    /// <summary>SSG-EG shape (<c>$90</c> bits 0–2).</summary>
    public uint OpSsgEgMode(uint opoffs) => Bits(0x90, 0, 3, opoffs);

    /// <summary>A raw envelope rate with key scaling added; 0 stays 0 and the result is at most 63.</summary>
    private static byte EffectiveRate(uint rawRate, uint ksr) => (byte)(rawRate == 0 ? 0 : Math.Min(rawRate + ksr, 63));

    private static uint Bitfield(uint value, int start, int length = 1) => FmTables.Bitfield(value, start, length);

    private uint Bits(int offset, int start, int count, uint extraOffset = 0) =>
        Bitfield(_regdata[offset + (int)extraOffset], start, count);

    /// <summary>A field split over two registers, high bits first.</summary>
    private uint Word(int offset1, int start1, int count1, int offset2, int start2, int count2, uint extraOffset = 0) =>
        (Bits(offset1, start1, count1, extraOffset) << count2) | Bits(offset2, start2, count2, extraOffset);
}
