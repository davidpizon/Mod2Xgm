using System.Buffers.Binary;
using System.Text;

namespace Mod2Xgm.Xgm;

/// <summary>A recording the XGM2 file stores, for the report.</summary>
/// <param name="Id">Its sample id in the file (1-based; 0 stops a channel).</param>
/// <param name="Sample">The song sample it was made from (0-based).</param>
/// <param name="Name">What it is: the MOD sample's number and name, or a pre-mix's name.</param>
/// <param name="Bytes">Its bytes in the file, padded to 256.</param>
/// <param name="HalfSpeed">True when it plays at half speed.</param>
/// <param name="RateHz">The rate it plays at, in hertz.</param>
internal sealed record Xgm2Sample(int Id, int Sample, string Name, int Bytes, bool HalfSpeed, int RateHz);

/// <summary>An XGM2 file and what is in it.</summary>
/// <param name="Bytes">The file.</param>
/// <param name="Samples">The recordings it stores, by id.</param>
/// <param name="FmBytes">The FM stream's bytes, before padding.</param>
/// <param name="PsgBytes">The PSG stream's bytes, before padding.</param>
/// <param name="Frames">Frames the song lasts, one pass.</param>
/// <param name="LoopFrame">The frame it loops back to, or <c>null</c>.</param>
internal sealed record Xgm2Output(byte[] Bytes, IReadOnlyList<Xgm2Sample> Samples, int FmBytes, int PsgBytes, int Frames, int? LoopFrame);

/// <summary>
/// Writes the uncompiled XGM2 file SGDK's xgm2tool and rescomp read (docs/formats/xgm.md#xgm2-layout-from-xgm2txt-version-byte-0x10,
/// plan T10.1, docs/adr/0099-xgm2-writer-records-the-player.md): single track, unpacked, no GD3. The FM stream uses
/// load instrument (<c>$2x</c>) for a channel's whole patch, total level (<c>$9x</c>), raw register writes (<c>$Ex</c>)
/// for the rest, key (<c>$F8</c>), LFO (<c>$F9</c>), CH3 mode (<c>$FA</c>/<c>$FB</c>) and PCM
/// (<c>$1x</c>) commands; the PSG stream tone (<c>$2x</c>) and volume (<c>$8x</c>–<c>$Bx</c>) commands; both end each
/// frame with a wait. The shorter delta and combined commands are left for later; golden tests guard the output.
/// </summary>
internal static class Xgm2Writer
{
    // Header (docs/formats/xgm.md#xgm2-layout-from-xgm2txt-version-byte-0x10).
    private const byte Version = 0x10;
    private const int FlagPal = 0x01;
    private const int SidOffset = 0x000C;

    /// <summary>The single-track SID table: 124 two-byte entries.</summary>
    private const int SidEntries = 124;

    /// <summary>The file's first data byte, after the 248-byte SID table.</summary>
    private const int DataOffset = SidOffset + (SidEntries * 2);

    /// <summary>Most recordings a single-track file holds: one entry is the end of the last (docs/adr/0085-target-limits.md).</summary>
    public const int MaxSamples = SidEntries - 1;

    /// <summary>Blocks, samples and their sizes are counted in 256 bytes.</summary>
    private const int Alignment = 256;

    /// <summary>A SID entry with no sample.</summary>
    private const ushort EmptyEntry = 0xffff;

    /// <summary>A 16-bit size in 256-byte units holds up to this many units.</summary>
    private const int MaxUnits = 0xffff;

    /// <summary>Loop addresses are 24-bit; all ones is "no loop".</summary>
    private const int NoLoop = 0xffffff;

    // FM stream commands (docs/formats/xgm.md#fm-stream-commands).
    private const byte FmWaitLong = 0x0f;
    private const int FmMaxShortWait = 15;
    private const int FmMaxLongWait = 271;
    private const byte FmPcm = 0x10;
    private const byte FmPcmHalfSpeed = 0x04;
    private const byte FmRawWrite = 0xe0;
    private const int FmMaxRawWrites = 8;
    private const byte FmLoadInstrument = 0x20;
    private const byte FmSetTl = 0x90;
    private const byte FmKey = 0xf8;
    private const byte FmLfo = 0xf9;
    private const byte FmCh3On = 0xfa;
    private const byte FmCh3Off = 0xfb;
    private const byte FmLoop = 0xff;
    private const byte FmFrameSplit = 0xf0;

    /// <summary>
    /// A frame's commands, its ending included, stay under this many bytes. xgm2tool's packer takes frames under 256
    /// (<c>XGCPacker.FRAME_MAX_SIZE</c>), but where it ends a packed block at a frame boundary it doesn't check that the
    /// match starting there (up to <c>MATCH_MAX_SIZE</c>, 31 bytes) stops at the next boundary; after a tiny frame, a match
    /// can run into the next frame, which must then still end within 256 bytes of the boundary. So a frame stays 31 bytes
    /// shorter than the packer allows.
    /// </summary>
    private const int MaxFrameBytes = 256 - 31;

    /// <summary>The most bytes the command that ends a frame takes: a long wait.</summary>
    private const int FrameEndBytes = 2;

    // PSG stream commands (docs/formats/xgm.md#psg-stream-commands).
    private const byte PsgWaitLong = 0x0e;
    private const int PsgMaxShortWait = 14;
    private const int PsgMaxLongWait = 270;
    private const byte PsgLoop = 0x0f;
    private const byte PsgTone = 0x20;
    private const byte PsgVolume = 0x80;

    /// <summary>A PSG channel's volume at its quietest: off.</summary>
    private const int PsgSilent = 0x0f;

    // YM3438 registers the writer treats apart (docs/genesis-audio.md).
    private const byte RegLfo = 0x22;
    private const byte RegCh3Mode = 0x27;
    private const byte RegKey = 0x28;
    private const byte RegDacData = 0x2a;
    private const byte RegDacEnable = 0x2b;
    private const byte RegFirstSlot = 0x30;
    private const byte RegLastSlot = 0x9f;
    private const byte RegTl = 0x40;
    private const byte RegAlgorithm = 0xb0;
    private const byte RegPan = 0xb4;

    /// <summary>A channel's patch: 7 registers of 4 slots, then algorithm and pan, as <c>$2x</c> carries them.</summary>
    private const int PatchRegisters = (7 * 4) + 2;
    private const byte Ch3SpecialBit = 0x40;
    private const int KeyChannelBits = 0x07;

    /// <summary>The PCM channels: SGDK's XGM2 driver mixes three.</summary>
    private const int PcmChannels = 3;

    /// <summary>Writes <paramref name="stream"/> as an XGM2 file.</summary>
    /// <exception cref="ConversionException">The song needs more recordings, PCM bytes or stream bytes than XGM2 holds (exit 6).</exception>
    public static Xgm2Output Write(GenesisStream stream, ConversionNotes notes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(notes);
        Encoder encoder = new(stream);
        encoder.Run();
        IReadOnlyList<Xgm2Sample> samples = encoder.Samples(stream.PcmRateHz);
        if (encoder.Splits > 0)
        {
            notes.Add("{Count} frame splits: some frames hold more FM writes than SGDK's XGM2 driver takes at once, so the writes after each split play one frame later", encoder.Splits);
        }

        if (encoder.CarriedOverLoop > 0)
        {
            notes.Add("{Count} recordings still sound where the song loops back; XGM2 can't start them partway, so they are silent from the second time through", encoder.CarriedOverLoop);
        }

        byte[] sdat = encoder.SampleData();
        byte[] fm = [.. encoder.Fm];
        byte[] psg = [.. encoder.Psg];
        int slen = Units(sdat.Length, "recordings");
        int fmlen = Units(fm.Length, "FM stream");
        int psglen = Units(psg.Length, "PSG stream");
        byte[] file = new byte[DataOffset + ((slen + fmlen + psglen) * Alignment)];
        Span<byte> span = file;
        Encoding.ASCII.GetBytes("XGM2", span[..4]);
        span[4] = Version;
        span[5] = (byte)(stream.Pal ? FlagPal : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], (ushort)slen);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], (ushort)fmlen);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], (ushort)psglen);
        IReadOnlyList<int> starts = encoder.SampleStarts();
        for (int i = 0; i < SidEntries; i++)
        {
            // Entry i is sample id i + 1; the entry after the last sample is its end (docs/formats/xgm.md).
            ushort entry = samples.Count == 0 || i > samples.Count ? EmptyEntry
                : (ushort)((i < samples.Count ? starts[i] : sdat.Length) / Alignment);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(SidOffset + (i * 2))..], entry);
        }

        sdat.CopyTo(span[DataOffset..]);
        fm.CopyTo(span[(DataOffset + (slen * Alignment))..]);
        psg.CopyTo(span[(DataOffset + ((slen + fmlen) * Alignment))..]);
        return new Xgm2Output(file, samples, fm.Length, psg.Length, stream.Frames.Count, stream.LoopFrame);
    }

    /// <summary>A block's size in 256-byte units, rounded up.</summary>
    private static int Units(int bytes, string what)
    {
        int units = (bytes + Alignment - 1) / Alignment;
        if (units > MaxUnits)
        {
            throw new ConversionException(ExitCode.DoesNotFit, $"The XGM2 {what} needs {bytes} bytes; the format holds {MaxUnits * Alignment}.");
        }

        return units;
    }

    /// <summary>Encodes the frames into the two streams, keeping the state of both chips for the loop point.</summary>
    private sealed class Encoder(GenesisStream stream)
    {
        private readonly Dictionary<sbyte[], int> _ids = new(ReferenceEqualityComparer.Instance);
        private readonly List<(sbyte[] Data, bool HalfSpeed, int Sample, string Name)> _samples = [];

        /// <summary>Every YM3438 register written, by part and register, but for the key, LFO, CH3 and DAC registers.</summary>
        private readonly SortedDictionary<(int Part, byte Register), byte> _fm = [];

        /// <summary>The last <c>$28</c> write of each channel, by its channel bits.</summary>
        private readonly SortedDictionary<int, byte> _keys = [];

        private readonly List<(int Part, byte Register, byte Value)> _raw = [];

        /// <summary>Each PCM channel's playing copy, the frame it started and its rate, or <c>null</c>.</summary>
        private readonly (sbyte[] Data, int Frame, double RateHz)?[] _pcm = new (sbyte[], int, double)?[PcmChannels];

        // The PSG as the bytes written leave it, and as the stream last set it (−1: never).
        private readonly int[] _tone = [-1, -1, -1, -1];
        private readonly int[] _volume = [-1, -1, -1, -1];
        private readonly int[] _toneSent = [-1, -1, -1, -1];
        private readonly int[] _volumeSent = [-1, -1, -1, -1];
        private int _latchChannel;
        private bool _latchVolume;
        private bool _noiseWritten;

        private byte? _lfo;
        private bool? _ch3;
        private int _fmFrame;
        private int _psgFrame;
        private int _fmLoop = NoLoop;
        private int _fmFrameBytes;

        /// <summary>Frames the driver is behind the song: one for each frame splitter not yet taken off a wait.</summary>
        private int _fmLag;
        private int _psgLoop = NoLoop;

        /// <summary>The FM stream.</summary>
        public List<byte> Fm { get; } = [];

        /// <summary>The PSG stream.</summary>
        public List<byte> Psg { get; } = [];

        /// <summary>Frame splitters put in frames too long for one go.</summary>
        public int Splits { get; private set; }

        /// <summary>Recordings still playing at the loop frame, which a loop can't restart partway.</summary>
        public int CarriedOverLoop { get; private set; }

        /// <summary>Encodes every frame, then the end: a loop back, or silence and the end.</summary>
        public void Run()
        {
            for (int f = 0; f < stream.Frames.Count; f++)
            {
                if (f == stream.LoopFrame)
                {
                    StartLoop(f);
                }

                foreach (ChipEvent e in stream.Frames[f])
                {
                    Encode(e, f);
                }

                FlushRaw();
                FlushPsg(f, force: false);
            }

            int end = stream.Frames.Count;
            if (stream.LoopFrame is null)
            {
                // A song that ends goes quiet: every key up, every PSG channel off, every PCM channel stopped.
                MoveFm(end);
                foreach (int channel in _keys.Keys)
                {
                    Command(FmKey, (byte)channel);
                }

                for (int c = 0; c < PcmChannels; c++)
                {
                    if (_pcm[c] is not null)
                    {
                        Command((byte)(FmPcm | c), 0);
                    }
                }

                MovePsg(end);
                for (int c = 0; c < _volume.Length; c++)
                {
                    Psg.Add((byte)(PsgVolume | (c << 4) | PsgSilent));
                }

                end++;
            }

            MoveFm(end);
            Command(FmLoop, (byte)_fmLoop, (byte)(_fmLoop >> 8), (byte)(_fmLoop >> 16));
            MovePsg(end);
            Psg.Add(PsgLoop);
            Add24(Psg, _psgLoop);
        }

        /// <summary>The recordings, by id.</summary>
        public IReadOnlyList<Xgm2Sample> Samples(double rateHz) => [.. _samples.Select((s, i) => new Xgm2Sample(
            i + 1,
            s.Sample,
            s.Name,
            Padded(s.Data.Length),
            s.HalfSpeed,
            (int)Math.Round(s.HalfSpeed ? rateHz / 2 : rateHz, MidpointRounding.AwayFromZero)))];

        /// <summary>Where each recording starts in the sample block.</summary>
        public IReadOnlyList<int> SampleStarts()
        {
            List<int> starts = [];
            int at = 0;
            foreach ((sbyte[] data, _, _, _) in _samples)
            {
                starts.Add(at);
                at += Padded(data.Length);
            }

            return starts;
        }

        /// <summary>The sample block: every recording, padded with silence to 256 bytes.</summary>
        public byte[] SampleData()
        {
            IReadOnlyList<int> starts = SampleStarts();
            byte[] data = new byte[_samples.Sum(s => Padded(s.Data.Length))];
            for (int i = 0; i < _samples.Count; i++)
            {
                CopySigned(_samples[i].Data, data.AsSpan(starts[i]));
            }

            return data;
        }

        private static void CopySigned(sbyte[] from, Span<byte> to)
        {
            for (int i = 0; i < from.Length; i++)
            {
                to[i] = unchecked((byte)from[i]);
            }
        }

        private static int Padded(int length) => (length + Alignment - 1) / Alignment * Alignment;

        /// <summary>
        /// The loop frame: both streams start here again, and the chips are set as they were on the first time through,
        /// since the song's end leaves them otherwise.
        /// </summary>
        private void StartLoop(int f)
        {
            MoveFm(f);
            _fmLoop = Fm.Count;
            foreach (((int part, byte register), byte value) in _fm.OrderBy(r => r.Key.Part).ThenBy(r => RestoreOrder(r.Key.Register)))
            {
                AddRaw(part, register, value);
            }

            FlushRaw();
            if (_lfo is { } lfo)
            {
                Command(FmLfo, lfo);
            }

            if (_ch3 is { } ch3)
            {
                Command(ch3 ? FmCh3On : FmCh3Off);
            }

            foreach (byte key in _keys.Values)
            {
                Command(FmKey, key);
            }

            for (int c = 0; c < PcmChannels; c++)
            {
                if (_pcm[c] is { } playing && playing.Frame < f && (playing.Frame + (playing.Data.Length / playing.RateHz * stream.FrameRate)) > f)
                {
                    CarriedOverLoop++;
                }
            }

            MovePsg(f);
            _psgLoop = Psg.Count;
            FlushPsg(f, force: true);
        }

        /// <summary>
        /// The order registers are set back at the loop: a channel's block and F-number high byte (<c>$A4</c>–<c>$A6</c>,
        /// <c>$AC</c>–<c>$AE</c>) just before its low byte, which latches them; everything else by address.
        /// </summary>
        private static int RestoreOrder(byte register) =>
            register is (>= 0xa4 and <= 0xa6) or (>= 0xac and <= 0xae) ? ((register - 4) * 2) - 1 : register * 2;

        private void Encode(ChipEvent e, int frame)
        {
            switch (e.Kind)
            {
                case ChipEventKind.FmWrite:
                    EncodeFm(e.Part, e.Register, e.Value, frame);
                    break;
                case ChipEventKind.PsgWrite:
                    ApplyPsg(e.Value);
                    break;
                case ChipEventKind.PcmPlay:
                    FlushRaw();
                    MoveFm(frame);
                    if (!_ids.TryGetValue(e.Data!, out int id))
                    {
                        _samples.Add((e.Data!, e.HalfSpeed, e.Sample, e.Name));
                        id = _samples.Count;
                        _ids[e.Data!] = id;
                        if (id > MaxSamples)
                        {
                            throw new ConversionException(ExitCode.DoesNotFit, $"The song needs more than {MaxSamples} stored recordings; XGM2 holds {MaxSamples}.");
                        }
                    }

                    Command((byte)(FmPcm | e.Channel | (e.HalfSpeed ? FmPcmHalfSpeed : 0)), (byte)id);
                    _pcm[e.Channel] = (e.Data!, frame, e.HalfSpeed ? stream.PcmRateHz / 2 : stream.PcmRateHz);
                    break;
                default:
                    FlushRaw();
                    MoveFm(frame);
                    Command((byte)(FmPcm | e.Channel), 0);
                    _pcm[e.Channel] = null;
                    break;
            }
        }

        private void EncodeFm(int part, byte register, byte value, int frame)
        {
            MoveFm(frame);
            switch (register)
            {
                case RegKey:
                    FlushRaw();
                    Command(FmKey, value);
                    _keys[value & KeyChannelBits] = value;
                    break;
                case RegLfo:
                    FlushRaw();
                    Command(FmLfo, value);
                    _lfo = value;
                    break;
                case RegCh3Mode:
                    // The driver owns the timers in $27; only the CH3 mode bit is the music's.
                    FlushRaw();
                    _ch3 = (value & Ch3SpecialBit) != 0;
                    Command(_ch3.Value ? FmCh3On : FmCh3Off);
                    break;
                case RegDacData or RegDacEnable:
                    throw new InvalidOperationException($"The player wrote DAC register ${register:X2}, which XGM2's driver owns.");
                default:
                    AddRaw(part, register, value);
                    _fm[(part, register)] = value;
                    break;
            }
        }

        private void AddRaw(int part, byte register, byte value) => _raw.Add((part, register, value));

        /// <summary>
        /// The register writes waiting, in as few bytes as the driver takes them while keeping its own state right
        /// (<c>drv_xgm2_ym_fct.i80</c>): a channel whose whole patch is written gets one load instrument (<c>$2x</c>,
        /// 31 bytes for some 64 as raw writes), from the values the writes leave; every other total level goes through
        /// <c>$9x</c>, since the driver keeps each slot's level to add the game's volume to and a raw write to <c>$4x</c>
        /// would leave that stale; the rest are raw writes, 8 per <c>$Ex</c>, one part each. Their order within the group
        /// doesn't matter: they land in the same frame, and a register written twice is sent once, with its last value.
        /// </summary>
        private void FlushRaw()
        {
            if (_raw.Count == 0)
            {
                return;
            }

            HashSet<(int Part, int Channel)> patches = [];
            foreach (IGrouping<(int Part, int Channel), (int Part, byte Register, byte Value)> channel in _raw
                .Where(w => PatchChannel(w.Register) >= 0)
                .GroupBy(w => (w.Part, PatchChannel(w.Register))))
            {
                if (channel.Select(w => w.Register).Distinct().Count() == PatchRegisters)
                {
                    patches.Add(channel.Key);
                    Command(LoadInstrument(channel.Key.Part, channel.Key.Channel));
                }
            }

            // The last write of each register left, in the order the registers were first written.
            List<(int Part, byte Register, byte Value)> rest = [.. _raw
                .Where(w => !patches.Contains((w.Part, PatchChannel(w.Register))))
                .Select(w => (w.Part, w.Register))
                .Distinct()
                .Select(k => (k.Part, k.Register, _fm[k]))];
            List<(int Part, byte Register, byte Value)> group = [];
            foreach ((int part, byte register, byte value) in rest.Where(w => !IsTl(w.Register)))
            {
                if (group.Count == FmMaxRawWrites || (group.Count > 0 && group[0].Part != part))
                {
                    Raw(group);
                }

                group.Add((part, register, value));
            }

            Raw(group);
            foreach ((int part, byte register, byte value) in rest.Where(w => IsTl(w.Register)))
            {
                // $9x: slot in bits 3-2 and channel in bits 1-0 of x, as in the register; level in bits 7-1, part in bit 0.
                Command((byte)(FmSetTl | (register & 0x0f)), (byte)((value << 1) | part));
            }

            _raw.Clear();
        }

        /// <summary>
        /// A <c>$2x</c> command with the channel's patch as the registers hold it: per register <c>$30</c>–<c>$90</c> its four
        /// slots, then <c>$B0</c> and <c>$B4</c> (xgm2tool's <c>XGMFMCommand.convertToLoadInstCommand</c>).
        /// </summary>
        private byte[] LoadInstrument(int part, int channel)
        {
            byte[] command = new byte[1 + PatchRegisters];
            command[0] = (byte)(FmLoadInstrument | (part << 2) | channel);
            int d = 1;
            for (int r = 0; r < 7; r++)
            {
                for (int s = 0; s < 4; s++)
                {
                    command[d++] = _fm[(part, (byte)(RegFirstSlot + (r << 4) + (s << 2) + channel))];
                }
            }

            command[d++] = _fm[(part, (byte)(RegAlgorithm + channel))];
            command[d] = _fm[(part, (byte)(RegPan + channel))];
            return command;
        }

        /// <summary>One <c>$Ex</c> command of up to 8 writes to one part; empties <paramref name="group"/>.</summary>
        private void Raw(List<(int Part, byte Register, byte Value)> group)
        {
            if (group.Count > 0)
            {
                Command([(byte)(FmRawWrite | (group[0].Part << 3) | (group.Count - 1)), .. group.SelectMany(w => new[] { w.Register, w.Value })]);
                group.Clear();
            }
        }

        /// <summary>The channel (0–2 within its part) a patch register (<c>$30</c>–<c>$9F</c>, <c>$B0</c>–<c>$B6</c>) belongs to, or −1.</summary>
        private static int PatchChannel(byte register)
        {
            int channel = register & 0x03;
            bool patch = register is (>= RegFirstSlot and <= RegLastSlot) or (>= RegAlgorithm and <= RegPan + 2);
            return patch && channel < 3 ? channel : -1;
        }

        /// <summary>True for a slot's total level register (<c>$40</c>–<c>$4E</c>, not a fourth-channel address).</summary>
        private static bool IsTl(byte register) => register is >= RegTl and < RegTl + 0x10 && (register & 0x03) != 3;

        /// <summary>A PSG byte: a latch (channel, tone or volume, low bits) or a data byte (a tone's high 6 bits).</summary>
        private void ApplyPsg(byte value)
        {
            if ((value & 0x80) != 0)
            {
                _latchChannel = (value >> 5) & 0x03;
                _latchVolume = (value & 0x10) != 0;
                SetPsg(value & 0x0f, low: true);
            }
            else
            {
                SetPsg(value & 0x3f, low: false);
            }
        }

        private void SetPsg(int bits, bool low)
        {
            int c = _latchChannel;
            if (_latchVolume)
            {
                _volume[c] = bits & 0x0f;
            }
            else if (c == 3)
            {
                // Writing the noise register restarts the noise, so every write is sent, changed or not.
                _tone[c] = bits & 0x0f;
                _noiseWritten = true;
            }
            else
            {
                int tone = Math.Max(0, _tone[c]);
                _tone[c] = low ? (tone & 0x3f0) | bits : (tone & 0x0f) | (bits << 4);
            }
        }

        /// <summary>The PSG channels whose tone or volume changed this frame, or all that were ever set when <paramref name="force"/>.</summary>
        private void FlushPsg(int frame, bool force)
        {
            for (int c = 0; c < _tone.Length; c++)
            {
                bool send = _tone[c] >= 0 && (force || (c == 3 ? _noiseWritten : _tone[c] != _toneSent[c]));
                if (send)
                {
                    MovePsg(frame);
                    Psg.Add((byte)(PsgTone | (c << 2) | (c == 3 ? 0 : (_tone[c] >> 8) & 0x03)));
                    Psg.Add((byte)(_tone[c] & 0xff));
                    _toneSent[c] = _tone[c];
                }
            }

            _noiseWritten = false;
            for (int c = 0; c < _volume.Length; c++)
            {
                if (_volume[c] >= 0 && (force || _volume[c] != _volumeSent[c]))
                {
                    MovePsg(frame);
                    Psg.Add((byte)(PsgVolume | (c << 4) | _volume[c]));
                    _volumeSent[c] = _volume[c];
                }
            }
        }

        /// <summary>
        /// Adds one FM command. A frame holds less than 256 bytes of commands, or xgm2tool can't pack it; past that a frame
        /// splitter (<c>$F0</c>) goes first, as xgm2tool's own converter puts one (<c>XGM.rebuildFMCommands</c>). SGDK's
        /// driver ends the frame there and plays the rest on the next one without counting it (<c>FMCom_frameDelay</c>
        /// in <c>drv_xgm2_ym_fct.i80</c>), so the FM stream falls a frame behind the PSG; the next wait gives it back.
        /// </summary>
        private void Command(params byte[] bytes)
        {
            // The frame's last command (a wait of up to 2 bytes, or the splitter) counts too, and the frame stays under 256.
            if (_fmFrameBytes + bytes.Length > MaxFrameBytes - 1 - FrameEndBytes)
            {
                Fm.Add(FmFrameSplit);
                _fmFrameBytes = 0;
                _fmLag++;
                Splits++;
            }

            Fm.AddRange(bytes);
            _fmFrameBytes += bytes.Length;
        }

        /// <summary>Ends the FM stream's frames up to <paramref name="frame"/> with waits.</summary>
        private void MoveFm(int frame)
        {
            if (frame > _fmFrame)
            {
                FlushRaw();

                // Frames the driver already spent on a split frame come off the wait; with none left, the next commands
                // run in the frame the driver is on.
                int frames = frame - _fmFrame;
                int behind = Math.Min(frames, _fmLag);
                _fmLag -= behind;
                if (frames > behind)
                {
                    Wait(Fm, frames - behind, FmMaxShortWait, FmWaitLong, FmMaxLongWait);
                    _fmFrameBytes = 0;
                }

                _fmFrame = frame;
            }
        }

        /// <summary>Ends the PSG stream's frames up to <paramref name="frame"/> with waits.</summary>
        private void MovePsg(int frame)
        {
            if (frame > _psgFrame)
            {
                Wait(Psg, frame - _psgFrame, PsgMaxShortWait, PsgWaitLong, PsgMaxLongWait);
                _psgFrame = frame;
            }
        }

        /// <summary>
        /// Waits <paramref name="frames"/> frames: a short wait (<c>$0x</c>, x + 1 frames) up to <paramref name="maxShort"/>,
        /// otherwise long ones (<paramref name="longWait"/>, xx + <paramref name="maxShort"/> + 1 frames).
        /// </summary>
        private static void Wait(List<byte> to, int frames, int maxShort, byte longWait, int maxLong)
        {
            while (frames > 0)
            {
                if (frames <= maxShort)
                {
                    to.Add((byte)(frames - 1));
                    return;
                }

                int n = Math.Min(frames, maxLong);
                to.Add(longWait);
                to.Add((byte)(n - maxShort - 1));
                frames -= n;
            }
        }

        private static void Add24(List<byte> to, int value)
        {
            to.Add((byte)value);
            to.Add((byte)(value >> 8));
            to.Add((byte)(value >> 16));
        }
    }
}
