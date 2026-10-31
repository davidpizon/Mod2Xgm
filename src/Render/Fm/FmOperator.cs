namespace Mod2Xgm.Render.Fm;

/// <summary>Envelope phases; the numbers are ymfm's, which index <see cref="OperatorCache.EgRate"/>.</summary>
internal enum EnvelopeState : byte
{
    /// <summary>Rising to full volume after a key on.</summary>
    Attack = 1,

    /// <summary>Falling to the sustain level.</summary>
    Decay = 2,

    /// <summary>Falling further at the sustain rate while the key is held.</summary>
    Sustain = 3,

    /// <summary>Falling to silence after a key off.</summary>
    Release = 4,
}

/// <summary>What an operator derives from the registers once per change (ymfm's <c>opdata_cache</c>).</summary>
internal sealed class OperatorCache
{
    /// <summary>A <see cref="PhaseStep"/> of this value means "recompute every sample": the LFO's vibrato is on.</summary>
    public const uint PhaseStepDynamic = 1;

    /// <summary>The phase step per sample (10.10), or <see cref="PhaseStepDynamic"/>.</summary>
    public uint PhaseStep { get; set; }

    /// <summary>Total level × 8, ready to add to an attenuation.</summary>
    public uint TotalLevel { get; set; }

    /// <summary>The raw block and F-number (14 bits) the phase step is computed from.</summary>
    public uint BlockFreq { get; set; }

    /// <summary>The detune's phase displacement.</summary>
    public int Detune { get; set; }

    /// <summary>The frequency multiple as an x.1 value (1 means 0.5).</summary>
    public uint Multiple { get; set; }

    /// <summary>The sustain level in envelope units.</summary>
    public uint EgSustain { get; set; }

    /// <summary>The 6-bit envelope rate per <see cref="EnvelopeState"/>, key scaling included.</summary>
    public byte[] EgRate { get; } = new byte[(int)EnvelopeState.Release + 1];
}

/// <summary>
/// One operator ("slot"): a sine whose phase advances with the channel's frequency, shaped by an envelope. A port of
/// ymfm's <c>fm_operator</c> for the OPN2 (<c>src/ymfm_fm.ipp</c>, BSD-3-Clause, THIRD-PARTY-NOTICES.md).
/// </summary>
internal sealed class FmOperator
{
    /// <summary>Above this envelope attenuation the operator is silent, so the work is skipped.</summary>
    private const uint EgQuiet = 0x380;

    private readonly OpnRegisters _regs;
    private readonly OperatorCache _cache = new();
    private uint _phase;
    private ushort _envAttenuation = 0x3ff;
    private EnvelopeState _envState = EnvelopeState.Release;
    private byte _ssgInverted;
    private byte _keyState;
    private byte _keyonLive;

    /// <summary>The SSG-EG inversion this sample sounds with when a turn is due next sample, or -1 when none is.</summary>
    private int _ssgTurnInverted = -1;

    /// <summary>An SSG-EG repeat in shapes 0 and 4 holds the phase at 0 for the sample it happens in.</summary>
    private bool _ssgPhaseRestart;

    /// <summary>True in the sample a key on starts the attack, which takes no envelope step.</summary>
    private bool _keyedOn;

    /// <summary>Creates the operator at register offset <paramref name="opoffs"/>.</summary>
    public FmOperator(OpnRegisters regs, uint opoffs)
    {
        _regs = regs;
        OpOffset = opoffs;
    }

    /// <summary>The operator's register offset.</summary>
    private uint OpOffset { get; }

    /// <summary>The register offset of the channel it belongs to.</summary>
    public uint ChOffset { get; set; }

    /// <summary>The current phase, 10 bits for a full period (plus the higher bits that wrap).</summary>
    public uint Phase => _phase >> 10;

    /// <summary>Back to the power-on state: silent, in release, phase 0.</summary>
    public void Reset()
    {
        _phase = 0;
        _envAttenuation = 0x3ff;
        _envState = EnvelopeState.Release;
        _ssgInverted = 0;
        _keyState = 0;
        _keyonLive = 0;
        _ssgTurnInverted = -1;
        _ssgPhaseRestart = false;
        _keyedOn = false;
    }

    /// <summary>Reads the registers into the cache and applies a pending key on or off.</summary>
    /// <returns>True while the operator can still be heard.</returns>
    public bool Prepare()
    {
        _regs.CacheOperatorData(ChOffset, OpOffset, _cache);
        ClockKeystate(_keyonLive != 0 ? 1u : 0u);
        return _envState != EnvelopeState.Release || _envAttenuation < EgQuiet;
    }

    /// <summary>Advances one sample: SSG-EG, the envelope on envelope steps, and the phase.</summary>
    /// <param name="envCounter">The engine's envelope counter (x.2: the low 2 bits count the divider).</param>
    /// <param name="lfoRawPm">The LFO's vibrato value this sample.</param>
    public void Clock(uint envCounter, int lfoRawPm)
    {
        _ssgPhaseRestart = false;
        if (_regs.OpSsgEgEnable(OpOffset) != 0)
        {
            ClockSsgEgState();
        }
        else
        {
            _ssgInverted = 0;
        }

        if (FmTables.Bitfield(envCounter, 0, 2) == 0)
        {
            ClockEnvelope(envCounter >> 2);
        }

        // A restarted phase sounds at 0 for one sample before it steps (ymfm steps it at once).
        if (!_ssgPhaseRestart)
        {
            ClockPhase(lfoRawPm);
        }

        // The sample whose attenuation reaches the midpoint already sounds with the inversion the turn will make; the
        // turn itself (restart, hold, phase reset) follows a sample later. At the quiet end of the alternating shapes
        // that is a full-level blip, at the loud end a silent sample (Nuked-OPN2; ymfm turns the inversion with the
        // rest, a sample later, docs/adr/0064-ym3438-chip-differences.md). A released note never inverts.
        _ssgTurnInverted = _regs.OpSsgEgEnable(OpOffset) != 0 && _keyState != 0 && FmTables.Bitfield(_envAttenuation, 9) != 0
            ? (int)InvertedAfterTurn()
            : -1;
        _keyedOn = false;
    }

    /// <summary>The operator's output (14-bit signed) at <paramref name="phase"/> (phase plus modulation).</summary>
    /// <param name="phase">The phase to read the sine at; the low 10 bits are one period.</param>
    /// <param name="amOffset">The LFO's tremolo for the channel.</param>
    public int ComputeVolume(uint phase, uint amOffset)
    {
        if (_envAttenuation > EgQuiet)
        {
            return 0;
        }

        uint sinAttenuation = FmTables.Waveform[phase & (FmTables.WaveformLength - 1)];
        uint envAttenuation = EnvelopeAttenuation(amOffset) << 2;
        int result = (int)FmTables.AttenuationToVolume((sinAttenuation & 0x7fff) + envAttenuation);
        return FmTables.Bitfield(sinAttenuation, 15) != 0 ? -result : result;
    }

    /// <summary>Sets the key state that the next <see cref="Prepare"/> applies.</summary>
    public void KeyOnOff(uint on) => _keyonLive = (byte)((_keyonLive & ~1) | (int)FmTables.Bitfield(on, 0));

    /// <summary>Starts the attack after a key on, or again when an SSG-EG cycle repeats (<paramref name="isRestart"/>).</summary>
    private void StartAttack(bool isRestart = false)
    {
        if (_envState == EnvelopeState.Attack)
        {
            return;
        }

        _envState = EnvelopeState.Attack;

        // A key on starts the phase at 0 and un-inverts, except in the inverted SSG-EG shapes; an SSG-EG repeat leaves
        // both to the SSG-EG code.
        if (!isRestart)
        {
            _keyedOn = true;
            _ssgInverted = (byte)(_regs.OpSsgEgEnable(OpOffset) & FmTables.Bitfield(_regs.OpSsgEgMode(OpOffset), 2));
            _phase = 0;
        }

        // Attack rates 62 and 63 jump straight to full volume.
        if (_cache.EgRate[(int)EnvelopeState.Attack] >= 62)
        {
            _envAttenuation = 0;
        }
    }

    /// <summary>Starts the release after a key off.</summary>
    private void StartRelease()
    {
        if (_envState >= EnvelopeState.Release)
        {
            return;
        }

        _envState = EnvelopeState.Release;

        // An inverted SSG-EG envelope releases from where it sounds, not from where it counts.
        if (_ssgInverted != 0)
        {
            _envAttenuation = (ushort)((0x200 - _envAttenuation) & 0x3ff);
            _ssgInverted = 0;
        }
    }

    private void ClockKeystate(uint keystate)
    {
        if ((keystate ^ _keyState) == 0)
        {
            return;
        }

        _keyState = (byte)keystate;
        if (keystate != 0)
        {
            StartAttack();
        }
        else
        {
            StartRelease();
        }
    }

    /// <summary>
    /// SSG-EG: once the attenuation passes the midpoint, repeat, hold or alternate. Bit 0 of the shape holds, bit 1
    /// alternates, bit 2 inverts.
    /// </summary>
    private void ClockSsgEgState()
    {
        if (FmTables.Bitfield(_envAttenuation, 9) == 0)
        {
            return;
        }

        uint mode = _regs.OpSsgEgMode(OpOffset);
        if (FmTables.Bitfield(mode, 0) != 0)
        {
            // Hold: end inverted in shapes 3 and 5, and silence the others once past the attack. An inverted hold keeps
            // the attenuation where its last step left it, so only one that stopped exactly on the midpoint holds at
            // full level; one that stepped past it wraps to near silence (Nuked-OPN2; ymfm pins it to the midpoint,
            // docs/adr/0064-ym3438-chip-differences.md).
            _ssgInverted = (byte)(FmTables.Bitfield(mode, 2) ^ FmTables.Bitfield(mode, 1));
            if (_envState != EnvelopeState.Attack && _ssgInverted == 0)
            {
                _envAttenuation = 0x3ff;
            }
        }
        else
        {
            // Repeat: toggle in the alternating shapes (even during the attack), restart from decay or sustain, and
            // reset the phase in shapes 0 and 4.
            _ssgInverted ^= (byte)FmTables.Bitfield(mode, 1);
            if (_envState is EnvelopeState.Decay or EnvelopeState.Sustain)
            {
                StartAttack(true);
            }

            if (FmTables.Bitfield(mode, 1) == 0)
            {
                _phase = 0;
                _ssgPhaseRestart = true;
            }
        }

        if (_envState == EnvelopeState.Release)
        {
            _envAttenuation = 0x3ff;
        }
    }

    /// <summary>The inversion <see cref="ClockSsgEgState"/> leaves once the attenuation has passed the midpoint.</summary>
    private uint InvertedAfterTurn()
    {
        uint mode = _regs.OpSsgEgMode(OpOffset);
        return FmTables.Bitfield(mode, 0) != 0
            ? FmTables.Bitfield(mode, 2) ^ FmTables.Bitfield(mode, 1)
            : _ssgInverted ^ FmTables.Bitfield(mode, 1);
    }

    /// <summary>One envelope step at count <paramref name="envCounter"/>.</summary>
    private void ClockEnvelope(uint envCounter)
    {
        if (_envState == EnvelopeState.Attack && _envAttenuation == 0)
        {
            _envState = EnvelopeState.Decay;
        }

        // Straight after the attack: with sustain level 0 the decay is skipped (Shinobi's sound test #5 cymbals).
        if (_envState == EnvelopeState.Decay && _envAttenuation >= _cache.EgSustain)
        {
            _envState = EnvelopeState.Sustain;
        }

        // The sample a key on lands in only starts the attack (Nuked-OPN2; ymfm takes its first step at once,
        // docs/adr/0064-ym3438-chip-differences.md).
        if (_keyedOn)
        {
            return;
        }

        uint rate = _cache.EgRate[(int)_envState];

        // Shift the counter so it becomes a 5.11 fixed-point number; only a whole step clocks the envelope.
        int rateShift = (int)(rate >> 2);
        envCounter <<= rateShift;
        if (FmTables.Bitfield(envCounter, 0, 11) != 0)
        {
            return;
        }

        uint relevantBits = FmTables.Bitfield(envCounter, rateShift <= 11 ? 11 : rateShift, 3);
        uint increment = FmTables.AttenuationIncrement(rate, relevantBits);

        if (_envState == EnvelopeState.Attack)
        {
            // Rates 62 and 63 don't step if set after the key on (a chip glitch Nuked-OPN2 confirms).
            if (rate < 62)
            {
                _envAttenuation = (ushort)(_envAttenuation + ((~(uint)_envAttenuation * increment) >> 4));
            }

            return;
        }

        if (_regs.OpSsgEgEnable(OpOffset) == 0)
        {
            _envAttenuation = (ushort)(_envAttenuation + increment);
        }
        else if (_envAttenuation < 0x200)
        {
            // SSG-EG steps four times as fast, and only below the midpoint.
            _envAttenuation = (ushort)(_envAttenuation + (4 * increment));
        }

        if (_envAttenuation >= 0x400)
        {
            _envAttenuation = 0x3ff;
        }
    }

    private void ClockPhase(int lfoRawPm)
    {
        uint phaseStep = _cache.PhaseStep;
        if (phaseStep == OperatorCache.PhaseStepDynamic)
        {
            phaseStep = _regs.ComputePhaseStep(ChOffset, _cache, lfoRawPm);
        }

        _phase += phaseStep;
    }

    /// <summary>The envelope's attenuation as heard: SSG-EG inversion, tremolo and total level applied, at most 0x3ff.</summary>
    private uint EnvelopeAttenuation(uint amOffset)
    {
        uint result = _envAttenuation;
        if ((_ssgTurnInverted >= 0 ? _ssgTurnInverted : _ssgInverted) != 0)
        {
            result = (0x200 - result) & 0x3ff;
        }

        if (_regs.OpLfoAmEnable(OpOffset) != 0)
        {
            result += amOffset;
        }

        result += _cache.TotalLevel;
        return Math.Min(result, 0x3ff);
    }
}
