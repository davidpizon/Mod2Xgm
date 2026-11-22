namespace Mod2Xgm.Xgm;

/// <summary>What an FM command is, for the encoder's folds of one command into the next (docs/adr/0139-xgm2-frame-budget.md).</summary>
internal enum FmCommandKind
{
    /// <summary>Any command nothing folds into.</summary>
    Other,

    /// <summary><c>$4x</c> key off of all four operators: a key on straight after makes it a <c>$5x</c>.</summary>
    KeyOff,

    /// <summary><c>$4x</c> key on of all four operators: a key off straight after makes it a <c>$5x</c>.</summary>
    KeyOn,

    /// <summary><c>$3x</c> frequency set: before a one-frame wait it becomes <c>$8x</c>.</summary>
    Frequency,

    /// <summary><c>$Ax</c> frequency delta: before a one-frame wait it becomes <c>$Bx</c>.</summary>
    FrequencyDelta,

    /// <summary><c>$9x</c> total level: before a one-frame wait it becomes <c>$Dx</c> when its change fits a delta.</summary>
    Level,
}

/// <summary>One FM command for the XGM2 stream, with what the encoder needs to count and fold it.</summary>
/// <param name="Bytes">The command's bytes.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Writes">The YM3438 register writes SGDK's driver makes for it (for the busy-frame note).</param>
/// <param name="Channels">The FM channels it touches: bit <c>part × 3 + channel</c>.</param>
internal sealed record FmCommand(byte[] Bytes, FmCommandKind Kind, int Writes, int Channels)
{
    /// <summary>For a key or a key-folding frequency set, the channel as <c>$28</c> numbers it (0–2, 4–6); otherwise −1.</summary>
    public int Key { get; init; } = -1;

    /// <summary>For <see cref="FmCommandKind.Level"/>, the <c>$Dx</c> data byte that sets the same level as a delta, or <c>null</c>.</summary>
    public byte? LevelDelta { get; init; }

    /// <summary>True for a <c>$3x</c> that carries the key off sent just before it (bit 14), which the encoder then takes back.</summary>
    public bool TakesKeyOff { get; init; }
}

/// <summary>What one <see cref="Xgm2FmState.Flush"/> sends.</summary>
/// <param name="Commands">The commands, in order.</param>
/// <param name="KeyFolded">True when the key on that follows went into the last <c>$3x</c>.</param>
/// <param name="Dropped">Registers not sent because the chip already holds their value.</param>
internal sealed record FmFlush(IReadOnlyList<FmCommand> Commands, bool KeyFolded, int Dropped);

/// <summary>
/// The FM side of the XGM2 encoder (docs/adr/0139-xgm2-frame-budget.md): what the YM3438 holds from the stream so far,
/// and what SGDK's XGM2 driver stored (<c>drv_xgm2_ym_fct.i80</c>), so each group of register writes goes out as the
/// fewest bytes that leave the chip as the writes would. A register whose value the chip already holds isn't sent; a
/// whole patch goes as one load instrument (<c>$2x</c>) where that is smaller; frequencies go as <c>$3x</c> (with the
/// key on that follows) or as a delta (<c>$Ax</c>); a pan alone as <c>$6x</c>/<c>$7x</c>; levels as <c>$9x</c>; the rest
/// as raw writes (<c>$Ex</c>). Nothing is assumed about a register the stream hasn't set.
/// </summary>
internal sealed class Xgm2FmState
{
    private const int Unknown = -1;
    private const int Registers = 0x100;
    private const byte RegFirstSlot = 0x30;
    private const byte RegLastSlot = 0x9f;
    private const byte RegTl = 0x40;
    private const byte RegAlgorithm = 0xb0;
    private const byte RegPan = 0xb4;
    private const byte RegFreqLow = 0xa0;
    private const byte RegFreqHigh = 0xa4;

    /// <summary>From a channel's frequency registers to its CH3 slot registers (<c>$A8</c>/<c>$AC</c>).</summary>
    private const int SlotGroupOffset = 0x08;

    /// <summary>The block and F-number high bits of <c>$A4</c>: bits 7–6 are unused, and the driver puts its key flags there.</summary>
    private const int FreqHighBits = 0x3f;

    /// <summary>The 14 bits of block and F-number a frequency command carries.</summary>
    private const int FreqBits = 0x3fff;
    private const int FreqKeyOff = 0x40;
    private const int FreqKeyOn = 0x80;

    /// <summary>The largest step <c>$Ax</c> takes, and <c>$Cx</c>/<c>$Dx</c> (xgm2.txt).</summary>
    private const int MaxFreqDelta = 128;
    private const int MaxLevelDelta = 64;

    private const byte FmRawWrite = 0xe0;
    private const int FmMaxRawWrites = 8;
    private const byte FmLoadInstrument = 0x20;
    private const byte FmSetFreq = 0x30;
    private const byte FmSlotMode = 0x08;
    private const byte FmPanPart0 = 0x60;
    private const byte FmPanPart1 = 0x70;
    private const byte FmSetTl = 0x90;
    private const byte FmFreqDelta = 0xa0;

    /// <summary>A load instrument is 31 bytes: the command and the 30 registers it carries.</summary>
    private const int LoadInstrumentBytes = 1 + PatchRegisterCount;

    /// <summary>The registers <c>$2x</c> carries for one channel: 7 slot registers of 4 slots, then <c>$B0</c> and <c>$B4</c>.</summary>
    private const int PatchRegisterCount = (7 * 4) + 2;

    /// <summary>Each register's value as the chip holds it, by <c>part × 256 + register</c>; −1 for one the stream hasn't set.</summary>
    private readonly int[] _chip = new int[2 * Registers];

    /// <summary>The F-number high byte latch, by <c>part × 2 + group</c> (0: <c>$A4</c>–<c>$A6</c>, 1: <c>$AC</c>–<c>$AE</c>): one each, shared by its channels.</summary>
    private readonly int[] _latch = new int[4];

    /// <summary>The 16 bits the driver stored for each channel's frequency (<c>FM_STATE_CH_FREQ</c>), by <c>part × 3 + channel</c>.</summary>
    private readonly int[] _freq = new int[6];

    /// <summary>The 16 bits the driver stored for each CH3 slot frequency (<c>FM_STATE_CH2_FREQ</c>).</summary>
    private readonly int[] _slotFreq = new int[3];

    /// <summary>Starts knowing nothing.</summary>
    public Xgm2FmState() => Forget();

    /// <summary>Forgets everything: the next value of every register is sent, as at the start and at the loop frame.</summary>
    public void Forget()
    {
        Array.Fill(_chip, Unknown);
        Array.Fill(_latch, Unknown);
        Array.Fill(_freq, Unknown);
        Array.Fill(_slotFreq, Unknown);
    }

    /// <summary>Records a total level sent outside <see cref="Flush"/> (the loop frame's silent slots).</summary>
    public void SetLevel(int part, byte register, byte level) => _chip[(part * Registers) + register] = level;

    /// <summary>The channel bit of FM channel <paramref name="key"/> as <c>$28</c> numbers it (0–2, 4–6).</summary>
    public static int KeyChannelBit(int key) => 1 << (((key >> 2) * 3) + (key & 0x03));

    /// <summary>The FM channel (0–2 within its part) a patch register (<c>$30</c>–<c>$9F</c>, <c>$B0</c>–<c>$B6</c>) belongs to, or −1.</summary>
    public static int PatchChannel(byte register)
    {
        int channel = register & 0x03;
        bool patch = register is (>= RegFirstSlot and <= RegLastSlot) or (>= RegAlgorithm and <= RegPan + 2);
        return patch && channel < 3 ? channel : -1;
    }

    /// <summary>True for a slot's total level register (<c>$40</c>–<c>$4E</c>, not a fourth-channel address).</summary>
    public static bool IsTl(byte register) => register is >= RegTl and < RegTl + 0x10 && (register & 0x03) != 3;

    /// <summary>True for a frequency register of a channel or CH3 slot: <c>$A0</c>–<c>$A2</c>, <c>$A4</c>–<c>$A6</c>, <c>$A8</c>–<c>$AA</c>, <c>$AC</c>–<c>$AE</c>.</summary>
    public static bool IsFrequency(byte register) => register is >= RegFreqLow and <= 0xae && (register & 0x03) != 3;

    /// <summary>
    /// The commands for <paramref name="pending"/>, the register writes since the last command (in order, a register
    /// possibly written twice), and the state they leave.
    /// </summary>
    /// <param name="pending">The writes.</param>
    /// <param name="keyOn">The channel (as <c>$28</c> numbers it) keyed on, all four operators, straight after the writes; or −1.</param>
    /// <param name="afterKeyOff">True when the last command sent is the <c>$4x</c> key off of <paramref name="keyOn"/>'s channel.</param>
    public FmFlush Flush(IReadOnlyList<(int Part, byte Register, byte Value)> pending, int keyOn = -1, bool afterKeyOff = false)
    {
        ArgumentNullException.ThrowIfNull(pending);
        List<FmCommand> commands = [];
        List<FmCommand> frequencies = [];
        List<(int Part, byte Register, byte Value)> rawFrequencies = [];
        HashSet<(int Part, byte Register)> handled = [];
        bool keyFolded = false;
        int dropped = 0;

        // Frequencies first in thought, last in the stream: each group's latch decides what the writes leave.
        foreach ((int part, int frequencyGroup) in pending.Where(w => IsFrequency(w.Register)).Select(w => (w.Part, Group: (w.Register >> 3) & 1)).Distinct())
        {
            dropped += PlanFrequencies(pending, part, frequencyGroup, keyOn, frequencies, rawFrequencies, handled, ref keyFolded);
        }

        // The last write of each other register, in the order the registers were first written.
        List<(int Part, byte Register, byte Value)> last = [];
        Dictionary<(int Part, byte Register), int> at = [];
        foreach ((int part, byte register, byte value) in pending.Where(w => !handled.Contains((w.Part, w.Register))))
        {
            if (at.TryGetValue((part, register), out int i))
            {
                last[i] = (part, register, value);
            }
            else
            {
                at[(part, register)] = last.Count;
                last.Add((part, register, value));
            }
        }

        List<(int Part, byte Register, byte Value)> changed = [.. last.Where(w => Chip(w.Part, w.Register) != w.Value)];
        dropped += last.Count - changed.Count;

        // A channel's patch as one load instrument, where that is smaller than its changed registers.
        foreach ((int part, int channel) in changed.Where(w => PatchChannel(w.Register) >= 0).Select(w => (w.Part, PatchChannel(w.Register))).Distinct().ToList())
        {
            byte[]? load = LoadInstrument(part, channel, changed);
            if (load is not null)
            {
                commands.Add(new FmCommand(load, FmCommandKind.Other, PatchRegisterCount, 1 << ((part * 3) + channel)));
                changed.RemoveAll(w => w.Part == part && PatchChannel(w.Register) == channel);
            }
        }

        // A pan change that keeps the LFO sensitivities: one byte.
        foreach ((int part, byte register, byte value) in changed.Where(w => IsPanOnly(w.Part, w.Register, w.Value)).ToList())
        {
            int channel = register & 0x03;
            commands.Add(new FmCommand([(byte)((part == 0 ? FmPanPart0 : FmPanPart1) | channel | ((value >> 6) << 2))], FmCommandKind.Other, 1, 1 << ((part * 3) + channel)));
            Set(part, register, value);
            changed.Remove((part, register, value));
        }

        // Raw writes, 8 to a command, one part each; then levels through $9x, so the driver keeps them.
        List<(int Part, byte Register, byte Value)> group = [];
        foreach ((int part, byte register, byte value) in changed.Where(w => !IsTl(w.Register)))
        {
            if (group.Count == FmMaxRawWrites || (group.Count > 0 && group[0].Part != part))
            {
                commands.Add(Raw(group));
            }

            group.Add((part, register, value));
        }

        if (group.Count > 0)
        {
            commands.Add(Raw(group));
        }

        foreach ((int part, byte register, byte value) in changed.Where(w => IsTl(w.Register)))
        {
            commands.Add(Level(part, register, value));
        }

        commands.AddRange(frequencies);

        // Writes a latch was left holding (rare: a high byte with no low byte after it), or writes after an unknown latch.
        for (int i = 0; i < rawFrequencies.Count; i += FmMaxRawWrites)
        {
            foreach (IGrouping<int, (int Part, byte Register, byte Value)> part in rawFrequencies.Skip(i).Take(FmMaxRawWrites).GroupBy(w => w.Part))
            {
                commands.Add(Raw([.. part]));
            }
        }

        // A key off sent just before this channel's frequency set and key on goes into the set (bit 14).
        if (afterKeyOff && keyFolded && commands is [{ Kind: FmCommandKind.Frequency } only])
        {
            byte[] bytes = [.. only.Bytes];
            bytes[1] |= FreqKeyOff;
            int index = ((keyOn >> 2) * 3) + (keyOn & 0x03);
            _freq[index] = (bytes[1] << 8) | bytes[2];
            commands[0] = only with { Bytes = bytes, Writes = only.Writes + 1, TakesKeyOff = true };
        }

        return new FmFlush(commands, keyFolded, dropped);
    }

    /// <summary>
    /// Works out one frequency group's writes: what each of its channels' block and F-number become (the high byte
    /// takes effect only when its low byte is written, from the latch the group shares), and adds the commands that set
    /// them. A channel or slot that ends as the chip already holds it sends nothing. Returns the writes dropped.
    /// </summary>
    private int PlanFrequencies(
        IReadOnlyList<(int Part, byte Register, byte Value)> pending,
        int part,
        int group,
        int keyOn,
        List<FmCommand> frequencies,
        List<(int Part, byte Register, byte Value)> raw,
        HashSet<(int Part, byte Register)> handled,
        ref bool keyFolded)
    {
        List<(int Part, byte Register, byte Value)> writes = [.. pending.Where(w => w.Part == part && IsFrequency(w.Register) && ((w.Register >> 3) & 1) == group)];
        foreach ((int p, byte register, _) in writes)
        {
            handled.Add((p, register));
        }

        int latch = _latch[(part * 2) + group];
        int dangling = -1;
        bool unknown = false;
        List<int> touched = [];
        Dictionary<int, (int High, int Low)> target = [];
        foreach ((_, byte register, byte value) in writes)
        {
            int index = register & 0x03;
            if ((register & 0x04) != 0)
            {
                latch = value & FreqHighBits;
                dangling = index;
            }
            else
            {
                unknown |= latch == Unknown;
                if (!target.ContainsKey(index))
                {
                    touched.Add(index);
                }

                target[index] = (latch, value);
                dangling = -1;
            }
        }

        // A CH3 slot of part 1 isn't one the driver sets, and a low byte after an unknown latch can't be worked out:
        // those writes go as the stream wrote them.
        if (unknown || (group == 1 && part == 1))
        {
            foreach ((int p, byte register, byte value) in writes)
            {
                raw.Add((p, register, value));
                SetRaw(p, register, value);
            }

            return 0;
        }

        int dropped = 0;
        int lastHigh = _latch[(part * 2) + group];
        FmCommand? folded = null;
        foreach (int index in touched)
        {
            (int high, int low) = target[index];
            byte highRegister = (byte)(RegFreqHigh + (group * SlotGroupOffset) + index);
            byte lowRegister = (byte)(RegFreqLow + (group * SlotGroupOffset) + index);
            if (Chip(part, highRegister) == high && Chip(part, lowRegister) == low)
            {
                dropped++;
                continue;
            }

            bool fold = group == 0 && (part * 4) + index == keyOn;
            FmCommand command = FrequencyCommand(part, group, index, high, low, fold);
            Set(part, highRegister, high);
            Set(part, lowRegister, low);
            lastHigh = high;
            if (fold)
            {
                folded = command;
                keyFolded = true;
            }
            else
            {
                frequencies.Add(command);
            }
        }

        // The key on comes after every write of its channel, so its frequency set goes last.
        if (folded is not null)
        {
            frequencies.Add(folded);
        }

        _latch[(part * 2) + group] = lastHigh;
        if (dangling >= 0 && lastHigh != latch)
        {
            byte register = (byte)(RegFreqHigh + (group * SlotGroupOffset) + dangling);
            raw.Add((part, register, (byte)latch));
            _latch[(part * 2) + group] = latch;
        }

        return dropped;
    }

    /// <summary>
    /// The command that sets a channel's (or CH3 slot's) frequency: a delta (<c>$Ax</c>, 2 bytes) from what the driver
    /// stored when the driver's sum gives exactly the new registers and no key goes with it; otherwise <c>$3x</c>
    /// (3 bytes), with the key on in bit 15 when <paramref name="keyOn"/>. The driver writes <c>$A4</c> then <c>$A0</c>.
    /// </summary>
    private FmCommand FrequencyCommand(int part, int group, int index, int high, int low, bool keyOn)
    {
        int channels = group == 0 ? 1 << ((part * 3) + index) : 1 << 2;
        ref int stored = ref group == 0 ? ref _freq[(part * 3) + index] : ref _slotFreq[index];
        byte x = (byte)(group == 0 ? index | (part << 2) : FmSlotMode | index);
        int value = (high << 8) | low;
        if (!keyOn && stored != Unknown)
        {
            int up = (value - (stored & FreqBits)) & FreqBits;
            int down = ((stored & FreqBits) - value) & FreqBits;
            if (up is >= 1 and <= MaxFreqDelta || down is >= 1 and <= MaxFreqDelta)
            {
                bool add = up is >= 1 and <= MaxFreqDelta;
                int delta = add ? up : down;
                stored = (stored + (add ? delta : -delta)) & 0xffff;
                return new FmCommand([(byte)(FmFreqDelta | x), (byte)(((delta - 1) << 1) | (add ? 0 : 1))], FmCommandKind.FrequencyDelta, 2, channels);
            }
        }

        byte first = (byte)(high | (keyOn ? FreqKeyOn : 0));
        stored = (first << 8) | low;
        return new FmCommand([(byte)(FmSetFreq | x), first, (byte)low], FmCommandKind.Frequency, keyOn ? 3 : 2, channels)
        {
            Key = keyOn ? (part * 4) + index : -1,
        };
    }

    /// <summary>
    /// The <c>$2x</c> command for a channel, when all 30 of its registers are known once <paramref name="changed"/> is
    /// applied and its 31 bytes are fewer than the changed registers would take; otherwise <c>null</c>. Resending a
    /// register the chip already holds changes nothing.
    /// </summary>
    private byte[]? LoadInstrument(int part, int channel, List<(int Part, byte Register, byte Value)> changed)
    {
        Dictionary<byte, byte> writes = changed.Where(w => w.Part == part && PatchChannel(w.Register) == channel).ToDictionary(w => w.Register, w => w.Value);
        int raw = writes.Count(w => !IsTl(w.Key) && !IsPanOnly(part, w.Key, w.Value));
        int cost = (2 * raw) + ((raw + FmMaxRawWrites - 1) / FmMaxRawWrites) + writes.Count(w => IsPanOnly(part, w.Key, w.Value)) + (2 * writes.Count(w => IsTl(w.Key)));
        if (cost <= LoadInstrumentBytes)
        {
            return null;
        }

        byte[] command = new byte[LoadInstrumentBytes];
        command[0] = (byte)(FmLoadInstrument | (part << 2) | channel);
        int d = 1;
        foreach (byte register in PatchOrder(channel))
        {
            int value = writes.TryGetValue(register, out byte v) ? v : Chip(part, register);
            if (value == Unknown)
            {
                return null;
            }

            command[d++] = (byte)value;
        }

        d = 1;
        foreach (byte register in PatchOrder(channel))
        {
            Set(part, register, command[d++]);
        }

        return command;
    }

    /// <summary>The registers <c>$2x</c> carries for <paramref name="channel"/>, in its order: per register <c>$30</c>–<c>$90</c> its four slots, then <c>$B0</c> and <c>$B4</c>.</summary>
    private static IEnumerable<byte> PatchOrder(int channel)
    {
        for (int r = 0; r < 7; r++)
        {
            for (int s = 0; s < 4; s++)
            {
                yield return (byte)(RegFirstSlot + (r << 4) + (s << 2) + channel);
            }
        }

        yield return (byte)(RegAlgorithm + channel);
        yield return (byte)(RegPan + channel);
    }

    /// <summary>True for a pan register (<c>$B4</c>–<c>$B6</c>) whose new value changes only the left/right bits of the known one.</summary>
    private bool IsPanOnly(int part, byte register, byte value) =>
        register is >= RegPan and <= RegPan + 2 && Chip(part, register) is var held && held != Unknown && ((held ^ value) & FreqHighBits) == 0;

    /// <summary>A total level through <c>$9x</c>, with the <c>$Dx</c> data byte that gives the same level as a delta when it fits.</summary>
    private FmCommand Level(int part, byte register, byte value)
    {
        int held = Chip(part, register);
        int delta = value - held;
        byte? asDelta = held != Unknown && Math.Abs(delta) is >= 1 and <= MaxLevelDelta
            ? (byte)(((Math.Abs(delta) - 1) << 2) | (delta < 0 ? 2 : 0) | part)
            : null;
        Set(part, register, value);

        // $9x: slot in bits 3-2 and channel in bits 1-0 of x, as in the register; level in bits 7-1, part in bit 0.
        return new FmCommand([(byte)(FmSetTl | (register & 0x0f)), (byte)((value << 1) | part)], FmCommandKind.Level, 1, 1 << ((part * 3) + (register & 0x03)))
        {
            LevelDelta = asDelta,
        };
    }

    /// <summary>One <c>$Ex</c> command of up to 8 writes to one part; empties <paramref name="group"/>.</summary>
    private FmCommand Raw(List<(int Part, byte Register, byte Value)> group)
    {
        int channels = 0;
        foreach ((int part, byte register, byte value) in group)
        {
            int channel = PatchChannel(register) >= 0 ? PatchChannel(register) : IsFrequency(register) ? register & 0x03 : -1;
            channels |= channel >= 0 ? 1 << ((part * 3) + channel) : 0;
            if (!IsFrequency(register))
            {
                Set(part, register, value);
            }
        }

        FmCommand command = new([(byte)(FmRawWrite | (group[0].Part << 3) | (group.Count - 1)), .. group.SelectMany(w => new[] { w.Register, w.Value })], FmCommandKind.Other, group.Count, channels);
        group.Clear();
        return command;
    }

    /// <summary>Applies a raw write of a frequency register to the chip's state: a high byte goes to the latch, a low byte commits it.</summary>
    private void SetRaw(int part, byte register, byte value)
    {
        int group = (register >> 3) & 1;
        int index = register & 0x03;
        if ((register & 0x04) != 0)
        {
            _latch[(part * 2) + group] = value & FreqHighBits;
            return;
        }

        int latch = _latch[(part * 2) + group];
        Set(part, (byte)(RegFreqHigh + (group * SlotGroupOffset) + index), latch);
        Set(part, register, value);
    }

    /// <summary>What the chip holds in <paramref name="register"/>: for a frequency high byte, the value in effect, not the latch.</summary>
    private int Chip(int part, byte register) => _chip[(part * Registers) + register];

    private void Set(int part, byte register, int value) => _chip[(part * Registers) + register] = value;
}
