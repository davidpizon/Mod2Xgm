namespace Mod2Xgm.Render.Fm;

/// <summary>
/// One FM channel: four operators wired by one of eight algorithms, with self-feedback on operator 1. A port of ymfm's
/// <c>fm_channel</c> (4-operator path, <c>src/ymfm_fm.ipp</c>, BSD-3-Clause, THIRD-PARTY-NOTICES.md).
/// </summary>
internal sealed class FmChannel
{
    /// <summary>
    /// The algorithms, as ymfm encodes them. Bits 0, 1–3 and 4–6 pick operator 2's, 3's and 4's modulator from the
    /// table of outputs computed so far (0 = none, 1–3 = O1–O3, 5 = O1+O2, 6 = O1+O3, 7 = O2+O3); bits 7–9 add O1, O2
    /// and O3 to the output (O4 always sounds).
    /// </summary>
    private static readonly ushort[] AlgorithmOps =
    [
        Algorithm(1, 2, 3, 0, 0, 0), // 0: O1 -> O2 -> O3 -> O4 -> out
        Algorithm(0, 5, 3, 0, 0, 0), // 1: (O1 + O2) -> O3 -> O4 -> out
        Algorithm(0, 2, 6, 0, 0, 0), // 2: (O1 + (O2 -> O3)) -> O4 -> out
        Algorithm(1, 0, 7, 0, 0, 0), // 3: ((O1 -> O2) + O3) -> O4 -> out
        Algorithm(1, 0, 3, 0, 1, 0), // 4: ((O1 -> O2) + (O3 -> O4)) -> out
        Algorithm(1, 1, 1, 0, 1, 1), // 5: ((O1 -> O2) + (O1 -> O3) + (O1 -> O4)) -> out
        Algorithm(1, 0, 0, 0, 1, 1), // 6: ((O1 -> O2) + O3 + O4) -> out
        Algorithm(0, 0, 0, 1, 1, 1), // 7: (O1 + O2 + O3 + O4) -> out
    ];

    private readonly OpnRegisters _regs;
    private readonly FmOperator[] _op;
    private readonly short[] _feedback = new short[2];
    private short _feedbackIn;

    /// <summary>Creates the channel at register offset <paramref name="choffs"/> with its four operators, in order O1–O4.</summary>
    public FmChannel(OpnRegisters regs, uint choffs, FmOperator[] operators)
    {
        _regs = regs;
        ChOffset = choffs;
        _op = operators;
        foreach (FmOperator op in operators)
        {
            op.ChOffset = choffs;
        }
    }

    /// <summary>The channel's register offset.</summary>
    private uint ChOffset { get; }

    /// <summary>Clears the feedback memory.</summary>
    public void Reset()
    {
        _feedback[0] = _feedback[1] = 0;
        _feedbackIn = 0;
    }

    /// <summary>Passes a key on/off write to the operators: bit <i>n</i> of <paramref name="states"/> is operator <i>n</i>+1.</summary>
    public void KeyOnOff(uint states)
    {
        for (int opnum = 0; opnum < _op.Length; opnum++)
        {
            _op[opnum].KeyOnOff(FmTables.Bitfield(states, opnum));
        }
    }

    /// <summary>Prepares every operator; true while any can still be heard.</summary>
    public bool Prepare()
    {
        uint activeMask = 0;
        for (int opnum = 0; opnum < _op.Length; opnum++)
        {
            if (_op[opnum].Prepare())
            {
                activeMask |= 1u << opnum;
            }
        }

        return activeMask != 0;
    }

    /// <summary>Advances the feedback memory and every operator one sample.</summary>
    public void Clock(uint envCounter, int lfoRawPm)
    {
        _feedback[0] = _feedback[1];
        _feedback[1] = _feedbackIn;
        foreach (FmOperator op in _op)
        {
            op.Clock(envCounter, lfoRawPm);
        }
    }

    /// <summary>
    /// Adds this sample's output to <paramref name="left"/> and <paramref name="right"/> per the channel's panning: the
    /// sum is shifted right by <paramref name="rshift"/> and clipped to ±<paramref name="clipMax"/> along the way.
    /// </summary>
    public void Output4Op(ref int left, ref int right, int rshift, int clipMax)
    {
        uint amOffset = _regs.LfoAmOffset(ChOffset);

        int opmod = 0;
        uint feedback = _regs.ChFeedback(ChOffset);
        if (feedback != 0)
        {
            opmod = (_feedback[0] + _feedback[1]) >> (int)(10 - feedback);
        }

        // Operator 1 is computed even when the channel is muted, so its feedback keeps going.
        _feedbackIn = (short)_op[0].ComputeVolume(_op[0].Phase + (uint)opmod, amOffset);
        int op1Value = _feedbackIn;
        if (_regs.ChOutputAny(ChOffset) == 0)
        {
            return;
        }

        uint algorithmOps = AlgorithmOps[(int)_regs.ChAlgorithm(ChOffset)];
        Span<short> opout = stackalloc short[8];
        opout[0] = 0;
        opout[1] = (short)op1Value;

        opmod = opout[(int)FmTables.Bitfield(algorithmOps, 0)] >> 1;
        opout[2] = (short)_op[1].ComputeVolume(_op[1].Phase + (uint)opmod, amOffset);
        opout[5] = (short)(opout[1] + opout[2]);

        opmod = opout[(int)FmTables.Bitfield(algorithmOps, 1, 3)] >> 1;
        opout[3] = (short)_op[2].ComputeVolume(_op[2].Phase + (uint)opmod, amOffset);
        opout[6] = (short)(opout[1] + opout[3]);
        opout[7] = (short)(opout[2] + opout[3]);

        opmod = opout[(int)FmTables.Bitfield(algorithmOps, 4, 3)] >> 1;
        int result = _op[3].ComputeVolume(_op[3].Phase + (uint)opmod, amOffset) >> rshift;

        int clipMin = -clipMax - 1;
        if (FmTables.Bitfield(algorithmOps, 7) != 0)
        {
            result = FmTables.Clamp(result + (opout[1] >> rshift), clipMin, clipMax);
        }

        if (FmTables.Bitfield(algorithmOps, 8) != 0)
        {
            result = FmTables.Clamp(result + (opout[2] >> rshift), clipMin, clipMax);
        }

        if (FmTables.Bitfield(algorithmOps, 9) != 0)
        {
            result = FmTables.Clamp(result + (opout[3] >> rshift), clipMin, clipMax);
        }

        if (_regs.ChOutput0(ChOffset) != 0)
        {
            left += result;
        }

        if (_regs.ChOutput1(ChOffset) != 0)
        {
            right += result;
        }
    }

    private static ushort Algorithm(int op2In, int op3In, int op4In, int op1Out, int op2Out, int op3Out) =>
        (ushort)(op2In | (op3In << 1) | (op4In << 4) | (op1Out << 7) | (op2Out << 8) | (op3Out << 9));
}
