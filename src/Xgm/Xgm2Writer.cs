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
internal sealed record Xgm2Output(byte[] Bytes, IReadOnlyList<Xgm2Sample> Samples, int FmBytes, int PsgBytes, int Frames, int? LoopFrame)
{
    /// <summary>What the writer did to keep the FM stream within SGDK's driver budget.</summary>
    public Xgm2Stats Stats { get; init; } = Xgm2Stats.None;
}

/// <summary>What the XGM2 writer did to keep each frame's FM commands small (docs/adr/0139-xgm2-frame-budget.md), for <c>--verbose</c>.</summary>
/// <param name="Splits">Frame splitters left in the stream.</param>
/// <param name="BusyFrames">Frames that don't split but use 80 % of the budget or more.</param>
/// <param name="Dropped">Register writes not sent because the chip already held their value.</param>
/// <param name="LoadInstruments">Load instrument commands (<c>$2x</c>).</param>
/// <param name="Compact">Compact commands: <c>$3x</c>–<c>$8x</c>, <c>$Ax</c>, <c>$Bx</c> and <c>$Dx</c>.</param>
/// <param name="Moved">Register writes moved to an earlier frame where their channel is silent.</param>
/// <param name="Stuck">Register writes in frames that still split, which had no earlier frame to go to.</param>
internal sealed record Xgm2Stats(int Splits, int BusyFrames, int Dropped, int LoadInstruments, int Compact, int Moved, int Stuck)
{
    /// <summary>Nothing done: a file not made by <see cref="Xgm2Writer.Write"/>.</summary>
    public static readonly Xgm2Stats None = new(0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Writes the uncompiled XGM2 file SGDK's xgm2tool and rescomp read (docs/formats/xgm.md#xgm2-layout-from-xgm2txt-version-byte-0x10,
/// plan T10.1, docs/adr/0099-xgm2-writer-records-the-player.md): single track, unpacked, no GD3. The FM stream uses
/// load instrument (<c>$2x</c>) for a channel's whole patch, total level (<c>$9x</c>), raw register writes (<c>$Ex</c>)
/// for the rest, XGM2's compact FM commands (<c>$3x</c>–<c>$8x</c>, <c>$Ax</c>, <c>$Bx</c>, <c>$Dx</c>; see
/// <see cref="Xgm2FmState"/>), key (<c>$4x</c>, <c>$F8</c>), LFO (<c>$F9</c>), CH3 mode (<c>$FA</c>/<c>$FB</c>) and PCM
/// (<c>$1x</c>) commands; the PSG stream tone (<c>$2x</c>) and volume (<c>$8x</c>–<c>$Bx</c>) commands; both end each
/// frame with a wait. Only what changes on the chip is sent, and patch writes move out of frames over the driver's
/// budget where their channel is silent (docs/adr/0139-xgm2-frame-budget.md); golden tests guard the output.
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

    /// <summary>
    /// Silence in the file's sample block. The sample bytes are unsigned: SGDK's xgm2tool (which rescomp runs) reads them
    /// as they are and adds <c>$80</c> to each when it compiles the song (<c>XGM.getPCMDataArray</c>, "sign the sample"),
    /// which gives the driver its signed bytes. xgm2.txt calls the samples signed (docs/adr/0127-xgm2-unsigned-samples.md).
    /// </summary>
    private const byte UnsignedSilence = 0x80;

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
    private const byte FmLoadInstrument = 0x20;
    private const byte FmKeyAll = 0x40;
    private const byte FmKeyAllOn = 0x08;
    private const byte FmKeySequence = 0x50;
    private const byte FmKeySequenceOnOff = 0x08;
    private const byte FmSetFreqWait = 0x80;
    private const byte FmSetTl = 0x90;
    private const byte FmFreqDeltaWait = 0xb0;
    private const byte FmTlDeltaWait = 0xd0;
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

    /// <summary>The bytes of commands a frame holds before the writer splits it: 222, room left for the splitter and the frame's end.</summary>
    private const int FrameBudget = MaxFrameBytes - 1 - FrameEndBytes;

    /// <summary>
    /// A frame this busy, 80 % of <see cref="FrameBudget"/> (178 bytes) or more, is noted (docs/xgm2-frame-budget.md,
    /// change 4): it has room for about one more FM note start, and busy frames are what keep SGDK's Z80 driver from
    /// refilling its PCM buffer in time. Change 3 also prefers to move writes into frames below it.
    /// </summary>
    private const int BusyFrameBytes = ((FrameBudget * 4) + 4) / 5;

    /// <summary>Frames named in the busy-frame notes.</summary>
    private const int BusiestFramesNoted = 5;

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
    private const byte RegTl = 0x40;

    /// <summary>A slot's total level at its quietest: 127, about 95 dB down (docs/genesis-audio.md).</summary>
    private const byte SilentTl = 0x7f;
    private const byte Ch3SpecialBit = 0x40;
    private const int KeyChannelBits = 0x07;

    /// <summary>The PCM channels: SGDK's XGM2 driver mixes three.</summary>
    private const int PcmChannels = 3;

    /// <summary>The six FM channels as <c>$28</c> numbers them: 0–2, then 4–6 for the second part.</summary>
    private static readonly int[] KeyChannels = [0, 1, 2, 4, 5, 6];

    /// <summary>
    /// Frames of each recording kept past the last frame a play reaches: the driver starts and stops PCM on frame
    /// boundaries, and a frame splitter can make a stop a frame late.
    /// </summary>
    private const int HeardMarginFrames = 2;

    /// <summary>Writes <paramref name="stream"/> as an XGM2 file.</summary>
    /// <param name="stream">The song's chip writes, frame by frame.</param>
    /// <param name="notes">Collects the conversion notes.</param>
    /// <param name="pcmBudget">The most bytes the recordings may take (<c>--max-pcm-bytes</c>), or <c>null</c> for what XGM2 holds.</param>
    /// <exception cref="ConversionException">The song needs more recordings, PCM bytes or stream bytes than XGM2 or the budget holds (exit 6).</exception>
    public static Xgm2Output Write(GenesisStream stream, ConversionNotes notes, long? pcmBudget = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(notes);
        Encoder encoder = new(stream);
        encoder.Run();

        // Frames that still split give up their patch writes to earlier frames where the channel is silent (change 3).
        PatchMoves moves = Xgm2PatchMover.Move(stream, Load, Load(encoder), FrameBudget, BusyFrameBytes - 1);
        if (moves.Moved > 0)
        {
            encoder = new Encoder(moves.Stream);
            encoder.Run();
        }

        IReadOnlyList<Xgm2Sample> samples = encoder.Samples(stream.PcmRateHz);
        if (stream.LoopFrame > 0)
        {
            notes.Add("The song loops back to frame {Frame}, not to its start; SGDK's xgm2tool (which rescomp runs) packs the song without moving the loop point, so in an SGDK game the loop jumps to the wrong place", stream.LoopFrame);
        }

        if (encoder.Splits > 0)
        {
            notes.Add("{Count} frame splits: some frames hold more FM writes than SGDK's XGM2 driver takes at once, so the writes after each split play one frame later", encoder.Splits);
        }

        int busy = NoteBusyFrames(stream, encoder, notes);

        if (encoder.CarriedOverLoop > 0)
        {
            notes.Add("{Count} recordings still sound where the song loops back; XGM2 can't start them partway, so they are silent from the second time through", encoder.CarriedOverLoop);
        }

        byte[] sdat = encoder.SampleData();
        if (sdat.Length > pcmBudget)
        {
            throw new ConversionException(ExitCode.DoesNotFit, $"The XGM2 recordings take {sdat.Length} bytes; the PCM budget (--max-pcm-bytes) is {pcmBudget}.");
        }

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
        return new Xgm2Output(file, samples, fm.Length, psg.Length, stream.Frames.Count, stream.LoopFrame)
        {
            Stats = new Xgm2Stats(encoder.Splits, busy, encoder.Dropped, encoder.LoadInstruments, encoder.Compact, moves.Moved, moves.Stuck),
        };
    }

    /// <summary>Encodes <paramref name="stream"/> for its frame loads, for <see cref="Xgm2PatchMover"/>.</summary>
    private static FrameLoad Load(GenesisStream stream)
    {
        Encoder encoder = new(stream);
        encoder.Run();
        return Load(encoder);
    }

    private static FrameLoad Load(Encoder encoder) => new(encoder.FrameBytes, encoder.SplitFrames);

    /// <summary>
    /// Notes the frames that don't split but use <see cref="BusyFrameBytes"/> or more (change 4 of
    /// docs/xgm2-frame-budget.md): how many, then the busiest few with where they are in the song, so a musician can find
    /// them in the tracker. Returns how many there are.
    /// </summary>
    private static int NoteBusyFrames(GenesisStream stream, Encoder encoder, ConversionNotes notes)
    {
        HashSet<int> split = [.. encoder.SplitFrames];
        List<int> busy = [.. Enumerable.Range(0, encoder.FrameBytes.Count).Where(f => encoder.FrameBytes[f] >= BusyFrameBytes && !split.Contains(f))];
        List<int> busiest = [.. busy.OrderByDescending(f => encoder.FrameBytes[f]).ThenBy(f => f).Take(BusiestFramesNoted)];
        for (int i = 0; i < busiest.Count; i++)
        {
            int f = busiest[i];
            double seconds = f / stream.FrameRate;
            (int order, int row) = stream.Positions is { } positions && f < positions.Count ? positions[f] : (-1, -1);
            string channels = string.Join(", ", Enumerable.Range(0, 6).Where(c => (encoder.FrameChannels[f] & (1 << c)) != 0).Select(c => FormattableString.Invariant($"FM{c + 1}")));
            if (i == 0)
            {
                notes.Add(
                    "{Count} frames use 80% or more of the {Budget} bytes of FM commands SGDK's XGM2 driver takes per frame; the busiest: frame {Frame} ({Seconds:0.00} s, order {Order} row {Row}) {Bytes} bytes, {Writes} register writes on {Channels:l}",
                    busy.Count,
                    FrameBudget,
                    f,
                    seconds,
                    order,
                    row,
                    encoder.FrameBytes[f],
                    encoder.FrameWrites[f],
                    channels);
            }
            else
            {
                notes.Add(
                    "Busy XGM2 frame {Frame} ({Seconds:0.00} s, order {Order} row {Row}): {Bytes} bytes, {Writes} register writes on {Channels:l}",
                    f,
                    seconds,
                    order,
                    row,
                    encoder.FrameBytes[f],
                    encoder.FrameWrites[f],
                    channels);
            }
        }

        return busy.Count;
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
        /// <summary>Each recording's id, by its bytes: copies with the same bytes are stored once, as the capacity check counts them.</summary>
        private readonly Dictionary<sbyte[], int> _ids = new(SameBytes.Instance);
        private readonly List<(sbyte[] Data, bool HalfSpeed, int Sample, string Name)> _samples = [];

        /// <summary>The bytes of each recording some play reaches before its channel stops or plays something else.</summary>
        private readonly List<int> _heard = [];

        /// <summary>Every YM3438 register written, by part and register, but for the key, LFO, CH3 and DAC registers.</summary>
        private readonly SortedDictionary<(int Part, byte Register), byte> _fm = [];

        /// <summary>The last <c>$28</c> write of each channel, by its channel bits.</summary>
        private readonly SortedDictionary<int, byte> _keys = [];

        private readonly List<(int Part, byte Register, byte Value)> _raw = [];

        /// <summary>What the chip and SGDK's driver hold from the FM stream so far.</summary>
        private readonly Xgm2FmState _state = new();

        /// <summary>The last command sent and where it starts, while nothing has come after it in its frame; or <c>null</c>.</summary>
        private (int Position, FmCommand Command)? _last;

        /// <summary>Each PCM channel's playing recording (its id), the frame it started and its rate, or <c>null</c>.</summary>
        private readonly (int Id, int Frame, double RateHz)?[] _pcm = new (int, int, double)?[PcmChannels];

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
        /// <summary>The frame of the FM bytes written so far.</summary>
        private int _fmFrame;

        /// <summary>The frame being encoded: its wait is written with its first command, so a frame whose writes all drop costs nothing.</summary>
        private int _fmTarget;
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

        /// <summary>Each song frame's FM bytes, the wait that ends it and any splitter included.</summary>
        public List<int> FrameBytes { get; } = [];

        /// <summary>Each song frame's YM3438 register writes, as SGDK's driver makes them for its commands.</summary>
        public List<int> FrameWrites { get; } = [];

        /// <summary>Each song frame's FM channels written: bit <c>part × 3 + channel</c>.</summary>
        public List<int> FrameChannels { get; } = [];

        /// <summary>The song frames a frame splitter went into, in order.</summary>
        public List<int> SplitFrames { get; } = [];

        /// <summary>Register writes not sent because the chip already held their value.</summary>
        public int Dropped { get; private set; }

        /// <summary>Load instrument commands sent.</summary>
        public int LoadInstruments { get; private set; }

        /// <summary>Compact commands sent (<c>$3x</c>–<c>$8x</c>, <c>$Ax</c>, <c>$Bx</c>, <c>$Dx</c>).</summary>
        public int Compact { get; private set; }

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

            // The loop (or end) command ends the frame it is in (FMCom_loop and PSGCOM_loop in SGDK's driver), so it goes
            // in the song's last frame: one that loops is its own last frame, one that ends gets a frame of silence.
            int last = Math.Max(0, stream.Frames.Count - 1);
            if (stream.LoopFrame is null)
            {
                // A song that ends goes quiet: every key up, every PSG channel off, every PCM channel stopped.
                last = stream.Frames.Count;
                MoveFm(last);
                foreach (int channel in _keys.Keys)
                {
                    SendKey((byte)channel);
                }

                MovePsg(last);
                for (int c = 0; c < _volume.Length; c++)
                {
                    Psg.Add((byte)(PsgVolume | (c << 4) | PsgSilent));
                }
            }

            // Recordings still sounding stop where the song ends or jumps back, so none plays on into the loop's opening;
            // stopping at the loop frame instead would cut those that cross it the first time through.
            MoveFm(last);
            for (int c = 0; c < PcmChannels; c++)
            {
                if (_pcm[c] is { } playing && Sounding(playing, last))
                {
                    Command((byte)(FmPcm | c), 0);
                }

                Stop(c, last);
            }

            Command(FmLoop, (byte)_fmLoop, (byte)(_fmLoop >> 8), (byte)(_fmLoop >> 16));

            // A frame splitter not yet taken back leaves FM behind: PSG waits for it, so both loop (or end) together.
            MovePsg(last);
            Wait(Psg, _fmLag, PsgMaxShortWait, PsgWaitLong, PsgMaxLongWait);
            Psg.Add(PsgLoop);
            Add24(Psg, _psgLoop);
        }

        /// <summary>True while the recording playing as <paramref name="playing"/> still sounds at the start of <paramref name="frame"/>.</summary>
        private bool Sounding((int Id, int Frame, double RateHz) playing, int frame) =>
            playing.Frame + (_samples[playing.Id - 1].Data.Length / playing.RateHz * stream.FrameRate) > frame;

        /// <summary>
        /// Ends the play on PCM channel <paramref name="channel"/> at <paramref name="frame"/>, noting how much of its recording
        /// it reached, with a margin for frame timing; the file stores no more than the most any play reaches.
        /// </summary>
        private void Stop(int channel, int frame)
        {
            if (_pcm[channel] is { } playing)
            {
                int id = playing.Id - 1;
                double seconds = (frame - playing.Frame + HeardMarginFrames) / stream.FrameRate;
                int bytes = (int)Math.Min(_samples[id].Data.Length, Math.Ceiling(seconds * playing.RateHz));
                _heard[id] = Math.Max(_heard[id], bytes);
                _pcm[channel] = null;
            }
        }

        /// <summary>The recordings, by id.</summary>
        public IReadOnlyList<Xgm2Sample> Samples(double rateHz) => [.. _samples.Select((s, i) => new Xgm2Sample(
            i + 1,
            s.Sample,
            s.Name,
            Padded(_heard[i]),
            s.HalfSpeed,
            (int)Math.Round(s.HalfSpeed ? rateHz / 2 : rateHz, MidpointRounding.AwayFromZero)))];

        /// <summary>Where each recording starts in the sample block.</summary>
        public IReadOnlyList<int> SampleStarts()
        {
            List<int> starts = [];
            int at = 0;
            foreach (int heard in _heard)
            {
                starts.Add(at);
                at += Padded(heard);
            }

            return starts;
        }

        /// <summary>
        /// The sample block: every recording, as much of it as is heard, padded with silence to 256 bytes, stored
        /// unsigned (silence is <see cref="UnsignedSilence"/>) because xgm2tool adds <c>$80</c> to every byte it compiles
        /// (docs/adr/0127-xgm2-unsigned-samples.md).
        /// </summary>
        public byte[] SampleData()
        {
            IReadOnlyList<int> starts = SampleStarts();
            byte[] data = new byte[_heard.Sum(Padded)];
            data.AsSpan().Fill(UnsignedSilence);
            for (int i = 0; i < _samples.Count; i++)
            {
                CopyUnsigned(_samples[i].Data.AsSpan(0, _heard[i]), data.AsSpan(starts[i]));
            }

            return data;
        }

        /// <summary>Copies signed 8-bit PCM as unsigned: the sign bit flipped, so 0 becomes <see cref="UnsignedSilence"/>.</summary>
        private static void CopyUnsigned(ReadOnlySpan<sbyte> from, Span<byte> to)
        {
            for (int i = 0; i < from.Length; i++)
            {
                to[i] = (byte)(unchecked((byte)from[i]) ^ UnsignedSilence);
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
            AdvanceFm();
            _fmLoop = Fm.Count;

            // The second time through may leave the chip otherwise than the first: everything is sent again, and what the
            // restore doesn't set is unknown from here.
            _state.Forget();

            // A slot whose level the first time through hadn't set yet was silent then. The song's end may leave a note
            // releasing on it, which the loop's first writes (a pan, say) would bring back as a new note, so it goes
            // quiet (docs/adr/0134-loop-silences-release-tails.md).
            foreach ((int part, byte register) in Enumerable.Range(0, 2)
                .SelectMany(part => Enumerable.Range(0, 0x10).Select(r => (part, (byte)(RegTl + r))))
                .Where(k => Xgm2FmState.IsTl(k.Item2) && !_fm.ContainsKey(k)))
            {
                Send(new FmCommand([(byte)(FmSetTl | (register & 0x0f)), (byte)((SilentTl << 1) | part)], FmCommandKind.Other, 1, 1 << ((part * 3) + (register & 0x03))));
                _state.SetLevel(part, register, SilentTl);
            }

            foreach (((int part, byte register), byte value) in _fm.OrderBy(r => r.Key.Part).ThenBy(r => RestoreOrder(r.Key.Register)))
            {
                AddRaw(part, register, value);
            }

            FlushRaw();
            if (_lfo is { } lfo)
            {
                Send(new FmCommand([FmLfo, lfo], FmCommandKind.Other, 1, 0));
            }

            if (_ch3 is { } ch3)
            {
                Send(new FmCommand([ch3 ? FmCh3On : FmCh3Off], FmCommandKind.Other, 1, 0));
            }

            // Every channel's key as it was, up for one not yet keyed: a note held at the song's end mustn't go on sounding.
            foreach (int channel in KeyChannels)
            {
                SendKey(_keys.TryGetValue(channel, out byte key) ? key : (byte)channel);
            }

            for (int c = 0; c < PcmChannels; c++)
            {
                if (_pcm[c] is { } playing && playing.Frame < f && Sounding(playing, f))
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
                        _heard.Add(0);
                        id = _samples.Count;
                        _ids[e.Data!] = id;
                        if (id > MaxSamples)
                        {
                            throw new ConversionException(ExitCode.DoesNotFit, $"The song needs more than {MaxSamples} stored recordings; XGM2 holds {MaxSamples}.");
                        }
                    }

                    Command((byte)(FmPcm | e.Channel | (e.HalfSpeed ? FmPcmHalfSpeed : 0)), (byte)id);
                    Stop(e.Channel, frame);
                    _pcm[e.Channel] = (id, frame, e.HalfSpeed ? stream.PcmRateHz / 2 : stream.PcmRateHz);
                    break;
                default:
                    FlushRaw();
                    MoveFm(frame);
                    Command((byte)(FmPcm | e.Channel), 0);
                    Stop(e.Channel, frame);
                    break;
            }
        }

        private void EncodeFm(int part, byte register, byte value, int frame)
        {
            MoveFm(frame);
            switch (register)
            {
                case RegKey:
                    // A key on of all four operators can ride on the channel's frequency set ($3x, bit 15).
                    int channel = value & KeyChannelBits;
                    bool allOn = (value & 0xf0) == 0xf0 && (channel & 0x03) != 3;
                    if (!FlushRaw(allOn ? channel : -1))
                    {
                        SendKey(value);
                    }

                    _keys[channel] = value;
                    break;
                case RegLfo:
                    FlushRaw();
                    Send(new FmCommand([FmLfo, value], FmCommandKind.Other, 1, 0));
                    _lfo = value;
                    break;
                case RegCh3Mode:
                    // The driver owns the timers in $27; only the CH3 mode bit is the music's.
                    FlushRaw();
                    _ch3 = (value & Ch3SpecialBit) != 0;
                    Send(new FmCommand([_ch3.Value ? FmCh3On : FmCh3Off], FmCommandKind.Other, 1, 0));
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
        /// Sends the register writes waiting in as few bytes as the driver takes them while keeping its own state right
        /// (<see cref="Xgm2FmState"/>; docs/adr/0139-xgm2-frame-budget.md). <paramref name="keyOn"/> is the channel keyed
        /// on, all four operators, straight after (or −1); returns true when that key on went into the last command.
        /// </summary>
        private bool FlushRaw(int keyOn = -1)
        {
            if (_raw.Count == 0)
            {
                return false;
            }

            bool afterKeyOff = keyOn >= 0 && _fmTarget == _fmFrame && _last is { Command: { Kind: FmCommandKind.KeyOff } off } && off.Key == keyOn;
            FmFlush flush = _state.Flush(_raw, keyOn, afterKeyOff);
            _raw.Clear();
            Dropped += flush.Dropped;
            foreach (FmCommand command in flush.Commands)
            {
                if (command.TakesKeyOff)
                {
                    // The key off just sent is now bit 14 of the frequency set: take its byte back.
                    Fm.RemoveAt(Fm.Count - 1);
                    _fmFrameBytes--;
                    FrameBytes[_fmFrame]--;
                    FrameWrites[_fmFrame]--;
                    Compact--;
                }

                Send(command);
            }

            return flush.KeyFolded;
        }

        /// <summary>
        /// A <c>$28</c> write: <c>$4x</c> (1 byte) for a key on or off of all four operators, folded with the key just sent
        /// for the same channel into a key sequence (<c>$5x</c>) when nothing came between; otherwise <c>$F8</c>.
        /// </summary>
        private void SendKey(byte value)
        {
            AdvanceFm();
            int channel = value & KeyChannelBits;
            int operators = value & 0xf0;
            int bit = (channel & 0x03) == 3 ? 0 : Xgm2FmState.KeyChannelBit(channel);
            if (bit == 0 || operators is not (0 or 0xf0))
            {
                Send(new FmCommand([FmKey, value], FmCommandKind.Other, 1, bit));
                return;
            }

            bool on = operators != 0;
            if (_last is { } last && last.Command.Key == channel && last.Command.Kind == (on ? FmCommandKind.KeyOff : FmCommandKind.KeyOn) && last.Position == Fm.Count - 1)
            {
                // $5x: x = channel and port, bit 3 the order (0: off then on, 1: on then off).
                Fm[last.Position] = (byte)(FmKeySequence | channel | (on ? 0 : FmKeySequenceOnOff));
                FrameWrites[_fmFrame]++;
                _last = null;
                return;
            }

            Send(new FmCommand([(byte)(FmKeyAll | channel | (on ? FmKeyAllOn : 0))], on ? FmCommandKind.KeyOn : FmCommandKind.KeyOff, 1, bit) { Key = channel });
        }

        /// <summary>Adds <paramref name="command"/> and remembers it as the last one, for the folds.</summary>
        private void Send(FmCommand command)
        {
            Command(command.Bytes);
            _last = (Fm.Count - command.Bytes.Length, command);
            FrameWrites[_fmFrame] += command.Writes;
            FrameChannels[_fmFrame] |= command.Channels;
            switch (command.Bytes[0] >> 4)
            {
                case FmLoadInstrument >> 4:
                    LoadInstruments++;
                    break;
                case >= 0x3 and <= 0x8 or 0xa or 0xb or 0xd:
                    Compact++;
                    break;
            }
        }

        /// <summary>
        /// Ends the frame in its last command instead of a one-frame wait (<c>$00</c>), when that is a frequency set, a
        /// frequency delta or a level that fits a delta: <c>$8x</c>, <c>$Bx</c> and <c>$Dx</c> end the driver's frame with no
        /// wait set, which is the same (<c>FM_nextFrame_done</c> in <c>drv_xgm2_ym_fct.i80</c>).
        /// </summary>
        private bool FoldFrameEnd()
        {
            if (_last is not { } last)
            {
                return false;
            }

            int x = Fm[last.Position] & 0x0f;
            switch (last.Command.Kind)
            {
                case FmCommandKind.Frequency:
                    Fm[last.Position] = (byte)(FmSetFreqWait | x);
                    return true;
                case FmCommandKind.FrequencyDelta:
                    Fm[last.Position] = (byte)(FmFreqDeltaWait | x);
                    return true;
                case FmCommandKind.Level when last.Command.LevelDelta is { } delta:
                    Fm[last.Position] = (byte)(FmTlDeltaWait | x);
                    Fm[last.Position + 1] = delta;
                    Compact++;
                    return true;
                default:
                    return false;
            }
        }

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
                if (force && _volume[c] < 0)
                {
                    // Not yet set at the loop frame: off, so a channel the song's end leaves sounding is quiet again.
                    MovePsg(frame);
                    Psg.Add((byte)(PsgVolume | (c << 4) | PsgSilent));
                    _volumeSent[c] = PsgSilent;
                }
                else if (_volume[c] >= 0 && (force || _volume[c] != _volumeSent[c]))
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
            AdvanceFm();
            Grow(_fmFrame);
            _last = null;

            // The frame's last command (a wait of up to 2 bytes, or the splitter) counts too, and the frame stays under 256.
            if (_fmFrameBytes + bytes.Length > FrameBudget)
            {
                Fm.Add(FmFrameSplit);
                _fmFrameBytes = 0;
                _fmLag++;
                Splits++;
                FrameBytes[_fmFrame]++;
                SplitFrames.Add(_fmFrame);
            }

            Fm.AddRange(bytes);
            _fmFrameBytes += bytes.Length;
            FrameBytes[_fmFrame] += bytes.Length;
        }

        /// <summary>Makes the per-frame counts reach <paramref name="frame"/>.</summary>
        private void Grow(int frame)
        {
            while (FrameBytes.Count <= frame)
            {
                FrameBytes.Add(0);
                FrameWrites.Add(0);
                FrameChannels.Add(0);
            }
        }

        /// <summary>
        /// Moves the FM stream on to <paramref name="frame"/>, after sending what waits for the frame before; the wait itself is
        /// written with the frame's first byte (<see cref="AdvanceFm"/>). A one-frame wait goes into the frame's
        /// last command where it can (<see cref="FoldFrameEnd"/>).
        /// </summary>
        private void MoveFm(int frame)
        {
            if (frame > _fmTarget)
            {
                FlushRaw();
                _fmTarget = frame;
            }
        }

        /// <summary>
        /// Writes the wait from the frame of the bytes so far to the frame being encoded (<see cref="MoveFm"/>), just before
        /// that frame's first byte.
        /// </summary>
        private void AdvanceFm()
        {
            if (_fmTarget > _fmFrame)
            {
                // Frames the driver already spent on a split frame come off the wait; with none left, the next commands
                // run in the frame the driver is on.
                int frames = _fmTarget - _fmFrame;
                int behind = Math.Min(frames, _fmLag);
                _fmLag -= behind;
                if (frames > behind)
                {
                    if (frames - behind > 1 || !FoldFrameEnd())
                    {
                        Grow(_fmFrame);
                        int before = Fm.Count;
                        Wait(Fm, frames - behind, FmMaxShortWait, FmWaitLong, FmMaxLongWait);
                        FrameBytes[_fmFrame] += Fm.Count - before;
                    }

                    _fmFrameBytes = 0;
                }

                _last = null;
                _fmFrame = _fmTarget;
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

    /// <summary>Compares recordings by their bytes, so separately made copies of the same sound share one id.</summary>
    private sealed class SameBytes : IEqualityComparer<sbyte[]>
    {
        /// <summary>The one instance.</summary>
        public static readonly SameBytes Instance = new();

        /// <inheritdoc/>
        public bool Equals(sbyte[]? x, sbyte[]? y) => ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));

        /// <inheritdoc/>
        public int GetHashCode(sbyte[] obj)
        {
            HashCode hash = new();
            hash.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(obj.AsSpan()));
            return hash.ToHashCode();
        }
    }
}
