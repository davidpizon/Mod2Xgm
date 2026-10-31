namespace Mod2Xgm.Render.Fm;

/// <summary>
/// The OPN2's FM core: six channels of four operators, the envelope counter and the LFO. A port of ymfm's
/// <c>fm_engine_base&lt;opna_registers&gt;</c> (<c>src/ymfm_fm.ipp</c>, BSD-3-Clause, THIRD-PARTY-NOTICES.md) without
/// the timers, status and IRQ, which nothing that renders needs (docs/adr/0056-ym3438-model.md).
/// </summary>
internal sealed class FmEngine
{
    /// <summary>Even with no register writes, prepare every this many samples, to notice notes that have faded out.</summary>
    private const uint PrepareInterval = 4096;

    /// <summary>
    /// Which operators each channel uses, in the order O1, O2, O3, O4. The register order is O1, O3, O2, O4, so the
    /// middle two are swapped.
    /// </summary>
    private static ReadOnlySpan<byte> OperatorMap =>
    [
        0, 6, 3, 9,
        1, 7, 4, 10,
        2, 8, 5, 11,
        12, 18, 15, 21,
        13, 19, 16, 22,
        14, 20, 17, 23,
    ];

    private readonly FmChannel[] _channels = new FmChannel[OpnRegisters.Channels];
    private readonly FmOperator[] _operators = new FmOperator[OpnRegisters.Operators];
    private uint _envCounter;
    private uint _activeChannels = OpnRegisters.AllChannels;
    private uint _modifiedChannels = OpnRegisters.AllChannels;
    private uint _prepareCount;

    /// <summary>Creates the channels and operators; call <see cref="Reset"/> before use.</summary>
    public FmEngine()
    {
        for (int opnum = 0; opnum < _operators.Length; opnum++)
        {
            _operators[opnum] = new FmOperator(Registers, OpnRegisters.OperatorOffset(opnum));
        }

        ReadOnlySpan<byte> map = OperatorMap;
        for (int chnum = 0; chnum < _channels.Length; chnum++)
        {
            FmOperator[] ops = [_operators[map[chnum * 4]], _operators[map[(chnum * 4) + 1]], _operators[map[(chnum * 4) + 2]], _operators[map[(chnum * 4) + 3]]];
            _channels[chnum] = new FmChannel(Registers, OpnRegisters.ChannelOffset(chnum), ops);
        }
    }

    /// <summary>The register file.</summary>
    public OpnRegisters Registers { get; } = new();

    /// <summary>
    /// Clears the registers (writing 0 to the mode register), the channels and the operators. Like ymfm, it leaves the
    /// envelope counter and the LFO where they are.
    /// </summary>
    public void Reset()
    {
        Registers.Reset();
        Write(OpnRegisters.RegMode, 0);
        foreach (FmChannel channel in _channels)
        {
            channel.Reset();
        }

        foreach (FmOperator op in _operators)
        {
            op.Reset();
        }
    }

    /// <summary>Advances the channels in <paramref name="chanmask"/> one sample.</summary>
    public void Clock(uint chanmask)
    {
        // Prepare after any write, and now and then anyway to catch notes that have faded out.
        if (_modifiedChannels != 0 || _prepareCount++ >= PrepareInterval)
        {
            _activeChannels = 0;
            for (int chnum = 0; chnum < _channels.Length; chnum++)
            {
                if (FmTables.Bitfield(chanmask, chnum) != 0 && _channels[chnum].Prepare())
                {
                    _activeChannels |= 1u << chnum;
                }
            }

            _modifiedChannels = _prepareCount = 0;
        }

        // The envelope counter's low 2 bits count samples up to the divider; the rest count envelope steps.
        if (FmTables.Bitfield(++_envCounter, 0, 2) == OpnRegisters.EgClockDivider)
        {
            _envCounter += 4 - OpnRegisters.EgClockDivider;
        }

        int lfoRawPm = Registers.ClockNoiseAndLfo();
        for (int chnum = 0; chnum < _channels.Length; chnum++)
        {
            if (FmTables.Bitfield(chanmask, chnum) != 0)
            {
                _channels[chnum].Clock(_envCounter, lfoRawPm);
            }
        }
    }

    /// <summary>Adds the outputs of the sounding channels in <paramref name="chanmask"/> to <paramref name="left"/> and <paramref name="right"/>.</summary>
    public void Output(ref int left, ref int right, int rshift, int clipMax, uint chanmask)
    {
        chanmask &= _activeChannels;
        for (int chnum = 0; chnum < _channels.Length; chnum++)
        {
            if (FmTables.Bitfield(chanmask, chnum) != 0)
            {
                _channels[chnum].Output4Op(ref left, ref right, rshift, clipMax);
            }
        }
    }

    /// <summary>Writes FM register <paramref name="regnum"/> (<c>$100</c> added for port 1).</summary>
    public void Write(int regnum, byte data)
    {
        _modifiedChannels = OpnRegisters.AllChannels;
        if (Registers.Write(regnum, data, out int keyonChannel, out uint keyonOpmask))
        {
            _channels[keyonChannel].KeyOnOff(keyonOpmask);
        }
    }
}
