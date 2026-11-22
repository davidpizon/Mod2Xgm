using Mod2Xgm.Render.Fm;

namespace Mod2Xgm.Xgm;

/// <summary>What one encoding of a stream left: each song frame's FM bytes, and the frames that hold a frame splitter.</summary>
/// <param name="Bytes">Each song frame's FM bytes, its ending included.</param>
/// <param name="SplitFrames">The frames that split, in order.</param>
internal sealed record FrameLoad(IReadOnlyList<int> Bytes, IReadOnlyList<int> SplitFrames);

/// <summary>What <see cref="Xgm2PatchMover.Move"/> did.</summary>
/// <param name="Stream">The stream with the writes moved.</param>
/// <param name="Moved">Register writes moved to an earlier frame.</param>
/// <param name="Stuck">Register writes in frames that split which had no earlier frame to go to.</param>
internal sealed record PatchMoves(GenesisStream Stream, int Moved, int Stuck);

/// <summary>
/// Change 3 of docs/xgm2-frame-budget.md (docs/adr/0139-xgm2-frame-budget.md): in a frame that still splits, the writes
/// to a channel's patch, levels and frequency that come before its key on move to the latest earlier frame with room,
/// while the channel is keyed off and silent there, so the render doesn't change. A key on resets each operator's
/// phase, and a silent, keyed-off operator's registers can't be heard until then. The silence is read from the YM3438
/// model's envelopes, on both times through a looping song.
/// </summary>
internal static class Xgm2PatchMover
{
    /// <summary>The furthest back a write moves: 10 s at 60 Hz, which bounds the search.</summary>
    private const int MaxLookBack = 600;

    private const byte RegKey = 0x28;
    private const byte RegLfo = 0x22;
    private const byte RegCh3Mode = 0x27;
    private const byte RegTl = 0x40;
    private const byte SilentTl = 0x7f;
    private const byte Ch3SpecialBit = 0x40;

    /// <summary>An estimate of a moved write's bytes in its new frame: a raw write or a <c>$9x</c>.</summary>
    private const int BytesPerWrite = 2;

    /// <summary>
    /// Moves writes out of the frames <paramref name="load"/> says split, re-encoding with <paramref name="encode"/>
    /// after each move to check that it helped and that the frame it went to doesn't split.
    /// </summary>
    /// <param name="stream">The recording.</param>
    /// <param name="encode">Encodes a stream and reports its frame loads.</param>
    /// <param name="load">The recording's frame loads.</param>
    /// <param name="budget">The bytes a frame holds before it splits.</param>
    /// <param name="preferred">The bytes a target frame preferably stays under.</param>
    public static PatchMoves Move(GenesisStream stream, Func<GenesisStream, FrameLoad> encode, FrameLoad load, int budget, int preferred)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(encode);
        ArgumentNullException.ThrowIfNull(load);
        if (load.SplitFrames.Count == 0)
        {
            return new PatchMoves(stream, 0, 0);
        }

        bool[][] silent = SilentChannels(stream);
        List<List<ChipEvent>> frames = [.. stream.Frames.Select(f => f.ToList())];
        int moved = 0;
        int stuck = 0;
        foreach (int k in load.SplitFrames.Distinct().Order().ToList())
        {
            HashSet<int> tried = [];
            while (k < frames.Count && load.SplitFrames.Contains(k))
            {
                Group? group = Groups(frames[k]).Where(g => !tried.Contains(g.Channel)).MaxBy(g => g.Indices.Count);
                if (group is null)
                {
                    break;
                }

                tried.Add(group.Channel);
                int j = Target(frames, k, group, silent, stream.LoopFrame, load.Bytes, budget, preferred);
                if (j < 0)
                {
                    stuck += group.Indices.Count;
                    continue;
                }

                List<ChipEvent> fromK = frames[k];
                List<ChipEvent> toJ = frames[j];
                frames[k] = [.. fromK.Where((_, i) => !group.Indices.Contains(i))];
                frames[j] = [.. toJ, .. group.Indices.Order().Select(i => fromK[i])];
                FrameLoad next = encode(stream with { Frames = Freeze(frames) });
                if (next.SplitFrames.Contains(j) || next.SplitFrames.Count > load.SplitFrames.Count)
                {
                    frames[k] = fromK;
                    frames[j] = toJ;
                    stuck += group.Indices.Count;
                    continue;
                }

                load = next;
                moved += group.Indices.Count;
            }
        }

        return new PatchMoves(moved == 0 ? stream : stream with { Frames = Freeze(frames) }, moved, stuck);
    }

    /// <summary>A channel's writes before its key on in one frame.</summary>
    /// <param name="Part">The YM3438 part.</param>
    /// <param name="Index">The channel within the part (0–2).</param>
    /// <param name="Indices">Where the writes are in the frame.</param>
    private sealed record Group(int Part, int Index, IReadOnlyList<int> Indices)
    {
        /// <summary>The channel as the silence table numbers it: part × 3 + channel.</summary>
        public int Channel => (Part * 3) + Index;
    }

    /// <summary>
    /// The groups of <paramref name="frame"/>: for each channel whose first key event in the frame is a key on, its
    /// patch, level and frequency writes before that key on.
    /// </summary>
    private static List<Group> Groups(List<ChipEvent> frame)
    {
        List<Group> groups = [];
        HashSet<int> seen = [];
        for (int i = 0; i < frame.Count; i++)
        {
            ChipEvent e = frame[i];
            if (e is not { Kind: ChipEventKind.FmWrite, Register: RegKey } || (e.Value & 0x03) == 3 || !seen.Add(e.Value & 0x07))
            {
                continue;
            }

            if ((e.Value & 0xf0) == 0)
            {
                continue;
            }

            int part = (e.Value >> 2) & 1;
            int index = e.Value & 0x03;
            List<int> indices = [.. Enumerable.Range(0, i).Where(n => frame[n] is { Kind: ChipEventKind.FmWrite } w && w.Part == part && BelongsTo(w.Register, part, index))];
            if (indices.Count > 0)
            {
                groups.Add(new Group(part, index, indices));
            }
        }

        return groups;
    }

    /// <summary>True for a register of channel <paramref name="index"/> of <paramref name="part"/>: its patch, levels and frequency (CH3's slots too).</summary>
    private static bool BelongsTo(byte register, int part, int index) =>
        Xgm2FmState.PatchChannel(register) == index
        || (Xgm2FmState.IsFrequency(register) && (register & 0x03) == index && (register < 0xa8 || (part == 0 && index == 2)));

    /// <summary>
    /// The latest frame before <paramref name="k"/> the group may move to, preferring one that stays under
    /// <paramref name="preferred"/> bytes, or −1. Every frame from it to <paramref name="k"/> must start with the channel
    /// silent and keyed off, hold no key event for it, and hold no later write to the group's registers (nor, for a
    /// frequency, to its part's other frequencies, which share the high-byte latch). A move never crosses the loop frame.
    /// </summary>
    private static int Target(List<List<ChipEvent>> frames, int k, Group group, bool[][] silent, int? loopFrame, IReadOnlyList<int> bytes, int budget, int preferred)
    {
        if (!silent[k][group.Channel])
        {
            return -1;
        }

        HashSet<byte> registers = [.. group.Indices.Select(i => frames[k][i].Register)];
        bool frequency = registers.Any(Xgm2FmState.IsFrequency);
        int lowest = Math.Max(0, k - MaxLookBack);
        if (loopFrame is { } loop && k >= loop)
        {
            lowest = Math.Max(lowest, loop);
        }

        int extra = BytesPerWrite * (group.Indices.Count + 1);
        int fallback = -1;
        for (int j = k - 1; j >= lowest; j--)
        {
            if (!silent[j][group.Channel] || frames[j].Any(e => IsKeyOf(e, group)))
            {
                break;
            }

            int load = (j < bytes.Count ? bytes[j] : 0) + extra;
            if (load <= preferred)
            {
                return j;
            }

            if (fallback < 0 && load <= budget)
            {
                fallback = j;
            }

            // A write here to one of the group's registers would be undone by a move further back.
            if (frames[j].Any(e => e.Kind == ChipEventKind.FmWrite && e.Part == group.Part && (registers.Contains(e.Register) || (frequency && Xgm2FmState.IsFrequency(e.Register)))))
            {
                break;
            }
        }

        return fallback;
    }

    private static bool IsKeyOf(ChipEvent e, Group group) =>
        e is { Kind: ChipEventKind.FmWrite, Register: RegKey } && (e.Value & 0x07) == (group.Part * 4) + group.Index;

    /// <summary>
    /// For each frame, which FM channels start it keyed off and silent in the YM3438 model, on the first time through
    /// and, for a looping song, on the second too: the end of the song, then the loop frame's restore as the writer sends
    /// it (silent unset levels, every register as it was, the keys as they were), then the frames again.
    /// </summary>
    internal static bool[][] SilentChannels(GenesisStream stream)
    {
        Ym3438 chip = new();
        double clock = stream.Pal ? Ym3438.PalClockHz : Ym3438.NtscClockHz;
        double perFrame = Ym3438.SampleRateHz(clock) / stream.FrameRate;
        int[] left = new int[(int)Math.Ceiling(perFrame) + 1];
        int[] right = new int[left.Length];
        long played = 0;
        int count = stream.Frames.Count;
        bool[][] silent = [.. Enumerable.Range(0, count).Select(_ => new bool[6])];
        Dictionary<(int Part, byte Register), byte> registers = [];
        Dictionary<int, byte> keys = [];
        Dictionary<(int Part, byte Register), byte>? atLoop = null;
        Dictionary<int, byte>? keysAtLoop = null;

        void Play(int f, bool second)
        {
            for (int c = 0; c < 6; c++)
            {
                silent[f][c] = second ? silent[f][c] && chip.ChannelSilent(c) : chip.ChannelSilent(c);
            }

            foreach (ChipEvent e in stream.Frames[f].Where(e => e.Kind == ChipEventKind.FmWrite))
            {
                chip.WriteRegister(e.Part, e.Register, e.Value);
                if (e.Register == RegKey)
                {
                    keys[e.Value & 0x07] = e.Value;
                }
                else
                {
                    registers[(e.Part, e.Register)] = e.Value;
                }
            }

            long end = (long)Math.Round((played + 1) * perFrame);
            long start = (long)Math.Round(played * perFrame);
            int n = (int)(end - start);
            chip.Generate(left.AsSpan(0, n), right.AsSpan(0, n));
            played++;
        }

        for (int f = 0; f < count; f++)
        {
            if (f == stream.LoopFrame)
            {
                atLoop = new(registers);
                keysAtLoop = new(keys);
            }

            Play(f, second: false);
        }

        if (stream.LoopFrame is not { } loop || atLoop is null || keysAtLoop is null)
        {
            return silent;
        }

        for (int part = 0; part < 2; part++)
        {
            for (int r = 0; r < 0x10; r++)
            {
                byte register = (byte)(RegTl + r);
                if (Xgm2FmState.IsTl(register) && !atLoop.ContainsKey((part, register)))
                {
                    chip.WriteRegister(part, register, SilentTl);
                }
            }
        }

        // Every register as it was, a frequency's high byte just before its low byte, which latches it; then the LFO and
        // the CH3 mode bit (the driver owns $27's timer bits).
        foreach (((int part, byte register), byte value) in atLoop.Where(r => r.Key.Register is not (RegLfo or RegCh3Mode))
            .OrderBy(r => r.Key.Part).ThenBy(r => RestoreOrder(r.Key.Register)))
        {
            chip.WriteRegister(part, register, value);
        }

        if (atLoop.TryGetValue((0, RegLfo), out byte lfo))
        {
            chip.WriteRegister(0, RegLfo, lfo);
        }

        if (atLoop.TryGetValue((0, RegCh3Mode), out byte mode))
        {
            chip.WriteRegister(0, RegCh3Mode, (byte)(mode & Ch3SpecialBit));
        }

        foreach (int channel in new[] { 0, 1, 2, 4, 5, 6 })
        {
            chip.WriteRegister(0, RegKey, keysAtLoop.TryGetValue(channel, out byte key) ? key : (byte)channel);
        }

        for (int f = loop; f < count; f++)
        {
            Play(f, second: true);
        }

        return silent;
    }

    /// <summary>The order the loop frame sets registers back in: as <c>Xgm2Writer</c>'s restore.</summary>
    private static int RestoreOrder(byte register) =>
        register is (>= 0xa4 and <= 0xa6) or (>= 0xac and <= 0xae) ? ((register - 4) * 2) - 1 : register * 2;

    private static List<IReadOnlyList<ChipEvent>> Freeze(List<List<ChipEvent>> frames) => [.. frames.Select(f => (IReadOnlyList<ChipEvent>)f.ToArray())];
}
