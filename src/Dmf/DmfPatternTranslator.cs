using System.Globalization;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;

namespace Mod2Xgm.Dmf;

/// <summary>
/// Writes a <see cref="GenesisPlan"/>'s rows as DMF cells (plan T5.5, docs/adr/0044-dmf-effects-and-song-grid.md): notes,
/// volumes, instruments and every effect, one DMF pattern per order, parts kept as the MOD has them.
/// <para>DMF effects keep running on the following rows until they are stopped, MOD effects last one row; so the
/// translator remembers what each channel has running and stops it on the first row that doesn't repeat it. At a row a
/// jump lands on, the channel's state could have come from anywhere, so it is restated on first use, and effects that
/// were running at any jump to it are stopped. That needs a first pass to learn; <see cref="Translate"/> runs twice.</para>
/// <para>Per-tick amounts are scaled by the DMF ticks a row has over the MOD ticks it had, so a slide or vibrato covers
/// the same ground in the same time even where the DMF ticks faster (<see cref="DmfTimingPlan.TicksPerSourceTick"/>).</para>
/// </summary>
internal sealed class DmfPatternTranslator
{
    /// <summary>Most effect columns a DMF channel has (docs/formats/dmf.md#7-pattern-data).</summary>
    public const int MaxEffectColumns = 4;

    /// <summary>Samples the DAC can reach: 12 banks (<c>EBxx</c>) of 12 notes (docs/formats/dmf.md#dac-samples-on-fm6).</summary>
    public const int MaxDacNotes = 144;

    /// <summary>Notes per DAC bank.</summary>
    private const int BankSize = 12;

    /// <summary>Quieter copies of a DAC sample are made in steps of this many decibels (David, 2026-10-28).</summary>
    public const double DacVolumeStepDb = 3;

    /// <summary>A DAC note further than this from its sample's pitch gets a note: about a tenth of a semitone.</summary>
    private const double DacPitchToleranceCents = 10;

    /// <summary>Quieter than this many steps (45 dB) a DAC note is left silent.</summary>
    private const int MaxDacVolumeSteps = 15;

    /// <summary>Highest FM octave DefleMask plays.</summary>
    private const int MaxOctave = 7;

    /// <summary>Note value for "note off" (docs/formats/dmf.md#8-patterns).</summary>
    private const int NoteOff = 100;

    /// <summary>A-4 (440 Hz), counted in semitones from C-0.</summary>
    private const int A4 = 57;

    /// <summary>Concert pitch of A-4.</summary>
    private const double A4Hz = 440;

    /// <summary>Without a measured pitch, an FM note is placed this many semitones above the MOD note's name (ProTracker C-2 → C-4).</summary>
    private const int FallbackShift = 24;

    /// <summary>ProTracker 2 keeps periods within 113–856 (docs/formats/mod.md).</summary>
    private const double MinPeriod = 113;

    private const double MaxPeriod = 856;

    /// <summary>The octave DAC notes are written in: DefleMask ignores it (docs/formats/dmf.md#dac-samples-on-fm6).</summary>
    private const int DacOctave = 3;

    /// <summary>The octave PSG noise notes are written in: only the note within the octave picks the noise rate.</summary>
    private const int NoiseOctave = 3;

    /// <summary>Noise brighter than this uses the highest fixed noise rate (note D); see <see cref="NoiseNote"/>.</summary>
    private const double BrightNoise = 0.5;

    /// <summary>Noise brighter than this (but not <see cref="BrightNoise"/>) uses the middle rate (note C#); darker noise the lowest (C).</summary>
    private const double MiddleNoise = 0.25;

    /// <summary>Pan beyond this from the centre plays on one side only: the Genesis has left, right or both, nothing between.</summary>
    private const double PanSide = 1.0 / 3;

    // Effect codes (docs/formats/dmf.md#effects).
    private const int ArpeggioCode = 0x00;
    private const int SlideUp = 0x01;
    private const int SlideDown = 0x02;
    private const int Portamento = 0x03;
    private const int VibratoCode = 0x04;
    private const int TremoloCode = 0x07;
    private const int Panning = 0x08;
    private const int VolumeSlideCode = 0x0A;
    private const int JumpToOrder = 0x0B;
    private const int RetriggerCode = 0x0C;
    private const int NextOrder = 0x0D;
    private const int ArpeggioSpeed = 0xE0;
    private const int VibratoShape = 0xE3;
    private const int FinePitch = 0xE5;
    private const int DacBank = 0xEB;
    private const int NoteCutCode = 0xEC;
    private const int NoteDelayCode = 0xED;
    private const int DacModeCode = 0x17;
    private const int NoiseModeCode = 0x20;

    /// <summary><c>20xy</c> value for fixed-rate white noise (docs/formats/dmf.md#effects).</summary>
    private const int WhiteFixedNoise = 0x01;

    /// <summary>Pan values for <c>08xy</c>.</summary>
    private const int PanLeft = 0x10;

    private const int PanRight = 0x01;
    private const int PanBoth = 0x11;

    // Priorities: when a cell needs more than four effects, the lowest numbers stay.
    private const int KeepFirst = 0;
    private const int PitchPriority = 1;
    private const int LevelPriority = 2;
    private const int TimingPriority = 3;
    private const int DetailPriority = 4;
    private const int StopPriority = 5;

    /// <summary>Flow effects go to the channel with room, PSG tone channels first: the simple mapper leaves them unused.</summary>
    private static readonly int[] FlowChannels = [6, 7, 8, 9, 0, 1, 2, 3, 4, 5];

    private readonly GenesisPlan _plan;
    private readonly Song _song;
    private readonly DmfTimingPlan _timing;
    private readonly double _clockHz;
    private readonly int _orders;
    private readonly int _rows;
    private readonly int _loopTarget;
    private readonly IReadOnlyDictionary<int, int> _noiseInstruments;
    private readonly Dictionary<int, SamplePitch> _pitches = [];
    private readonly Dictionary<int, int> _noiseNotes = [];

    /// <summary>Prepares a translation of the first <paramref name="orders"/> orders of <paramref name="plan"/>.</summary>
    /// <param name="plan">The mapped song.</param>
    /// <param name="song">The song the plan was made from: samples (pitch, noise colour), instruments (finetune) and channel pans.</param>
    /// <param name="timing">Tick rate and tick-time changes (plan T5.4).</param>
    /// <param name="clockHz">The source Amiga's Paula clock, which turns playback rates into periods.</param>
    /// <param name="orders">Orders to write (DMF holds at most 127).</param>
    /// <param name="noiseInstruments">For each sample played on the PSG noise channel, its instrument number.</param>
    public DmfPatternTranslator(GenesisPlan plan, Song song, DmfTimingPlan timing, double clockHz, int orders, IReadOnlyDictionary<int, int> noiseInstruments)
    {
        _plan = plan;
        _song = song;
        _timing = timing;
        _clockHz = clockHz;
        _orders = orders;
        _rows = plan.Patterns.Take(orders).Select(p => p.Count).DefaultIfEmpty(1).Max();
        _loopTarget = plan.LoopOrder is { } loop && loop < orders ? loop : 0;
        _noiseInstruments = noiseInstruments;
    }

    /// <summary>Rows in every DMF pattern: the longest of the plan's.</summary>
    public int RowsPerPattern => _rows;

    /// <summary>Translates the plan: a first pass learns what is running at each jump, the second writes the cells.</summary>
    public DmfTranslation Translate()
    {
        Pass first = new(this, new Dictionary<(int Order, int Row), HashSet<(int Channel, int Code)>>());
        first.Run();
        Pass second = new(this, first.RunningAtJumps);
        return second.Run();
    }

    /// <summary>Where play goes after row <paramref name="row"/> of order <paramref name="order"/> other than the next row, or <c>null</c>.</summary>
    private (int Order, int Row)? JumpAfter(int order, int row)
    {
        IReadOnlyList<Effect> flow = PlanRowAt(order, row).Flow;
        PositionJump? jump = flow.OfType<PositionJump>().FirstOrDefault();
        PatternBreak? patternBreak = flow.OfType<PatternBreak>().FirstOrDefault();
        if (flow.OfType<StopSong>().Any())
        {
            return (_loopTarget, 0);
        }

        if (jump is not null)
        {
            return (jump.Order < _orders ? jump.Order : 0, 0);
        }

        if (patternBreak is not null)
        {
            return order + 1 < _orders ? (order + 1, patternBreak.Row < _rows ? patternBreak.Row : 0) : (_loopTarget, 0);
        }

        return order == _orders - 1 && row == _rows - 1 ? (_loopTarget, 0) : null;
    }

    private PlanRow PlanRowAt(int order, int row)
    {
        IReadOnlyList<PlanRow> rows = _plan.Patterns[order];
        return row < rows.Count ? rows[row] : EmptyRow;
    }

    private static PlanRow EmptyRow { get; } = new([.. Enumerable.Repeat(PlanCell.Empty, GenesisPlan.ChannelCount)], []);

    /// <summary>The pitch an FM note sounds at, in semitones from C-0 with a fraction; <paramref name="measured"/> says how it was found.</summary>
    private double HeardSemitones(NotePitch note, int sample, out bool measured)
    {
        if (!_pitches.TryGetValue(sample, out SamplePitch pitch))
        {
            pitch = SamplePitch.Measure(_song.Samples[sample]);
            _pitches[sample] = pitch;
        }

        if (pitch.FrequencyAt(note.PlaybackRateHz) is { } hz)
        {
            measured = true;
            return A4 + (12 * Math.Log2(hz / A4Hz));
        }

        measured = false;
        return note.Semitone + FallbackShift + (note.Cents / 100);
    }

    /// <summary>The note that picks a fixed noise rate for <paramref name="sample"/>: D (fastest) for bright noise, C# for middling, C (slowest) for dark.</summary>
    private int NoiseNote(int sample)
    {
        if (!_noiseNotes.TryGetValue(sample, out int note))
        {
            double brightness = SampleTraits.Analyze(_song.Samples[sample]).Brightness;
            note = brightness > BrightNoise ? 2 : brightness > MiddleNoise ? 1 : 0;
            _noiseNotes[sample] = note;
        }

        return note;
    }

    /// <summary>The <c>08xy</c> value for a pan of −1 (left) to +1 (right).</summary>
    private static int PanCode(double pan) => pan < -PanSide ? PanLeft : pan > PanSide ? PanRight : PanBoth;

    /// <summary>A note's semitone as a DMF note and octave: C is stored as note 12 of the octave below (docs/formats/dmf.md#8-patterns).</summary>
    private static (int Note, int Octave) NoteAndOctave(int semitone)
    {
        int octave = Math.DivRem(semitone, 12, out int note);
        return note == 0 ? (12, octave - 1) : (note, octave);
    }

    /// <summary>One run over the rows.</summary>
    private sealed class Pass
    {
        private readonly DmfPatternTranslator _t;
        private readonly IReadOnlyDictionary<(int Order, int Row), HashSet<(int Channel, int Code)>> _stopsAt;
        private readonly HashSet<(int Order, int Row)> _destinations = [];
        private readonly Track[] _tracks = [.. Enumerable.Range(0, GenesisPlan.ChannelCount).Select(_ => new Track())];
        private readonly Memory[] _memory;
        private readonly double?[] _panOverride;
        private readonly List<DacVariant> _variants = [];
        private readonly Dictionary<DacVariant, int> _variantIndex = [];
        private readonly List<double> _variantSeconds = [];
        private readonly Dictionary<string, int> _tallies = [];
        private readonly SortedDictionary<int, bool> _fmSamples = [];
        private readonly DmfCell[][][] _cells;

        private TickTimes _pair;
        private int _speed;
        private double _seconds;
        private bool _restorePair;

        public Pass(DmfPatternTranslator translator, IReadOnlyDictionary<(int Order, int Row), HashSet<(int Channel, int Code)>> stopsAt)
        {
            _t = translator;
            _stopsAt = stopsAt;
            _memory = [.. translator._song.Channels.Select(_ => new Memory())];
            _panOverride = new double?[translator._song.Channels.Count];
            _cells = [.. Enumerable.Range(0, GenesisPlan.ChannelCount).Select(_ => new DmfCell[translator._orders][])];
            _pair = new TickTimes(translator._timing.Timing.TickTime1, translator._timing.Timing.TickTime2);
            _speed = translator._plan.Timing.Speed;
        }

        /// <summary>For each row a jump lands on, the effects that were running on each channel when some jump left for it.</summary>
        public Dictionary<(int Order, int Row), HashSet<(int Channel, int Code)>> RunningAtJumps { get; } = [];

        public DmfTranslation Run()
        {
            _destinations.Add((0, 0));
            for (int p = 0; p < _t._orders; p++)
            {
                for (int r = 0; r < _t._rows; r++)
                {
                    if (_t.JumpAfter(p, r) is { } target)
                    {
                        _destinations.Add(target);
                    }
                }
            }

            for (int p = 0; p < _t._orders; p++)
            {
                for (int channel = 0; channel < GenesisPlan.ChannelCount; channel++)
                {
                    _cells[channel][p] = new DmfCell[_t._rows];
                }

                for (int r = 0; r < _t._rows; r++)
                {
                    TranslateRow(p, r);
                }
            }

            CloseDac();
            return new DmfTranslation(_cells, _variants, _variantSeconds, _tallies, _fmSamples);
        }

        private void TranslateRow(int order, int row)
        {
            PlanRow planRow = _t.PlanRowAt(order, row);
            List<Planned> flow = [];
            if (_t._timing.Changes.Count > order && _t._timing.Changes[order].Count > row && _t._timing.Changes[order][row] is { } change)
            {
                _pair = change;
                flow.AddRange(PairEffects(change));
                _restorePair = false;
            }
            else if (_restorePair)
            {
                flow.AddRange(PairEffects(_pair));
                _restorePair = false;
            }

            foreach (SetSpeed speed in planRow.Flow.OfType<SetSpeed>().Where(s => s.TicksPerRow > 0))
            {
                _speed = speed.TicksPerRow;
            }

            int baseTicks = row % 2 == 0 ? _pair.First : _pair.Second;
            RowClock context = new(baseTicks, _speed, (double)baseTicks / _speed);
            TranslateFlow(order, row, planRow, flow, baseTicks);

            bool landing = _destinations.Contains((order, row));
            List<Planned>[] effects = new List<Planned>[GenesisPlan.ChannelCount];
            DmfCell[] cells = new DmfCell[GenesisPlan.ChannelCount];
            for (int channel = 0; channel < GenesisPlan.ChannelCount; channel++)
            {
                Track track = _tracks[channel];
                if (landing)
                {
                    track.Forget();
                    if (_stopsAt.TryGetValue((order, row), out HashSet<(int Channel, int Code)>? stops))
                    {
                        foreach ((int c, int code) in stops.Where(s => s.Channel == channel))
                        {
                            track.Running[Group(code)] = code;
                        }
                    }
                }

                effects[channel] = [];
                cells[channel] = TranslateCell(channel, planRow.Cells[channel], context, effects[channel]);
            }

            PlaceFlow(flow, effects);
            for (int channel = 0; channel < GenesisPlan.ChannelCount; channel++)
            {
                List<Planned> kept = [.. effects[channel].OrderBy(e => e.Priority).ThenBy(e => e.Order)];
                if (kept.Count > MaxEffectColumns)
                {
                    Tally("{Count} effects were left out because a cell had more than four (DMF's limit); the least important went", kept.Count - MaxEffectColumns);
                    kept = kept[..MaxEffectColumns];
                }

                _cells[channel][order][row] = cells[channel] with { Effects = [.. kept.OrderBy(e => e.Order).Select(e => e.Effect)] };
            }

            if (_t.JumpAfter(order, row) is { } target)
            {
                if (!RunningAtJumps.TryGetValue(target, out HashSet<(int Channel, int Code)>? running))
                {
                    running = [];
                    RunningAtJumps[target] = running;
                }

                for (int channel = 0; channel < GenesisPlan.ChannelCount; channel++)
                {
                    running.UnionWith(_tracks[channel].Running.Values.Select(code => (channel, code)));
                }
            }

            _seconds += planRow.Flow.OfType<PatternDelay>().Select(d => baseTicks * (1 + d.Rows)).DefaultIfEmpty(baseTicks).First() / (double)_t._timing.Timing.TickRateHz;
        }

        /// <summary>Jumps, breaks, stops, pattern delays and the song's end; tick-time changes are already in <paramref name="flow"/>.</summary>
        private void TranslateFlow(int order, int row, PlanRow planRow, List<Planned> flow, int baseTicks)
        {
            if (order == 0 && row == 0 && _t._timing.TicksPerSourceTick > 1)
            {
                // Arpeggio steps once per tick; the DMF ticks faster, so each step lasts that many ticks.
                flow.Add(new Planned(KeepFirst, new DmfEffect(ArpeggioSpeed, Math.Min(byte.MaxValue, _t._timing.TicksPerSourceTick))));
            }

            if (planRow.Flow.OfType<PatternDelay>().FirstOrDefault() is { } delay)
            {
                int stretched = baseTicks * (1 + delay.Rows);
                if (stretched > byte.MaxValue)
                {
                    Tally("{Count} pattern delays (EEx) were shortened: a DMF row holds at most 255 ticks");
                }

                int ticks = Math.Min(byte.MaxValue, stretched);
                flow.Add(new Planned(KeepFirst, new DmfEffect(DmfTimingPlanner.SpeedEffect1, ticks)));
                flow.Add(new Planned(KeepFirst, new DmfEffect(DmfTimingPlanner.SpeedEffect2, ticks)));
                _restorePair = true;
            }

            if (planRow.Flow.OfType<PatternLoop>().Any())
            {
                Tally("{Count} pattern loops (E6x) play their rows once: DMF has no pattern loop, and the parts are kept as written");
            }

            PositionJump? jump = planRow.Flow.OfType<PositionJump>().FirstOrDefault();
            PatternBreak? patternBreak = planRow.Flow.OfType<PatternBreak>().FirstOrDefault();
            bool last = order == _t._orders - 1;
            if (planRow.Flow.OfType<StopSong>().Any())
            {
                Tally("{Count} song stops (F00) became jumps to the loop start: DefleMask always loops");
                flow.Add(new Planned(KeepFirst, new DmfEffect(JumpToOrder, _t._loopTarget)));
            }
            else if (jump is not null)
            {
                if (jump.Order >= _t._orders)
                {
                    Tally("{Count} position jumps (Bxx) went past the song's end; they go to order 0, as ProTracker does");
                }

                if (patternBreak is { Row: > 0 })
                {
                    Tally("{Count} jumps to a row of another order (Bxx with Dxx) start that order at row 0: DMF can't do both in one row");
                }

                flow.Add(new Planned(KeepFirst, new DmfEffect(JumpToOrder, jump.Order < _t._orders ? jump.Order : 0)));
            }
            else if (patternBreak is not null)
            {
                if (!last)
                {
                    flow.Add(new Planned(KeepFirst, new DmfEffect(NextOrder, patternBreak.Row < _t._rows ? patternBreak.Row : 0)));
                }
                else
                {
                    // DefleMask ignores 0Dxx on the last order (docs/formats/dmf.md#effects); jump to the loop instead.
                    if (patternBreak.Row > 0)
                    {
                        Tally("{Count} pattern breaks (Dxx) on the last order to a row other than 0 start the loop at row 0: DefleMask ignores 0Dxx there");
                    }

                    flow.Add(new Planned(KeepFirst, new DmfEffect(JumpToOrder, _t._loopTarget)));
                }
            }
            else if (last && row == _t._rows - 1 && _t._loopTarget != 0)
            {
                flow.Add(new Planned(KeepFirst, new DmfEffect(JumpToOrder, _t._loopTarget)));
            }
        }

        private static IEnumerable<Planned> PairEffects(TickTimes pair) =>
            pair.Effects().Select(e => new Planned(KeepFirst, e));

        /// <summary>Puts song-wide effects in the first channels with a free column, PSG tone channels first.</summary>
        private void PlaceFlow(List<Planned> flow, List<Planned>[] effects)
        {
            foreach (Planned planned in flow)
            {
                int channel = FlowChannels.FirstOrDefault(c => effects[c].Count < MaxEffectColumns, -1);
                if (channel < 0)
                {
                    Tally("{Count} song-wide effects were left out: every channel's four effect columns were full");
                    continue;
                }

                effects[channel].Add(planned with { Order = effects[channel].Count });
            }
        }

        private DmfCell TranslateCell(int channel, PlanCell cell, RowClock row, List<Planned> effects)
        {
            Track track = _tracks[channel];
            bool psg = channel >= (int)GenesisChannel.Psg1;
            Draft draft = new();
            HashSet<int> issued = [];
            int? source = cell.Source;
            Memory? memory = source is { } s && s < _memory.Length ? _memory[s] : null;

            if (cell.NoteOff && cell.Note is null)
            {
                draft.Note = NoteOff;
                EndSound(channel, track);
            }

            TonePortamento? porta = cell.Effects.OfType<TonePortamento>().FirstOrDefault();
            if (cell.Note is { } note && cell.Voice is { } voice && cell.Sample is { } sample)
            {
                switch (voice.Kind)
                {
                    case VoiceKind.Dac:
                        StartDac(channel, cell, note, voice, sample, memory, track, draft, effects);
                        break;
                    case VoiceKind.PsgNoise:
                        StartNoise(cell, sample, track, draft, effects);
                        break;
                    default:
                        StartFm(channel, cell, note, voice, sample, porta, track, draft, effects);
                        break;
                }

                if (source is { } src)
                {
                    track.Source = src;
                }
            }
            else if (cell.Volume is { } volume && track.Sounding && track.Kind is VoiceKind.Fm or VoiceKind.PsgNoise)
            {
                track.ModVolume = volume;
                draft.Volume = track.Kind == VoiceKind.PsgNoise ? DmfUnits.PsgVolume(volume) : DmfUnits.FmVolume(volume);
                track.DmfVolume = draft.Volume;
            }

            if (track.Sounding)
            {
                Pan(channel, track, effects);
                foreach (Effect effect in cell.Effects)
                {
                    Translate(effect, channel, psg, track, memory, row, draft, effects, issued, cell.Note is not null);
                }
            }
            else if (cell.Effects.Count > 0)
            {
                Tally("{Count} effects on rows where their channel plays nothing were left out", cell.Effects.Count);
            }

            foreach ((int group, int code) in track.Running.Where(r => !issued.Contains(r.Key)).ToList())
            {
                effects.Add(new Planned(StopPriority, new DmfEffect(code, 0), effects.Count));
                track.Running.Remove(group);
            }

            Resync(track, draft, issued);
            return new DmfCell(draft.Note, draft.Octave, draft.Volume, [], draft.Instrument);
        }

        /// <summary>Restores the MOD's volume where a slide ended off it, or after a jump the volume is unknown.</summary>
        private static void Resync(Track track, Draft draft, HashSet<int> issued)
        {
            if (!track.Sounding || draft.Volume >= 0 || issued.Contains(VolumeSlideCode) || track.Kind is not (VoiceKind.Fm or VoiceKind.PsgNoise))
            {
                return;
            }

            bool psg = track.Kind == VoiceKind.PsgNoise;
            double exact = DmfUnits.ExactVolume(track.ModVolume, psg);
            if (track.DmfVolume is not { } dmf || Math.Abs(dmf - exact) >= 1)
            {
                draft.Volume = psg ? DmfUnits.PsgVolume(track.ModVolume) : DmfUnits.FmVolume(track.ModVolume);
                track.DmfVolume = draft.Volume;
            }
        }

        private void StartFm(int channel, PlanCell cell, NotePitch note, PlanVoice voice, int sample, TonePortamento? porta, Track track, Draft draft, List<Planned> effects)
        {
            if (channel == (int)GenesisChannel.Fm6 && track.DacMode != 0)
            {
                effects.Add(new Planned(KeepFirst, new DmfEffect(DacModeCode, 0), effects.Count));
                track.DacMode = 0;
            }

            CloseDacOn(channel, track);
            double semitones = _t.HeardSemitones(note, sample, out bool measured);
            _fmSamples.TryAdd(sample, measured);
            if (cell.Effects.OfType<SetFineTune>().FirstOrDefault() is { } fineTune)
            {
                double instrumentCents = _t._song.Instruments.FirstOrDefault(i => i.Sample == sample)?.FineTuneCents ?? 0;
                semitones += (fineTune.Cents - instrumentCents) / 100;
            }

            int whole = (int)Math.Round(semitones, MidpointRounding.AwayFromZero);
            int shifted = whole;
            while (shifted < 0)
            {
                shifted += 12;
            }

            while (shifted > (MaxOctave * 12) + 11)
            {
                shifted -= 12;
            }

            if (shifted != whole)
            {
                Tally("{Count} FM notes were outside DefleMask's octaves 0–7 and were moved by whole octaves");
            }

            int fine = DmfUnits.FinePitchCentre + (int)Math.Round((semitones - whole) * DmfUnits.FinePitchStepsPerSemitone, MidpointRounding.AwayFromZero);
            bool glide = porta is not null && track.Sounding && track.Kind == VoiceKind.Fm;
            (draft.Note, draft.Octave) = NoteAndOctave(shifted);

            // An instrument on a portamento row would restart the note in DefleMask (Furnace: newInsTriggersInPorta).
            draft.Instrument = glide ? -1 : voice.Index;
            double volume = cell.Volume ?? track.ModVolume;
            draft.Volume = DmfUnits.FmVolume(volume);
            if (fine != track.Fine)
            {
                effects.Add(new Planned(PitchPriority, new DmfEffect(FinePitch, Math.Clamp(fine, 0, byte.MaxValue)), effects.Count));
                track.Fine = fine;
            }

            // DefleMask slides from the written note's F-number; E5xx shifts the pitch on top without moving it, so the
            // slide speeds are worked out from the whole note (Furnace: baseFreq versus pitch).
            double targetPeriod = Math.Clamp(_t._clockHz / note.PlaybackRateHz, MinPeriod, MaxPeriod);
            if (glide)
            {
                track.TargetPeriod = targetPeriod;
                track.TargetSemitones = shifted;
            }
            else
            {
                track.Period = targetPeriod;
                track.Semitones = shifted;
                track.TargetPeriod = null;
            }

            track.Kind = VoiceKind.Fm;
            track.Sounding = true;
            track.ModVolume = volume;
            track.DmfVolume = draft.Volume;
        }

        private void StartNoise(PlanCell cell, int sample, Track track, Draft draft, List<Planned> effects)
        {
            if (track.NoiseMode != WhiteFixedNoise)
            {
                effects.Add(new Planned(KeepFirst, new DmfEffect(NoiseModeCode, WhiteFixedNoise), effects.Count));
                track.NoiseMode = WhiteFixedNoise;
            }

            int note = _t.NoiseNote(sample);
            (draft.Note, draft.Octave) = NoteAndOctave((NoiseOctave * 12) + note);
            draft.Instrument = _t._noiseInstruments[sample];
            double volume = cell.Volume ?? track.ModVolume;
            draft.Volume = DmfUnits.PsgVolume(volume);
            track.Kind = VoiceKind.PsgNoise;
            track.Sounding = true;
            track.ModVolume = volume;
            track.DmfVolume = draft.Volume;
        }

        private void StartDac(int channel, PlanCell cell, NotePitch note, PlanVoice voice, int sample, Memory? memory, Track track, Draft draft, List<Planned> effects)
        {
            CloseDacOn(channel, track);
            int offset = 0;
            if (cell.Effects.OfType<SampleOffset>().FirstOrDefault() is { } sampleOffset)
            {
                offset = sampleOffset.Frame ?? memory?.Offset ?? 0;
                if (memory is not null)
                {
                    memory.Offset = offset;
                }
            }

            double volume = cell.Volume ?? track.ModVolume;
            int steps = volume <= 0 ? int.MaxValue : (int)Math.Round(-20 * Math.Log10(volume) / DacVolumeStepDb, MidpointRounding.AwayFromZero);
            int length = _t._song.Samples[sample].Length;
            if (offset >= length)
            {
                Tally("{Count} DAC notes start past their sample's end (9xx); left silent, as ProTracker plays nothing");
                SilenceDac(track, draft);
                return;
            }

            if (steps > MaxDacVolumeSteps)
            {
                Tally("{Count} DAC notes are quieter than −45 dB; left silent");
                SilenceDac(track, draft);
                return;
            }

            int? index = Variant(new DacVariant(voice.Index, offset, Math.Max(0, steps)));
            if (index is not { } k)
            {
                SilenceDac(track, draft);
                return;
            }

            if (Math.Abs(Math.Log2(note.PlaybackRateHz / _t._plan.DacSamples[voice.Index].PlaybackRateHz)) * 1200 > DacPitchToleranceCents)
            {
                Tally("{Count} DAC notes play at their sample's most-used pitch, not their own: the simple mapper gives each DAC sample one pitch (ADR 0040)");
            }

            if (track.DacMode != 1)
            {
                effects.Add(new Planned(KeepFirst, new DmfEffect(DacModeCode, 1), effects.Count));
                track.DacMode = 1;
            }

            int bank = k / BankSize;
            if (bank != track.Bank)
            {
                effects.Add(new Planned(KeepFirst, new DmfEffect(DacBank, bank), effects.Count));
                track.Bank = bank;
            }

            (draft.Note, draft.Octave) = NoteAndOctave((DacOctave * 12) + (k % BankSize));
            track.Kind = VoiceKind.Dac;
            track.Sounding = true;
            track.ModVolume = volume;
            track.DacVariant = k;
            track.DacStart = _seconds;
        }

        private void SilenceDac(Track track, Draft draft)
        {
            draft.Note = NoteOff;
            track.Sounding = false;
            track.Running.Clear();
        }

        /// <summary>The DAC note of a sample copy, made on first use; past 144, the nearest copy of the same sample, or <c>null</c>.</summary>
        private int? Variant(DacVariant wanted)
        {
            if (_variantIndex.TryGetValue(wanted, out int index))
            {
                return index;
            }

            if (_variants.Count < MaxDacNotes)
            {
                _variantIndex[wanted] = _variants.Count;
                _variants.Add(wanted);
                _variantSeconds.Add(0);
                return _variants.Count - 1;
            }

            int nearest = -1;
            int bestCost = int.MaxValue;
            for (int i = 0; i < _variants.Count; i++)
            {
                DacVariant have = _variants[i];
                if (have.Dac != wanted.Dac)
                {
                    continue;
                }

                int cost = Math.Abs(have.VolumeSteps - wanted.VolumeSteps) + (have.Offset == wanted.Offset ? 0 : 100);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    nearest = i;
                }
            }

            if (nearest < 0)
            {
                Tally("{Count} DAC notes were left out: all 144 DAC notes (12 banks × 12) were taken");
                return null;
            }

            Tally("{Count} DAC notes play the nearest existing copy of their sample: all 144 DAC notes (12 banks × 12) were taken");
            return nearest;
        }

        private void CloseDacOn(int channel, Track track)
        {
            if (channel == (int)GenesisChannel.Fm6 && track.DacVariant is { } k)
            {
                _variantSeconds[k] = Math.Max(_variantSeconds[k], _seconds - track.DacStart);
                track.DacVariant = null;
            }
        }

        private void CloseDac() => CloseDacOn((int)GenesisChannel.Fm6, _tracks[(int)GenesisChannel.Fm6]);

        private void EndSound(int channel, Track track)
        {
            CloseDacOn(channel, track);
            track.Sounding = false;

            // A note off stops DefleMask's slides itself (Furnace: noteOffResetsSlides).
            track.Running.Remove(Group(SlideUp));
        }

        /// <summary>Restates the pan when the note's MOD channel wants another side (FM channels only; the PSG is mono).</summary>
        private void Pan(int channel, Track track, List<Planned> effects)
        {
            if (channel > (int)GenesisChannel.Fm6 || track.Source < 0 || track.Source >= _t._song.Channels.Count)
            {
                return;
            }

            int code = PanCode(_panOverride[track.Source] ?? _t._song.Channels[track.Source].Pan);
            if (code != track.Pan)
            {
                effects.Add(new Planned(KeepFirst, new DmfEffect(Panning, code), effects.Count));
                track.Pan = code;
            }
        }

        private void Translate(Effect effect, int channel, bool psg, Track track, Memory? memory, RowClock row, Draft draft, List<Planned> effects, HashSet<int> issued, bool hasNote)
        {
            bool fm = track.Kind == VoiceKind.Fm;
            bool dac = track.Kind == VoiceKind.Dac;
            switch (effect)
            {
                case Model.Arpeggio arpeggio when fm:
                    Issue(new DmfEffect(ArpeggioCode, (Math.Min(arpeggio.FirstSemitones, 15) << 4) | Math.Min(arpeggio.SecondSemitones, 15)), PitchPriority, track, effects, issued);
                    break;
                case PitchSlide slide when fm:
                    PitchSlideOf(slide, memory, row, track, effects, issued);
                    break;
                case TonePortamento porta when fm:
                    PortamentoOf(porta, memory, row, track, effects, issued, hasNote);
                    break;
                case Vibrato vibrato when fm:
                    VibratoOf(vibrato, memory, row, track, effects, issued);
                    break;
                case Tremolo tremolo when !dac:
                    TremoloOf(tremolo, memory, row, psg, track, effects, issued);
                    break;
                case VolumeSlide slide when !dac:
                    VolumeSlideOf(slide, memory, row, psg, track, draft, effects, issued);
                    break;
                case Retrigger retrigger:
                    effects.Add(new Planned(TimingPriority, new DmfEffect(RetriggerCode, DmfUnits.ByteValue(retrigger.IntervalTicks * row.Ratio)), effects.Count));
                    break;
                case NoteCut cut:
                    effects.Add(new Planned(TimingPriority, new DmfEffect(NoteCutCode, Math.Min(byte.MaxValue, (int)Math.Round(cut.Tick * row.Ratio))), effects.Count));
                    break;
                case NoteDelay delay when hasNote:
                    effects.Add(new Planned(KeepFirst, new DmfEffect(NoteDelayCode, Math.Min(byte.MaxValue, (int)Math.Round(delay.Tick * row.Ratio))), effects.Count));
                    break;
                case SetPan pan when channel <= (int)GenesisChannel.Fm6 && track.Source >= 0 && track.Source < _panOverride.Length:
                    _panOverride[track.Source] = pan.Pan;
                    Pan(channel, track, effects);
                    break;
                case SetVibratoWaveform shape when fm:
                    effects.Add(new Planned(DetailPriority, new DmfEffect(VibratoShape, VibratoShapeOf(shape.Shape)), effects.Count));
                    break;
                case SampleOffset when dac:
                case SetFineTune when fm:
                case NoteDelay:
                    break;
                default:
                    Tally(LeftOut(effect, track.Kind));
                    break;
            }
        }

        /// <summary>The conversion note for an effect the channel can't play.</summary>
        private static string LeftOut(Effect effect, VoiceKind? kind)
        {
            string name = effect switch
            {
                Model.Arpeggio => "arpeggio (0xy)",
                PitchSlide => "pitch slide (1xx, 2xx, E1x, E2x)",
                TonePortamento => "tone portamento (3xx, 5xy)",
                Vibrato => "vibrato (4xy, 6xy)",
                Tremolo => "tremolo (7xy)",
                VolumeSlide => "volume slide (Axy, 5xy, 6xy, EAx, EBx)",
                SampleOffset => "sample offset (9xx)",
                SetFineTune => "finetune (E5x)",
                SetVibratoWaveform => "vibrato waveform (E4x)",
                SetTremoloWaveform => "tremolo waveform (E7x): DMF has one tremolo shape",
                SetGlissando => "glissando (E3x): DMF has none",
                SetLowPassFilter => "Amiga filter (E0x): the Genesis has none",
                InvertLoop => "invert loop (EFx): DMF has none",
                SetPan => "pan (8xx) on the PSG, which is mono",
                UnsupportedEffect unsupported => string.Create(CultureInfo.InvariantCulture, $"unsupported command ({unsupported.Description})"),
                _ => effect.GetType().Name,
            };
            string where = kind switch
            {
                VoiceKind.Dac => " on DAC notes (the DAC plays a sample at one pitch and volume)",
                VoiceKind.PsgNoise => " on PSG noise notes",
                _ => string.Empty,
            };
            return "{Count} " + name.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal) + " effects were left out" + where;
        }

        /// <summary>DMF vibrato shapes (<c>E3xx</c>): 0 sine, 5 ramp down, 6 square, 7 random.</summary>
        private static int VibratoShapeOf(Waveform shape) => shape switch
        {
            Waveform.RampDown => 5,
            Waveform.Square => 6,
            Waveform.Random => 7,
            _ => 0,
        };

        /// <summary>
        /// Writes a running effect, unless it is already running with this value: DefleMask restarts or stacks some effects
        /// when they are written again (tremolo deepens until the note is silent, measured with the effect probes).
        /// </summary>
        private static void Issue(DmfEffect effect, int priority, Track track, List<Planned> effects, HashSet<int> issued)
        {
            int group = Group(effect.Code);
            issued.Add(group);
            if (effect.Value != 0 && track.Running.ContainsKey(group) && track.Values.TryGetValue(group, out DmfEffect last) && last == effect)
            {
                return;
            }

            effects.Add(new Planned(priority, effect, effects.Count));
            if (effect.Value == 0)
            {
                track.Running.Remove(group);
                track.Values.Remove(group);
            }
            else
            {
                track.Running[group] = StopCode(effect.Code);
                track.Values[group] = effect;
            }
        }

        /// <summary>Effects that share DefleMask's state: 01, 02 and 03 one speed; 0A and 07 one volume change.</summary>
        private static int Group(int code) => code switch
        {
            SlideDown or Portamento => SlideUp,
            TremoloCode => VolumeSlideCode,
            _ => code,
        };

        /// <summary>The code that stops <paramref name="code"/> with value 0.</summary>
        private static int StopCode(int code) => code == SlideDown ? SlideUp : code;

        private void PitchSlideOf(PitchSlide slide, Memory? memory, RowClock row, Track track, List<Planned> effects, HashSet<int> issued)
        {
            double? amount = slide.Amount ?? memory?.Slide;
            if (memory is not null)
            {
                memory.Slide = amount;
            }

            if (amount is not { } periods || periods == 0 || slide.Scale != PitchScale.AmigaPeriod)
            {
                return;
            }

            int steps = slide.Timing == SlideTiming.OnceAtRowStart ? 1 : row.Speed - 1;
            if (steps <= 0)
            {
                return;
            }

            double move = 12 * Math.Log2(track.Period / Math.Clamp(track.Period - (periods * steps), MinPeriod, MaxPeriod));
            int value = DmfUnits.ByteValue(DmfUnits.FnumDistance(track.Semitones, move) / row.Ticks);
            Issue(new DmfEffect(periods > 0 ? SlideUp : SlideDown, value), PitchPriority, track, effects, issued);
            Move(track, periods * steps);
        }

        /// <summary>
        /// Tone portamento: <c>03xx</c> on the note's row only, with one speed for the whole glide. DefleMask freezes a glide
        /// when <c>03xx</c> is written again on a row without a note (measured with the effect probes), so the rows after it
        /// just let it run.
        /// </summary>
        private void PortamentoOf(TonePortamento porta, Memory? memory, RowClock row, Track track, List<Planned> effects, HashSet<int> issued, bool hasNote)
        {
            double? speed = porta.Speed ?? memory?.Portamento;
            if (memory is not null)
            {
                memory.Portamento = speed;
            }

            if (speed is not { } periods || periods <= 0 || track.TargetPeriod is not { } target || row.Speed <= 1)
            {
                return;
            }

            double distance = Math.Abs(track.Period - target);
            double perRow = periods * (row.Speed - 1);
            if (hasNote && distance > 0)
            {
                // The whole glide takes distance / perRow rows of the MOD, so as many rows' ticks in the DMF.
                double move = 12 * Math.Log2(track.Period / target);
                double ticks = distance / perRow * row.Ticks;
                Issue(new DmfEffect(Portamento, DmfUnits.ByteValue(DmfUnits.FnumDistance(track.Semitones, move) / ticks)), PitchPriority, track, effects, issued);
            }
            else if (track.Running.ContainsKey(Group(Portamento)))
            {
                issued.Add(Group(Portamento));
            }
            else if (distance > 0)
            {
                Tally("{Count} tone portamento rows (3xx, 5xy) continue a glide that had stopped; DefleMask can't restart one without a note, so they are left out");
                return;
            }

            double step = Math.Min(perRow, distance);
            Move(track, track.Period > target ? step : -step);
            if (Math.Abs(track.Period - target) < 1e-9 && track.TargetSemitones is { } semitones)
            {
                track.Semitones = semitones;
            }
        }

        /// <summary>Follows a pitch change of <paramref name="periods"/> (positive raises the pitch) within ProTracker's range.</summary>
        private static void Move(Track track, double periods)
        {
            double before = track.Period;
            track.Period = Math.Clamp(before - periods, MinPeriod, MaxPeriod);
            track.Semitones += 12 * Math.Log2(before / track.Period);
        }

        private void VibratoOf(Vibrato vibrato, Memory? memory, RowClock row, Track track, List<Planned> effects, HashSet<int> issued)
        {
            double? speed = vibrato.CyclesPerTick ?? memory?.VibratoSpeed;
            double? depth = vibrato.Depth ?? memory?.VibratoDepth;
            if (memory is not null)
            {
                memory.VibratoSpeed = speed;
                memory.VibratoDepth = depth;
            }

            if (speed is not { } cycles || depth is not { } peak || vibrato.Scale != PitchScale.AmigaPeriod)
            {
                return;
            }

            // ProTracker moves the vibrato on every tick but a row's first; DefleMask on every tick.
            int x = DmfUnits.Digit(DmfUnits.VibratoSteps * cycles * (row.Speed - 1) / row.Ticks);
            int y = DmfUnits.Digit(DmfUnits.Semitones(track.Period, peak) / DmfUnits.VibratoSemitonesPerDepth);
            if (x > 0 && y > 0)
            {
                Issue(new DmfEffect(VibratoCode, (x << 4) | y), PitchPriority, track, effects, issued);
            }
        }

        private void TremoloOf(Tremolo tremolo, Memory? memory, RowClock row, bool psg, Track track, List<Planned> effects, HashSet<int> issued)
        {
            double? speed = tremolo.CyclesPerTick ?? memory?.TremoloSpeed;
            double? depth = tremolo.Depth ?? memory?.TremoloDepth;
            if (memory is not null)
            {
                memory.TremoloSpeed = speed;
                memory.TremoloDepth = depth;
            }

            if (speed is not { } cycles || depth is not { } peak || peak <= 0)
            {
                return;
            }

            // DefleMask's tremolo only lowers the volume; match the MOD's swing from top to bottom.
            double top = Math.Min(1, track.ModVolume + peak);
            double bottom = Math.Max(1.0 / 64, track.ModVolume - peak);
            int x = DmfUnits.Digit(DmfUnits.TremoloSteps * cycles * (row.Speed - 1) / row.Ticks);
            double swingDb = 20 * Math.Log10(top / bottom);
            int y = DmfUnits.Digit(psg ? DmfUnits.DbToSteps(swingDb, psg: true) : swingDb / DmfUnits.TremoloDbPerDepth);
            if (x > 0 && y > 0)
            {
                Issue(new DmfEffect(TremoloCode, (x << 4) | y), LevelPriority, track, effects, issued);
            }
        }

        private void VolumeSlideOf(VolumeSlide slide, Memory? memory, RowClock row, bool psg, Track track, Draft draft, List<Planned> effects, HashSet<int> issued)
        {
            double? amount = slide.Amount ?? memory?.VolumeSlide;
            if (memory is not null)
            {
                memory.VolumeSlide = amount;
            }

            if (amount is not { } step || step == 0)
            {
                return;
            }

            if (slide.Timing == SlideTiming.OnceAtRowStart)
            {
                // A fine slide is one step at the row's start: just set the volume it reaches.
                track.ModVolume = Math.Clamp(track.ModVolume + step, 0, 1);
                draft.Volume = psg ? DmfUnits.PsgVolume(track.ModVolume) : DmfUnits.FmVolume(track.ModVolume);
                track.DmfVolume = draft.Volume;
                return;
            }

            // Each slide row starts from the MOD's volume, so a slide DefleMask can't keep up with (its fastest is 3.75
            // steps a tick) lags by one row at most.
            double start = DmfUnits.ExactVolume(track.ModVolume, psg);
            if (draft.Volume < 0 && (track.DmfVolume is not { } known || Math.Abs(known - start) >= 1))
            {
                draft.Volume = (int)Math.Round(start, MidpointRounding.AwayFromZero);
                track.DmfVolume = draft.Volume;
            }

            double end = Math.Clamp(track.ModVolume + (step * (row.Speed - 1)), 0, 1);
            double from = track.DmfVolume ?? start;
            double to = DmfUnits.ExactVolume(end, psg);
            track.ModVolume = end;
            double perTick = (to - from) / row.Ticks;
            int digit = DmfUnits.Digit(Math.Abs(perTick) / DmfUnits.VolumeSlideStep);
            if (perTick < 0)
            {
                // Sliding below 0 makes DefleMask jump back to full volume (Furnace: legacyVolumeSlides); stop at 0.
                digit = Math.Min(digit, (int)Math.Floor(from / (DmfUnits.VolumeSlideStep * row.Ticks)));
            }

            if (digit == 0)
            {
                return;
            }

            int max = psg ? DmfUnits.MaxPsgVolume : DmfUnits.MaxFmVolume;
            Issue(new DmfEffect(VolumeSlideCode, perTick > 0 ? digit << 4 : digit), LevelPriority, track, effects, issued);
            track.DmfVolume = Math.Clamp(from + (Math.Sign(perTick) * digit * DmfUnits.VolumeSlideStep * row.Ticks), 0, max);
        }

        private void Tally(string template, int count = 1) => _tallies[template] = _tallies.GetValueOrDefault(template) + count;
    }

    /// <summary>What one Genesis channel is doing, as far as DefleMask knows; −1 or <c>null</c> where it can't be known.</summary>
    private sealed class Track
    {
        public int Pan { get; set; } = -1;

        public int Bank { get; set; } = -1;

        public int DacMode { get; set; } = -1;

        public int Fine { get; set; } = -1;

        public int NoiseMode { get; set; } = -1;

        /// <summary>Running effects, by group (<see cref="Pass"/>), with the code that stops each.</summary>
        public Dictionary<int, int> Running { get; } = [];

        /// <summary>The running effects' last written values, by group, so an unchanged one isn't written again.</summary>
        public Dictionary<int, DmfEffect> Values { get; } = [];

        public VoiceKind? Kind { get; set; }

        public int Source { get; set; } = -1;

        public bool Sounding { get; set; }

        /// <summary>The MOD volume, 0–1.</summary>
        public double ModVolume { get; set; } = 1;

        /// <summary>The DMF volume in steps, or <c>null</c> when unknown.</summary>
        public double? DmfVolume { get; set; }

        /// <summary>The Amiga period now playing.</summary>
        public double Period { get; set; } = 428;

        /// <summary>The pitch heard now, in semitones from C-0.</summary>
        public double Semitones { get; set; }

        public double? TargetPeriod { get; set; }

        public double? TargetSemitones { get; set; }

        public int? DacVariant { get; set; }

        public double DacStart { get; set; }

        /// <summary>After a jump lands here, what DefleMask has set can't be known.</summary>
        public void Forget()
        {
            Pan = -1;
            Bank = -1;
            DacMode = -1;
            Fine = -1;
            NoiseMode = -1;
            DmfVolume = null;
            Values.Clear();
        }
    }

    /// <summary>A MOD channel's effect memory (a parameter of 0 repeats the last one), as the plan carries it per source channel.</summary>
    private sealed class Memory
    {
        public double? Slide { get; set; }

        public double? Portamento { get; set; }

        public double? VibratoSpeed { get; set; }

        public double? VibratoDepth { get; set; }

        public double? TremoloSpeed { get; set; }

        public double? TremoloDepth { get; set; }

        public double? VolumeSlide { get; set; }

        public int Offset { get; set; }
    }

    /// <summary>A cell's note, octave, volume and instrument being written.</summary>
    private sealed class Draft
    {
        public int Note { get; set; }

        public int Octave { get; set; }

        public int Volume { get; set; } = -1;

        public int Instrument { get; set; } = -1;
    }

    /// <summary>An effect waiting for a column: lower <paramref name="Priority"/> stays when there are too many.</summary>
    private sealed record Planned(int Priority, DmfEffect Effect, int Order = 0);

    /// <summary>The row being written.</summary>
    /// <param name="Ticks">DMF ticks in the row.</param>
    /// <param name="Speed">MOD ticks in the row.</param>
    /// <param name="Ratio">DMF ticks per MOD tick.</param>
    private sealed record RowClock(int Ticks, int Speed, double Ratio);
}

/// <summary>One copy of a DAC sample the song needs: which sample, from which frame, how much quieter.</summary>
/// <param name="Dac">Index into <see cref="GenesisPlan.DacSamples"/>.</param>
/// <param name="Offset">First frame played (MOD <c>9xx</c>), in the song sample's frames.</param>
/// <param name="VolumeSteps">How many 3 dB steps quieter than the sample (<see cref="DmfPatternTranslator.DacVolumeStepDb"/>).</param>
internal readonly record struct DacVariant(int Dac, int Offset, int VolumeSteps);

/// <summary>What the translator wrote.</summary>
/// <param name="Cells">By channel, order and row.</param>
/// <param name="DacVariants">The DAC sample copies, in DAC-note order (bank × 12 + note).</param>
/// <param name="DacSeconds">The longest each copy sounds before the next DAC note, in seconds; looped samples are played out this long.</param>
/// <param name="Tallies">Conversion-note templates (each with a <c>{Count}</c>) and how often each happened.</param>
/// <param name="FmSamples">The samples FM notes were made from, and whether each one's pitch was measured.</param>
internal sealed record DmfTranslation(
    IReadOnlyList<IReadOnlyList<IReadOnlyList<DmfCell>>> Cells,
    IReadOnlyList<DacVariant> DacVariants,
    IReadOnlyList<double> DacSeconds,
    IReadOnlyDictionary<string, int> Tallies,
    IReadOnlyDictionary<int, bool> FmSamples);
