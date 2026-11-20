using Mod2Xgm.Dmf;
using Mod2Xgm.Render.Fm;
using Mod2Xgm.Render.Psg;

namespace Mod2Xgm.Tests.Dmf.Sanity;

/// <summary>
/// Plays a Genesis DMF the way DefleMask does, as far as docs/formats/dmf.md records it, and logs what it tells the
/// chips, one frame per tick (the DMF sanity check, docs/testing.md#dmf-sanity-check). Test-only: the converter never
/// reads DMF. It writes what DefleMask's VGM export writes — a note's patch, key-off, frequency and key-on; volumes as
/// carrier total levels; PSG tone and attenuation; DAC samples as PCM plays — so the logs can be compared with that export.
/// Where DefleMask's rule isn't known, it takes the simplest reading of docs/formats/dmf.md and lists the effect in
/// <see cref="DmfPlayback.Unsupported"/> when it doesn't play it at all; the comparisons show where it is wrong.
/// </summary>
internal sealed class DmfPlayer
{
    /// <summary>A song that hasn't looped or ended after this many ticks (30 minutes at 60 Hz) is cut there.</summary>
    public const int MaxTicks = 60 * 60 * 30;

    /// <summary>Note value for "note off" (docs/formats/dmf.md#8-patterns).</summary>
    private const int NoteOff = 100;

    private readonly DmfFile _file;
    private readonly List<List<ChipWrite>> _frames = [];
    private readonly Channel[] _channels;
    private readonly SortedSet<string> _unsupported = [];
    private readonly sbyte[]?[] _samples;
    private readonly Ch3Keys _ch3 = new();
    private int _frame;
    private bool _speedB;
    private int _arpSpeed = 1;

    private DmfPlayer(DmfFile file)
    {
        _file = file;
        bool ext = file.System == DmfSystem.GenesisExtendedChannel3;
        List<Channel> channels = [new FmChannel(this, 0), new FmChannel(this, 1)];
        if (ext)
        {
            channels.AddRange(Enumerable.Range(0, 4).Select(op => (Channel)new Ch3OpChannel(this, op)));
        }
        else
        {
            channels.Add(new FmChannel(this, 2));
        }

        channels.AddRange([new FmChannel(this, 3), new FmChannel(this, 4), new FmChannel(this, 5), new PsgChannel(this, 0), new PsgChannel(this, 1), new PsgChannel(this, 2), new NoiseChannel(this)]);
        _channels = [.. channels];
        _samples = new sbyte[file.Samples.Count][];
    }

    /// <summary>The YM2612's clock in DefleMask's VGM export, whatever the region (docs/formats/dmf.md#what-deflemask-shows).</summary>
    private static double YmClockHz => Ym3438.NtscClockHz;

    /// <summary>The SN76489's clock in DefleMask's VGM export.</summary>
    private static double PsgClockHz => Sn76489.NtscClockHz;

    /// <summary>Plays <paramref name="file"/> from its first order until it loops back to a row it played, or runs off its last order.</summary>
    public static DmfPlayback Play(DmfFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        DmfPlayer player = new(file);
        int? loop = player.Run();
        return new DmfPlayback(new ChipLog([.. player._frames], loop, file.Timing.TickRateHz), [.. player._unsupported]);
    }

    /// <summary>Plays the song; returns the loop frame, or <c>null</c> when the song ends.</summary>
    private int? Run()
    {
        Initialize();
        int speed1 = _file.Timing.TickTime1;
        int speed2 = _file.Timing.TickTime2;
        int timeBase = _file.Timing.TimeBase + 1;
        Dictionary<(int Order, int Row), int> visited = [];
        int order = 0;
        int row = 0;
        while (_frame < MaxTicks)
        {
            if (visited.TryGetValue((order, row), out int loopFrame))
            {
                return loopFrame;
            }

            visited[(order, row)] = _frame;
            DmfCell[] cells = [.. _file.Channels.Select(c => c.Patterns[order][row])];

            // Speeds set on the row count for the row itself.
            int? jumpOrder = null;
            int? breakRow = null;
            foreach (DmfEffect e in cells.SelectMany(c => c.Effects).Where(e => e.Code >= 0))
            {
                int value = Math.Max(0, e.Value);
                switch (e.Code)
                {
                    case 0x09 when value > 0:
                        speed1 = value;
                        break;
                    case 0x0F when value > 0:
                        speed2 = value;
                        break;
                    case 0x0B:
                        jumpOrder ??= value;
                        break;
                    case 0x0D:
                        breakRow ??= value;
                        break;
                }
            }

            int ticks = (_speedB ? speed2 : speed1) * timeBase;
            _speedB = !_speedB;
            for (int c = 0; c < _channels.Length; c++)
            {
                _channels[c].StartRow(cells[c], ticks);
            }

            for (int tick = 0; tick < ticks; tick++)
            {
                foreach (Channel channel in _channels)
                {
                    channel.Tick(tick);
                }

                _frame++;
            }

            // The jump rules (docs/formats/dmf.md#effects): 0Dxx overrides 0Bxx and is ignored on the last order.
            bool last = order == _file.MatrixRows - 1;
            if (breakRow is { } r && !last)
            {
                order++;
                row = Math.Min(r, _file.RowsPerPattern - 1);
            }
            else if (jumpOrder is { } o)
            {
                order = Math.Min(o, _file.MatrixRows - 1);
                row = 0;
            }
            else if (++row >= _file.RowsPerPattern)
            {
                row = 0;
                if (++order >= _file.MatrixRows)
                {
                    // Off the end with no jump: DefleMask's VGM export ends here, without a loop.
                    return null;
                }
            }
        }

        _unsupported.Add($"cut at {MaxTicks} ticks without looping");
        return null;
    }

    /// <summary>What DefleMask's export writes before the first row: LFO on at rate 0, CH3 mode, centre pans, keys up, PSG off.</summary>
    private void Initialize()
    {
        Fm(0, 0x22, 0x08);
        Fm(0, 0x27, _file.System == DmfSystem.GenesisExtendedChannel3 ? 0x40 : 0x00);
        for (int part = 0; part < 2; part++)
        {
            for (int ch = 0; ch < 3; ch++)
            {
                Fm(part, 0xB4 + ch, 0xC0);
            }
        }

        foreach (int key in new[] { 0, 1, 2, 4, 5, 6 })
        {
            Fm(0, 0x28, key);
        }

        foreach (int psg in new[] { 0x9F, 0xBF, 0xDF, 0xFF })
        {
            Psg(psg);
        }
    }

    private void Add(ChipWrite write)
    {
        while (_frames.Count <= _frame)
        {
            _frames.Add([]);
        }

        _frames[_frame].Add(write);
    }

    private void Fm(int part, int register, int value) => Add(ChipWrite.Fm(part, register, value & 0xFF));

    private void Psg(int value) => Add(ChipWrite.Psg(value & 0xFF));

    private void Unsupported(string what) => _unsupported.Add(what);

    /// <summary>
    /// DMF sample <paramref name="index"/> as DefleMask's export stores it (docs/formats/dmf.md#9-pcm-samples): cut, gain
    /// amp / 50, resampled by its pitch step when loaded (nearest point), then the top 8 bits.
    /// </summary>
    private sbyte[] SampleData(int index)
    {
        if (_samples[index] is { } done)
        {
            return done;
        }

        DmfSample s = _file.Samples[index];
        IEnumerable<short> raw = s.Data;
        if (s.CutStart is { } start && s.CutEnd is { } end && end > start)
        {
            raw = raw.Skip(start).Take(end - start);
        }

        int[] points = [.. raw.Select(v => s.Bits == 8 ? (v - 0x80) << 8 : v)];
        double gain = s.Amp / 50.0;
        double[] steps = [1 / 6.0, 1 / 5.0, 1 / 4.0, 1 / 3.0, 1 / 2.0, 1, 2, 3, 4, 5, 6];
        double step = steps[s.Pitch];
        int length = (int)Math.Floor(points.Length / step);
        sbyte[] data = new sbyte[length];
        for (int i = 0; i < length; i++)
        {
            int v = (int)Math.Clamp(points[Math.Min(points.Length - 1, (int)Math.Floor(i * step))] * gain, short.MinValue, short.MaxValue);
            data[i] = (sbyte)(v >> 8);
        }

        _samples[index] = data;
        return data;
    }

    /// <summary>The FM frequency (in hertz) of pitch <paramref name="pitch"/> (note + 12 × octave; C of octave n is 12n): DefleMask's F-number table.</summary>
    private static double FmNoteHz(int pitch)
    {
        int[] table = [644, 682, 723, 766, 811, 859, 910, 965, 1022, 1083, 1147, 1215];
        int semitone = ((pitch % 12) + 12) % 12;
        int block = (pitch - semitone) / 12;
        double fnum = table[semitone];
        if (block < 0)
        {
            fnum /= Math.Pow(2, -block);
            block = 0;
        }
        else if (block > 7)
        {
            fnum = Math.Min(0x7FF, fnum * Math.Pow(2, block - 7));
            block = 7;
        }

        return FmHz(fnum, block);
    }

    /// <summary>Hertz of F-number <paramref name="fnum"/> at <paramref name="block"/>.</summary>
    private static double FmHz(double fnum, int block) => fnum * YmClockHz / 144 / Math.Pow(2, 21 - block);

    /// <summary>Block and F-number for <paramref name="hz"/>: the lowest block whose F-number stays under the next C (1288), as DefleMask's slides move them.</summary>
    private static (int Block, int Fnum) FmRegisters(double hz)
    {
        double linear = hz * 144 * Math.Pow(2, 21) / YmClockHz;
        int block = 0;
        while (block < 7 && linear / Math.Pow(2, block) >= 1287.5)
        {
            block++;
        }

        int fnum = (int)Math.Round(linear / Math.Pow(2, block));
        return (block, Math.Clamp(fnum, 0, 0x7FF));
    }

    /// <summary>The PSG frequency of <paramref name="pitch"/>: two octaves above the FM note of that name (docs/formats/dmf.md#psg-notes-sound-two-octaves-up).</summary>
    private static double PsgNoteHz(int pitch) => 440 * 4 * Math.Pow(2, (pitch - 57) / 12.0);

    /// <summary>
    /// The PSG period for <paramref name="hz"/>, 8–1023, or 1 above that: DefleMask's VGM exports write period 1 for
    /// every note above A-7 (A#-7 would be 7), so it does too.
    /// </summary>
    private static int PsgPeriod(double hz)
    {
        int period = (int)Math.Round(PsgClockHz / 32 / hz);
        return period < 8 ? 1 : Math.Min(period, 0x3FF);
    }

    /// <summary>One pattern channel and the effects running on it.</summary>
    private abstract class Channel(DmfPlayer player)
    {
        private DmfCell? _pending;
        private int _pendingTick;
        private int _cutTick = -1;
        private int _retrigger;
        private int _rowTick;
        private int _volumeQuarters;
        private int _slideSetTick = -1;
        private bool _slideSetRow;

        protected DmfPlayer Player { get; } = player;

        /// <summary>The note playing (note + 12 × octave), or −1.</summary>
        protected int Pitch { get; set; } = -1;

        /// <summary>True between a note and its note-off.</summary>
        protected bool On { get; set; }

        /// <summary>The instrument number, or −1.</summary>
        protected int Instrument { get; set; } = -1;

        /// <summary>The channel volume (FM 0–7F, PSG 0–F).</summary>
        protected int Volume { get; set; }

        /// <summary>The pitch slides move (hertz), before arpeggio, vibrato and fine pitch.</summary>
        protected double BaseHz { get; set; }

        protected int ArpX { get; private set; }

        protected int ArpY { get; private set; }

        private int ArpTicks { get; set; }

        private int SlideSpeed { get; set; }

        private int PortaSpeed { get; set; }

        private double? PortaTargetHz { get; set; }

        private int VibratoSpeed { get; set; }

        private int VibratoDepth { get; set; }

        private int VibratoPosition { get; set; }

        private int TremoloSpeed { get; set; }

        private int TremoloDepth { get; set; }

        private int TremoloPosition { get; set; }

        private int VolumeSlide { get; set; }

        private double FineSemitones { get; set; }

        protected bool Legato { get; private set; }

        /// <summary>The loudest volume.</summary>
        protected abstract int MaxVolume { get; }

        /// <summary>A new row: its cell plays now, or on the tick its <c>EDxx</c> delays it to.</summary>
        public void StartRow(DmfCell cell, int ticks)
        {
            _retrigger = 0;
            _cutTick = -1;
            DmfEffect delay = cell.Effects.FirstOrDefault(e => e.Code == 0xED && e.Value > 0);
            _pending = cell;
            _pendingTick = delay.Code == 0xED ? delay.Value : 0;
            if (_pendingTick >= ticks)
            {
                _pending = null;
            }
        }

        /// <summary>One tick: the row's cell if it is due, then every running effect and macro, then the writes they need.</summary>
        public void Tick(int tick)
        {
            if (_pending is { } cell && tick == _pendingTick)
            {
                _pending = null;
                _rowTick = tick;
                PlayCell(cell);
            }

            if (_cutTick >= 0 && tick == _cutTick)
            {
                Cut();
            }

            if (_retrigger > 0 && tick > _rowTick && (tick - _rowTick) % _retrigger == 0 && Pitch >= 0)
            {
                NoteOn(Pitch, retrigger: true);
            }

            // A slide up starts on the tick after the one it is set on; a slide down on that tick (DefleMask's VGM exports).
            if (SlideSpeed < 0 || (SlideSpeed > 0 && !(_slideSetTick == tick && _slideSetRow)))
            {
                BaseHz = Slide(BaseHz, SlideSpeed);
            }

            if (PortaSpeed > 0 && PortaTargetHz is { } target && !(_slideSetTick == tick && _slideSetRow))
            {
                double next = Slide(BaseHz, BaseHz < target ? PortaSpeed : -PortaSpeed);
                BaseHz = (BaseHz < target && next >= target) || (BaseHz > target && next <= target) ? target : next;
            }

            _slideSetRow = false;
            if (VolumeSlide != 0)
            {
                // The volume is kept in quarter steps and rounded down, so a slide moves it on its first tick.
                _volumeQuarters = Math.Clamp(_volumeQuarters + VolumeSlide, 0, MaxVolume * 4);
                Volume = _volumeQuarters / 4;
            }

            Update();
            VibratoPosition += VibratoSpeed;
            TremoloPosition += TremoloSpeed;
            ArpTicks++;
        }

        /// <summary>The semitones arpeggio, vibrato and fine pitch add to <see cref="BaseHz"/> now.</summary>
        protected double PitchOffset()
        {
            int stage = ArpX != 0 || ArpY != 0 ? (ArpTicks / Math.Max(1, Player._arpSpeed)) % 3 : 0;
            double arp = stage switch { 1 => ArpX, 2 => ArpY, _ => 0 };
            double vibrato = VibratoDepth / 16.0 * Math.Sin(2 * Math.PI * VibratoPosition / 64.0);
            return arp + vibrato + FineSemitones;
        }

        /// <summary>The volume after tremolo: it only lowers, 0.65 dB per depth digit at the bottom of its swing (FM steps are 0.75 dB).</summary>
        protected int EffectiveVolume()
        {
            double lower = TremoloDepth == 0 ? 0 : TremoloDepth * 0.65 / 0.75 * (1 - Math.Cos(2 * Math.PI * TremoloPosition / 128.0)) / 2;
            return Math.Clamp((int)Math.Round(Volume - lower), 0, MaxVolume);
        }

        private void PlayCell(DmfCell cell)
        {
            bool portamento = false;
            foreach (DmfEffect e in cell.Effects.Where(e => e.Code >= 0))
            {
                int v = Math.Max(0, e.Value);
                switch (e.Code)
                {
                    case 0x00:
                        ArpX = v >> 4;
                        ArpY = v & 0x0F;
                        break;
                    case 0x01:
                        SlideSpeed = v;
                        PortaSpeed = 0;
                        MarkSlideSet();
                        break;
                    case 0x02:
                        SlideSpeed = -v;
                        PortaSpeed = 0;
                        MarkSlideSet();
                        break;
                    case 0x03:
                        PortaSpeed = v;
                        SlideSpeed = 0;
                        portamento = v > 0;
                        MarkSlideSet();
                        if (cell.Note == NoteOff || (cell.Note == 0 && cell.Octave == 0))
                        {
                            // On a row without a note DefleMask freezes the glide (docs/formats/dmf.md#effect-units).
                            PortaTargetHz = null;
                        }

                        break;
                    case 0x04:
                        VibratoSpeed = v >> 4;
                        VibratoDepth = v & 0x0F;
                        break;
                    case 0x05 or 0x06:
                        VolumeSlide = (v >> 4) != 0 ? v >> 4 : -(v & 0x0F);
                        break;
                    case 0x07:
                        TremoloSpeed = v >> 4;
                        TremoloDepth = v & 0x0F;
                        break;
                    case 0x0A:
                        VolumeSlide = (v >> 4) != 0 ? v >> 4 : -(v & 0x0F);
                        break;
                    case 0x0C:
                        _retrigger = v;
                        break;
                    case 0xE0:
                        // The arpeggio speed is the whole song's, whichever channel sets it.
                        Player._arpSpeed = Math.Max(1, v);
                        break;
                    case 0xE1 or 0xE2:
                        if (Pitch >= 0 && (v >> 4) > 0)
                        {
                            int to = Pitch + ((e.Code == 0xE1 ? 1 : -1) * (v & 0x0F));
                            PortaTargetHz = NoteHz(to);
                            PortaSpeed = v >> 4;
                            Pitch = to;
                        }

                        break;
                    case 0xE5:
                        FineSemitones = (v - 0x80) / 128.0;
                        break;
                    case 0xEA:
                        Legato = v != 0;
                        break;
                    case 0xEC:
                        _cutTick = _rowTick + v;
                        break;
                    case 0x09 or 0x0B or 0x0D or 0x0F or 0xED:
                        // Handled by the row: speeds, jumps, delay.
                        break;
                    default:
                        if (!Effect(e.Code, v))
                        {
                            Player.Unsupported($"effect {e.Code:X2} on {Name}");
                        }

                        break;
                }
            }

            if (cell.Instrument >= 0 && cell.Instrument != Instrument)
            {
                Instrument = cell.Instrument;
                InstrumentChanged();
            }

            if (cell.Volume >= 0)
            {
                Volume = cell.Volume;
                _volumeQuarters = Volume * 4;
            }

            if (cell.Note == NoteOff)
            {
                Release();
            }
            else if (cell.Note != 0 || cell.Octave != 0)
            {
                // Note 0 with an octave is a C there (the DMF reader takes such cells leniently); DefleMask plays it.
                int pitch = cell.Note + (12 * cell.Octave);
                if (portamento && On)
                {
                    PortaTargetHz = NoteHz(pitch);
                    Pitch = pitch;
                }
                else
                {
                    if (PortaSpeed > 0)
                    {
                        PortaTargetHz = null;
                    }

                    NoteOn(pitch, retrigger: false);
                }
            }
        }

        /// <summary>Notes that a slide or portamento was set on this tick.</summary>
        private void MarkSlideSet()
        {
            _slideSetTick = _rowTick;
            _slideSetRow = true;
        }

        /// <summary>Sets the channel volume and the quarter steps slides count in.</summary>
        protected void ResetVolume(int volume)
        {
            Volume = volume;
            _volumeQuarters = volume * 4;
        }

        /// <summary>A note starts (or starts again): every effect that runs from the note starts over.</summary>
        protected virtual void NoteOn(int pitch, bool retrigger)
        {
            Pitch = pitch;
            BaseHz = NoteHz(pitch);
            ArpTicks = 0;
            VibratoPosition = 0;
            On = true;
        }

        /// <summary>The note's frequency without effects.</summary>
        protected abstract double NoteHz(int pitch);

        /// <summary><paramref name="hz"/> moved by <paramref name="steps"/> of the channel's own slide unit (F-number or period steps).</summary>
        protected abstract double Slide(double hz, int steps);

        /// <summary>An effect only this kind of channel has; false when it isn't played.</summary>
        protected abstract bool Effect(int code, int value);

        /// <summary>The instrument column changed the instrument.</summary>
        protected abstract void InstrumentChanged();

        /// <summary>A note-off: the note releases.</summary>
        protected abstract void Release();

        /// <summary>A note cut (<c>ECxx</c>).</summary>
        protected virtual void Cut() => Release();

        /// <summary>Writes whatever the tick changed: frequency, volume, macros.</summary>
        protected abstract void Update();

        /// <summary>The channel's name in DefleMask, for <see cref="DmfPlayback.Unsupported"/>.</summary>
        protected abstract string Name { get; }
    }

    /// <summary>The FM state CH3's four operator channels share in extended mode: the key bits written to <c>$28</c>.</summary>
    private sealed class Ch3Keys
    {
        /// <summary>The operator bits (4–7) on.</summary>
        public int Mask { get; set; }

        /// <summary>The pan bits of <c>$B6</c>: CH3 has one pan register, so its operator channels share it.</summary>
        public int Pan { get; set; } = 0xC0;
    }

    /// <summary>What FM and CH3 operator channels share: a patch copy effects change, written as DefleMask writes it.</summary>
    private abstract class FmBase(DmfPlayer player) : Channel(player)
    {
        /// <summary>The Yamaha operator (1–4) each file operator (register slot +0, +4, +8, +C) is.</summary>
        protected static readonly int[] YamahaOperator = [1, 3, 2, 4];

        /// <summary>The detune register value for each stored DT (docs/formats/dmf.md#6-instruments).</summary>
        private static readonly int[] DetuneRegister = [7, 6, 5, 0, 1, 2, 3, 4];

        private bool _dirty = true;
        private int _writtenVolume = -1;
        private (int Block, int Fnum)? _writtenFrequency;

        /// <summary>The patch as effects have left it, operators in file order; <c>null</c> before an FM instrument.</summary>
        protected int[][]? Operators { get; private set; }

        protected int Algorithm { get; set; }

        protected int Feedback { get; set; }

        protected int Fms { get; private set; }

        protected int Ams { get; private set; }

        /// <summary>The pan bits of <c>$B4</c>+ch (L = 0x80, R = 0x40).</summary>
        protected virtual int Pan { get; set; } = 0xC0;

        protected override int MaxVolume => 0x7F;

        protected FmBase Init()
        {
            ResetVolume(MaxVolume);
            return this;
        }

        protected override double NoteHz(int pitch) => FmNoteHz(pitch);

        protected override double Slide(double hz, int steps)
        {
            (int block, int fnum) = FmRegisters(hz);
            int moved = fnum + steps;
            while (moved >= 1288 && block < 7)
            {
                moved /= 2;
                block++;
            }

            while (moved < 644 && block > 0)
            {
                moved *= 2;
                block--;
            }

            return FmHz(Math.Clamp(moved, 1, 0x7FF), block);
        }

        protected override void InstrumentChanged()
        {
            DmfFmInstrument? fm = Player._file.Instruments[Instrument].Fm;
            if (fm is null)
            {
                return;
            }

            Operators = [.. fm.Operators.Select(o => new[] { o.Am, o.Ar, o.Dr, o.Mult, o.Rr, o.Sl, o.Tl, o.Dt2, o.Rs, o.Dt, o.D2R, o.SsgMode })];
            Algorithm = fm.Algorithm;
            Feedback = fm.Feedback;
            Fms = fm.Fms;
            Ams = fm.Ams;
            _dirty = true;
        }

        /// <summary>True when the file operator <paramref name="j"/> is a carrier under the current algorithm.</summary>
        protected bool IsCarrier(int j) => YamahaOperator[j] switch
        {
            4 => true,
            2 => Algorithm >= 4,
            3 => Algorithm >= 5,
            _ => Algorithm == 7,
        };

        /// <summary>File operator <paramref name="j"/>'s total level with the channel volume: linear on 127 − TL for a carrier.</summary>
        protected int TotalLevel(int j, int volume)
        {
            int tl = Operators![j][6];
            // DefleMask floors the attenuation it adds, so TL never ends up above this (DMF sanity check).
            return IsCarrier(j) ? tl + ((127 - tl) * (127 - volume) / 127) : tl;
        }

        /// <summary>Writes the register values of file operator <paramref name="j"/> to slot offset <c>4j</c> of <paramref name="part"/>/<paramref name="ch"/>.</summary>
        protected void WriteOperator(int part, int ch, int j)
        {
            int[] o = Operators![j];
            int slot = (j * 4) + ch;
            Player.Fm(part, 0x30 + slot, (DetuneRegister[o[9] & 7] << 4) | (o[3] & 0x0F));
            Player.Fm(part, 0x40 + slot, TotalLevel(j, EffectiveVolume()));
            Player.Fm(part, 0x50 + slot, (o[8] << 6) | (o[1] & 0x1F));
            Player.Fm(part, 0x60 + slot, (o[0] != 0 ? 0x80 : 0) | (o[2] & 0x1F));
            Player.Fm(part, 0x70 + slot, o[10] & 0x1F);
            Player.Fm(part, 0x80 + slot, (o[5] << 4) | (o[4] & 0x0F));
            Player.Fm(part, 0x90 + slot, o[11] & 0x0F);
        }

        /// <summary>Takes the "patch must be written" flag: true once after each instrument change or patch effect.</summary>
        protected bool TakeDirty()
        {
            bool dirty = _dirty && Operators is not null;
            _dirty = false;
            return dirty;
        }

        protected void MarkDirty() => _dirty = true;

        /// <summary>The frequency registers to write now, or <c>null</c> when they haven't changed.</summary>
        protected (int Block, int Fnum)? FrequencyChange(bool force)
        {
            (int, int) registers = FmRegisters(BaseHz * Math.Pow(2, PitchOffset() / 12));
            if (!force && _writtenFrequency == registers)
            {
                return null;
            }

            _writtenFrequency = registers;
            return registers;
        }

        /// <summary>True when the effective volume changed since the last time it was written.</summary>
        protected bool VolumeChanged(bool force)
        {
            int volume = EffectiveVolume();
            bool changed = force || volume != _writtenVolume;
            _writtenVolume = volume;
            return changed;
        }

        /// <summary>Operator effects shared by FM and CH3 operator channels: <c>10</c>–<c>1D</c> (docs/formats/dmf.md#effects).</summary>
        protected bool OperatorEffect(int code, int value, int part, int ch, Func<int, bool> owns)
        {
            switch (code)
            {
                case 0x08:
                    Pan = ((value >> 4) != 0 ? 0x80 : 0) | ((value & 0x0F) != 0 ? 0x40 : 0);
                    WritePan(part, ch);
                    return true;
                case 0x10:
                    Player.Fm(0, 0x22, ((value >> 4) != 0 ? 0x08 : 0) | (value & 0x07));
                    return true;
                case 0x11:
                    Feedback = value & 7;
                    Player.Fm(part, 0xB0 + ch, (Feedback << 3) | Algorithm);
                    return true;
                case >= 0x12 and <= 0x15 when Operators is not null:
                    int tlOp = Array.IndexOf(YamahaOperator, code - 0x11);
                    Operators[tlOp][6] = value & 0x7F;
                    if (owns(tlOp))
                    {
                        Player.Fm(part, 0x40 + (tlOp * 4) + ch, TotalLevel(tlOp, EffectiveVolume()));
                    }

                    return true;
                case 0x16 when Operators is not null:
                    int multOp = Array.IndexOf(YamahaOperator, Math.Clamp(value >> 4, 1, 4));
                    Operators[multOp][3] = value & 0x0F;
                    if (owns(multOp))
                    {
                        Player.Fm(part, 0x30 + (multOp * 4) + ch, (DetuneRegister[Operators[multOp][9] & 7] << 4) | Operators[multOp][3]);
                    }

                    return true;
                case 0x19 when Operators is not null:
                    for (int j = 0; j < 4; j++)
                    {
                        Operators[j][1] = value & 0x1F;
                        if (owns(j))
                        {
                            Player.Fm(part, 0x50 + (j * 4) + ch, (Operators[j][8] << 6) | Operators[j][1]);
                        }
                    }

                    return true;
                case >= 0x1A and <= 0x1D when Operators is not null:
                    int arOp = Array.IndexOf(YamahaOperator, code - 0x19);
                    Operators[arOp][1] = value & 0x1F;
                    if (owns(arOp))
                    {
                        Player.Fm(part, 0x50 + (arOp * 4) + ch, (Operators[arOp][8] << 6) | Operators[arOp][1]);
                    }

                    return true;
                case >= 0x12 and <= 0x1D:
                    // An operator effect before any FM instrument: nothing to change yet.
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Writes pan, AMS and FMS (<c>$B4</c>+ch).</summary>
        protected void WritePan(int part, int ch) => Player.Fm(part, 0xB4 + ch, Pan | (Ams << 4) | Fms);
    }

    /// <summary>One of FM1–FM6; FM6 also plays DAC samples while <c>17 01</c> is on.</summary>
    private sealed class FmChannel : FmBase
    {
        private readonly int _part;
        private readonly int _ch;
        private readonly int _index;
        private bool _dac;
        private int _bank;
        private bool _dacPlaying;

        public FmChannel(DmfPlayer player, int index)
            : base(player)
        {
            _index = index;
            _part = index / 3;
            _ch = index % 3;
            Init();
        }

        protected override string Name => $"FM{_index + 1}";

        private int KeyChannel => (_part << 2) | _ch;

        protected override bool Effect(int code, int value)
        {
            switch (code)
            {
                case 0x17:
                    if (_index != 5)
                    {
                        return true;
                    }

                    if (value == 0 && _dacPlaying)
                    {
                        Player.Add(new ChipWrite(ChipTarget.PcmStop));
                        _dacPlaying = false;
                    }

                    _dac = value != 0;
                    Player.Fm(0, ChipLog.RegDacEnable, _dac ? 0x80 : 0x00);
                    return true;
                case 0xEB:
                    if (value <= 0x0B)
                    {
                        _bank = value;
                    }

                    return true;
                default:
                    return OperatorEffect(code, value, _part, _ch, _ => true);
            }
        }

        protected override void NoteOn(int pitch, bool retrigger)
        {
            bool sounding = On;
            base.NoteOn(pitch, retrigger);
            if (_dac)
            {
                int sample = (_bank * 12) + (((pitch % 12) + 12) % 12);
                if (sample < Player._file.Samples.Count)
                {
                    Player.Add(new ChipWrite(ChipTarget.PcmPlay, Sample: sample, RateHz: Player._file.Samples[sample].RateHz, Data: Player.SampleData(sample)));
                    _dacPlaying = true;
                }

                return;
            }

            WritePatch();
            if (Legato && sounding && !retrigger)
            {
                return;
            }

            Player.Fm(0, 0x28, KeyChannel);
            WriteFrequency(force: true);
            Player.Fm(0, 0x28, 0xF0 | KeyChannel);
        }

        protected override void Release()
        {
            On = false;
            if (_dac)
            {
                if (_dacPlaying)
                {
                    Player.Add(new ChipWrite(ChipTarget.PcmStop));
                    _dacPlaying = false;
                }

                return;
            }

            Player.Fm(0, 0x28, KeyChannel);
        }

        protected override void Update()
        {
            if (_dac || Operators is null || Pitch < 0)
            {
                return;
            }

            WriteFrequency(force: false);
            if (VolumeChanged(force: false))
            {
                for (int j = 0; j < 4; j++)
                {
                    if (IsCarrier(j))
                    {
                        Player.Fm(_part, 0x40 + (j * 4) + _ch, TotalLevel(j, EffectiveVolume()));
                    }
                }
            }
        }

        /// <summary>DefleMask writes a new instrument's patch on its row, note or not, and on FM6 with the DAC on too.</summary>
        protected override void InstrumentChanged()
        {
            base.InstrumentChanged();
            WritePatch();
        }

        /// <summary>Writes the whole patch when it changed: four operators, algorithm and feedback, pan.</summary>
        private void WritePatch()
        {
            if (TakeDirty())
            {
                for (int j = 0; j < 4; j++)
                {
                    WriteOperator(_part, _ch, j);
                }

                Player.Fm(_part, 0xB0 + _ch, (Feedback << 3) | Algorithm);
                WritePan(_part, _ch);
                VolumeChanged(force: true);
            }
        }

        private void WriteFrequency(bool force)
        {
            if (FrequencyChange(force) is { } f)
            {
                Player.Fm(_part, 0xA4 + _ch, (f.Block << 3) | (f.Fnum >> 8));
                Player.Fm(_part, 0xA0 + _ch, f.Fnum & 0xFF);
            }
        }
    }

    /// <summary>One of FM3's operator channels in extended mode: its own note, playing one operator of its instrument.</summary>
    private sealed class Ch3OpChannel : FmBase
    {
        /// <summary>The frequency registers (high, low) of OP1–OP4 in CH3 special mode (docs/formats/dmf.md#2-system; VGM exports).</summary>
        private static readonly (int High, int Low)[] FrequencyRegisters = [(0xAD, 0xA9), (0xAE, 0xAA), (0xAC, 0xA8), (0xA6, 0xA2)];

        private readonly int _op;
        private readonly int _file;

        public Ch3OpChannel(DmfPlayer player, int op)
            : base(player)
        {
            _op = op;
            _file = Array.IndexOf(YamahaOperator, op + 1);
            Init();
        }

        protected override string Name => $"FM3 OP{_op + 1}";

        private int Bit => 0x10 << _op;

        /// <summary>CH3's one pan register, shared by its four operator channels.</summary>
        protected override int Pan { get => Player._ch3.Pan; set => Player._ch3.Pan = value; }

        protected override bool Effect(int code, int value) => OperatorEffect(code, value, 0, 2, j => j == _file);

        /// <summary>DefleMask writes a new instrument's operator on its row, note or not.</summary>
        protected override void InstrumentChanged()
        {
            base.InstrumentChanged();
            WritePatch();
        }

        /// <summary>Writes this channel's operator of the patch when it changed, with CH3's algorithm, feedback and pan.</summary>
        private void WritePatch()
        {
            if (TakeDirty())
            {
                WriteOperator(0, 2, _file);
                Player.Fm(0, 0xB2, (Feedback << 3) | Algorithm);
                WritePan(0, 2);
                VolumeChanged(force: true);
            }
        }

        protected override void NoteOn(int pitch, bool retrigger)
        {
            bool sounding = On;
            base.NoteOn(pitch, retrigger);
            WritePatch();
            if (Legato && sounding && !retrigger)
            {
                return;
            }

            Ch3Keys keys = Player._ch3;
            keys.Mask &= ~Bit;
            Player.Fm(0, 0x28, keys.Mask | 2);
            WriteFrequency(force: true);
            keys.Mask |= Bit;
            Player.Fm(0, 0x28, keys.Mask | 2);
        }

        protected override void Release()
        {
            On = false;
            Ch3Keys keys = Player._ch3;
            keys.Mask &= ~Bit;
            Player.Fm(0, 0x28, keys.Mask | 2);
        }

        protected override void Update()
        {
            if (Operators is null || Pitch < 0)
            {
                return;
            }

            WriteFrequency(force: false);
            if (VolumeChanged(force: false) && IsCarrier(_file))
            {
                Player.Fm(0, 0x40 + (_file * 4) + 2, TotalLevel(_file, EffectiveVolume()));
            }
        }

        private void WriteFrequency(bool force)
        {
            if (FrequencyChange(force) is { } f)
            {
                (int high, int low) = FrequencyRegisters[_op];
                Player.Fm(0, high, (f.Block << 3) | (f.Fnum >> 8));
                Player.Fm(0, low, f.Fnum & 0xFF);
            }
        }
    }

    /// <summary>What the PSG's tone channels and the noise channel share: STD instrument macros and attenuation.</summary>
    private abstract class PsgBase(DmfPlayer player, int channel) : Channel(player)
    {
        private int _macroTick;
        private int _writtenAttenuation = -1;

        protected int PsgChannelIndex { get; } = channel;

        protected override int MaxVolume => 0x0F;

        protected override double NoteHz(int pitch) => PsgNoteHz(pitch);

        protected override double Slide(double hz, int steps) => PsgClockHz / 32 / Math.Clamp((PsgClockHz / 32 / hz) - steps, 1, 0x3FF);

        protected DmfStdInstrument? Std => Instrument >= 0 ? Player._file.Instruments[Instrument].Std : null;

        protected override void InstrumentChanged()
        {
            if (Std is { } std)
            {
                if (std.DutyNoise.Values.Count > 0)
                {
                    Player.Unsupported("duty/noise macro");
                }

                if (std.ArpeggioMode != 0 && std.Arpeggio.Values.Count > 0)
                {
                    Player.Unsupported("fixed arpeggio macro");
                }
            }
        }

        protected override void NoteOn(int pitch, bool retrigger)
        {
            base.NoteOn(pitch, retrigger);
            _macroTick = 0;
            _writtenAttenuation = -1;
        }

        protected override void Release()
        {
            On = false;
            WriteAttenuation(0x0F);
        }

        /// <summary>The value macro <paramref name="macro"/> has on the current tick, or <c>null</c> when it is empty.</summary>
        protected int? MacroValue(DmfMacro? macro)
        {
            if (macro is null || macro.Values.Count == 0)
            {
                return null;
            }

            int at = _macroTick;
            if (at >= macro.Values.Count)
            {
                at = macro.Loop >= 0 ? macro.Loop + ((at - macro.Loop) % (macro.Values.Count - macro.Loop)) : macro.Values.Count - 1;
            }

            return macro.Values[at];
        }

        /// <summary>The semitones the arpeggio macro adds now (values are stored offset by 12).</summary>
        protected int MacroArpeggio() => Std is { ArpeggioMode: 0 } std && MacroValue(std.Arpeggio) is { } v ? v - 12 : 0;

        /// <summary>The attenuation to write: 15 − (volume macro − (15 − channel volume)), silent when off.</summary>
        protected int Attenuation()
        {
            if (!On)
            {
                return 0x0F;
            }

            int macro = MacroValue(Std?.Volume) ?? 0x0F;
            // DefleMask subtracts: the macro value less what the channel volume is below F (VGM exports).
            return 0x0F - Math.Max(0, Math.Clamp(macro, 0, 0x0F) - (0x0F - EffectiveVolume()));
        }

        protected void WriteAttenuation(int attenuation)
        {
            if (attenuation != _writtenAttenuation)
            {
                Player.Psg(0x90 | (PsgChannelIndex << 5) | attenuation);
                _writtenAttenuation = attenuation;
            }
        }

        protected void AdvanceMacros() => _macroTick++;

        protected PsgBase Init()
        {
            ResetVolume(MaxVolume);
            return this;
        }
    }

    /// <summary>One of PSG1–PSG3.</summary>
    private sealed class PsgChannel : PsgBase
    {
        private int _writtenPeriod = -1;

        public PsgChannel(DmfPlayer player, int channel)
            : base(player, channel) => Init();

        protected override string Name => $"PSG{PsgChannelIndex + 1}";

        /// <summary>Pan and the FM-only effects do nothing on a PSG channel (the Genesis PSG is mono).</summary>
        protected override bool Effect(int code, int value) => code is 0x08 or 0x10 or 0x11;

        protected override void NoteOn(int pitch, bool retrigger)
        {
            base.NoteOn(pitch, retrigger);
            _writtenPeriod = -1;
        }

        protected override void Update()
        {
            if (Pitch < 0)
            {
                return;
            }

            int period = PsgPeriod(BaseHz * Math.Pow(2, (PitchOffset() + MacroArpeggio()) / 12));
            if (On && period != _writtenPeriod)
            {
                Player.Psg(0x80 | (PsgChannelIndex << 5) | (period & 0x0F));
                Player.Psg((period >> 4) & 0x3F);
                _writtenPeriod = period;
            }

            WriteAttenuation(Attenuation());
            AdvanceMacros();
        }
    }

    /// <summary>
    /// PSG4, the noise channel. <c>20xy</c> sets its mode (x ≠ 0: the rate comes from PSG3, whose tone the note then sets
    /// and whose volume goes off; y ≠ 0: white noise); DefleMask's export starts in white noise at PSG3's rate.
    /// </summary>
    private sealed class NoiseChannel : PsgBase
    {
        private bool _fromPsg3 = true;
        private bool _white = true;
        private int _writtenPeriod = -1;

        public NoiseChannel(DmfPlayer player)
            : base(player, 3) => Init();

        protected override string Name => "PSG4 (noise)";

        protected override bool Effect(int code, int value)
        {
            if (code == 0x20)
            {
                _fromPsg3 = (value >> 4) != 0;
                _white = (value & 0x0F) != 0;
                return true;
            }

            return code is 0x08 or 0x10;
        }

        protected override void NoteOn(int pitch, bool retrigger)
        {
            base.NoteOn(pitch, retrigger);
            _writtenPeriod = -1;
            if (_fromPsg3)
            {
                WritePsg3();
                Player.Psg(0xDF);
                Player.Psg(0xE0 | (_white ? 0x04 : 0) | 0x03);
            }
            else
            {
                // Fixed rates: C, C#, D (and every next three semitones) pick N/2048, N/1024, N/512, as DefleMask's VGM exports show.
                Player.Psg(0xE0 | (_white ? 0x04 : 0) | (2 - (((pitch % 3) + 3) % 3)));
            }
        }

        protected override void Update()
        {
            if (Pitch < 0)
            {
                return;
            }

            if (_fromPsg3 && On)
            {
                WritePsg3();
            }

            WriteAttenuation(Attenuation());
            AdvanceMacros();
        }

        private void WritePsg3()
        {
            int period = PsgPeriod(BaseHz * Math.Pow(2, (PitchOffset() + MacroArpeggio()) / 12));
            if (period != _writtenPeriod)
            {
                Player.Psg(0xC0 | (period & 0x0F));
                Player.Psg((period >> 4) & 0x3F);
                _writtenPeriod = period;
            }
        }
    }
}

/// <summary>What <see cref="DmfPlayer"/> played.</summary>
/// <param name="Log">The chip writes, one frame per tick.</param>
/// <param name="Unsupported">Effects and features the player met but doesn't play, by name.</param>
internal sealed record DmfPlayback(ChipLog Log, IReadOnlyList<string> Unsupported);
