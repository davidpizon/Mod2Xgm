using System.Globalization;
using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;

namespace Mod2Xgm.Optimizer;

/// <summary>
/// The greedy allocator (plan T9.3, docs/adr/0087-greedy-allocator.md, docs/multi-pass.md#passes): section by section (one
/// order each), the most important tracks (a sample on a channel) choose first, each taking the sound closest to the
/// MOD for its notes. A note whose sound has no room takes the next best; with none left it is cut. A sample with no
/// steady pitch never takes a tuned voice unless the user requires it, so its notes can be cut while tuned voices sit
/// free (David: it plays as wrong notes). <c>--require-*</c> samples only ever use their kind. An FM note may also sound a
/// quiet PSG tone, on a channel no note needs (docs/adr/0088-layering.md).
/// </summary>
/// <param name="limits">What the target holds.</param>
/// <param name="menu">What each sample can sound like.</param>
internal sealed class OptimizerMapper(TargetLimits limits, IReadOnlyList<SampleSounds> menu) : IChannelMapper
{
    /// <summary>What a fully percussive sample's notes add to their importance, as a multiple of a note with no hit: a replaced attack is easy to hear, and David chose this over smaller values (docs/sample-capacity.md#importance).</summary>
    private const double PercussiveBoost = 8;

    /// <summary>The most of a note's length that counts towards its importance: a drum is as short as the long notes it would otherwise lose to are long (docs/sample-capacity.md#importance).</summary>
    private const double MaxHeardSeconds = 0.25;

    /// <summary>Importance of a note on a bar's first row (<see cref="RowsPerBar"/>) against any other (docs/sample-capacity.md#importance).</summary>
    private const double DownbeatBoost = 1.5;

    /// <summary>Rows between downbeats: MOD patterns of 64 rows are almost always four bars of 16.</summary>
    private const int RowsPerBar = 16;

    /// <summary>How much closer another sound must be, in dB of log-mel distance, for a track to change sound between sections (David: sticky).</summary>
    private const double SwitchCostDb = 3;

    /// <summary>What a recording or a PSG tone costs a note that slides or wobbles, in dB: they hold their starting pitch and FM doesn't (David).</summary>
    private const double BentCostDb = 12;

    /// <summary>A PSG tone's level under an FM note, as a factor on what it would play at alone: 12 dB down, David's pick over 6 dB, which stuttered on hawks_claw and was too crisp on kc (docs/adr/0088-layering.md).</summary>
    internal const double ToneLayerLevel = 0.25;

    /// <summary>A distance for a sound with no measurement: as far as the ruler goes.</summary>
    private const double UnknownDb = NoteScorer.RangeDb;

    /// <summary>The order sounds are tried in when they tie: the least changed first.</summary>
    private static readonly VoiceKind[] AllKinds = [VoiceKind.Dac, VoiceKind.Fm, VoiceKind.PsgTone, VoiceKind.PsgNoise];

    /// <summary>
    /// Notes the last <see cref="Map"/> played with anything but their first choice, or cut, because of a limit: what
    /// <c>--no-consolidate</c> refuses (docs/adr/0087-greedy-allocator.md).
    /// </summary>
    public int NotesMoved { get; private set; }

    /// <inheritdoc />
    public GenesisPlan Map(Song song, Playback playback, MappingOptions options, ConversionNotes notes)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(notes);
        if (options is { UseDac: false, RequireDac.Count: > 0 })
        {
            throw new ArgumentException("--no-dac and --require-dac can't both be given.", nameof(options));
        }

        Run run = new(limits, menu, song, playback, options, notes);
        GenesisPlan plan = run.Execute();
        NotesMoved = run.Moved;
        return plan;
    }

    /// <summary>A note of the song's pattern, as the allocator sees it: one cell of an order, however often the song plays it.</summary>
    private sealed class PlannedNote(int order, int row, int channel, int sample, NotePitch pitch, double volume)
    {
        public int Order => order;

        public int Row => row;

        public int Channel => channel;

        public int Sample => sample;

        public NotePitch Pitch => pitch;

        public double Volume => volume;

        /// <summary>Each time the song plays it.</summary>
        public List<SongNote> Plays { get; } = [];

        public double Importance { get; set; }

        /// <summary>True when its pitch or volume moves while it sounds.</summary>
        public bool Bent { get; set; }

        /// <summary>The sound it was given; <c>null</c> when it was cut.</summary>
        public VoiceKind? Kind { get; set; }

        /// <summary>The column it plays on, or −1.</summary>
        public int Column { get; set; } = -1;

        /// <summary>The first tick it sounds on.</summary>
        public int FirstTick => Plays.Min(p => p.FirstTick);

        /// <summary>True when its pitch moves while it sounds: a PSG tone couldn't follow.</summary>
        public bool Slides { get; set; }

        /// <summary>The column of a second voice under it, or −1 (docs/adr/0088-layering.md).</summary>
        public int LayerColumn { get; set; } = -1;
    }

    /// <summary>One pass over a song.</summary>
    private sealed class Run
    {
        private readonly TargetLimits _limits;
        private readonly Song _song;
        private readonly Playback _playback;
        private readonly MappingOptions _options;
        private readonly ConversionNotes _notes;
        private readonly Dictionary<int, SampleSounds> _menu;
        private readonly bool _dmf;
        private readonly int _columns;

        // When each column plays: every note's voice first, so a layer can only take what nothing needs.
        private readonly List<(int From, int To)>[] _busy;

        // Sounds at once on every tick, per kind.
        private readonly int[] _dacAt;
        private readonly int[] _fmAt;
        private readonly int[] _toneAt;
        private readonly int[] _noiseAt;

        // What the recordings store so far (docs/adr/0086-capacity-check.md).
        private readonly Dictionary<(int Sample, double RateHz, int StartFrame, int VolumeSteps), int> _copyBytes = [];
        private readonly HashSet<(int Sample, double RateHz, int StartFrame, int VolumeSteps)> _stored = [];
        private readonly Dictionary<int, int> _storedAs = [];
        private long _bytes;

        private readonly List<PlannedNote> _planned = [];
        private readonly Dictionary<(int Order, int Row, int Channel), PlannedNote> _noteAt = [];
        private readonly Dictionary<int, Dictionary<VoiceKind, int>> _played = [];
        private readonly SortedDictionary<int, int> _ignored = [];
        private readonly Dictionary<(int Channel, int Sample), VoiceKind?> _lastKind = [];

        private int _unplayed;
        private int _noInstrument;
        private int _silent;
        private int _slides;

        public Run(TargetLimits limits, IReadOnlyList<SampleSounds> menu, Song song, Playback playback, MappingOptions options, ConversionNotes notes)
        {
            _limits = limits;
            _song = song;
            _playback = playback;
            _options = options;
            _notes = notes;
            _menu = menu.ToDictionary(m => m.Sample);
            _dmf = limits.Target == PcmTarget.Dmf;
            _columns = _dmf ? GenesisPlan.ChannelCount : GenesisPlan.ColumnCount;
            _busy = [.. Enumerable.Range(0, _columns).Select(_ => new List<(int, int)>())];
            int ticks = playback.Ticks.Count;
            _dacAt = new int[ticks];
            _fmAt = new int[ticks];
            _toneAt = new int[ticks];
            _noiseAt = new int[ticks];
        }

        /// <summary>Notes that lost their first choice to a limit.</summary>
        public int Moved { get; private set; }

        public GenesisPlan Execute()
        {
            Collect();
            SizeCopies();
            CheckRequired();
            foreach (IGrouping<int, PlannedNote> section in _planned.GroupBy(n => n.Order).OrderBy(g => g.Key))
            {
                Allocate(section);
            }

            AssignColumns();
            AddLayers();
            return Write();
        }

        /// <summary>Finds every note the plan's cells will hold, with how the song plays it and how much it matters.</summary>
        private void Collect()
        {
            ILookup<(int Order, int Row, int Channel), SongNote> plays = SongNote.Find(_song, _playback)
                .ToLookup(n => (_playback.Ticks[n.FirstTick].Order, _playback.Ticks[n.FirstTick].Row, n.Channel));
            int[] instrument = [.. Enumerable.Repeat(-1, _song.Channels.Count)];
            double[] volume = [.. Enumerable.Repeat(1.0, _song.Channels.Count)];
            PlannedNote?[] last = new PlannedNote?[_song.Channels.Count];
            for (int order = 0; order < _song.Orders.Count; order++)
            {
                IReadOnlyList<Row> rows = _song.Patterns[_song.Orders[order]].Rows;
                for (int r = 0; r < rows.Count; r++)
                {
                    for (int c = 0; c < rows[r].Cells.Count; c++)
                    {
                        Cell cell = rows[r].Cells[c];
                        if (cell.Instrument is { } chosen)
                        {
                            instrument[c] = chosen;
                            volume[c] = _song.Instruments[chosen].DefaultVolume;
                        }

                        volume[c] = cell.Volume ?? volume[c];
                        IEnumerable<SongNote> here = plays[(order, r, c)];
                        if (cell.Note is null)
                        {
                            // A retrigger with no note: the same note again, for as long as it sounds.
                            last[c]?.Plays.AddRange(here);
                        }
                        else
                        {
                            last[c] = Note(order, r, c, cell, instrument[c], volume[c], here) ?? last[c];
                        }
                    }
                }
            }

            foreach (PlannedNote note in _planned)
            {
                note.Importance = Importance(note);
                note.Bent = note.Plays.Any(Bends);
                note.Slides = note.Plays.Any(Slides);
            }
        }

        /// <summary>Records the note in <paramref name="cell"/> when it will sound; a slide stays with the note it slides.</summary>
        private PlannedNote? Note(int order, int row, int c, Cell cell, int instrument, double volume, IEnumerable<SongNote> here)
        {
            List<SongNote> plays = [.. here];
            if (instrument < 0)
            {
                _noInstrument++;
                return null;
            }

            int sample = _song.Instruments[instrument].Sample;
            if (_options.Ignore.Contains(sample))
            {
                _ignored[sample] = _ignored.GetValueOrDefault(sample) + 1;
                return null;
            }

            if (_song.Samples[sample].IsEmpty || !_menu.ContainsKey(sample))
            {
                _silent++;
                return null;
            }

            if (plays.Count == 0)
            {
                _unplayed += cell.Effects.OfType<TonePortamento>().Any() ? 0 : 1;
                return null;
            }

            NotePitch pitch = _playback.NotePitches.TryGetValue(new NotePosition(order, row, c), out NotePitch played) ? played : cell.Note ?? default;
            PlannedNote note = new(order, row, c, sample, pitch, volume);
            note.Plays.AddRange(plays);
            _planned.Add(note);
            _noteAt[(order, row, c)] = note;
            return note;
        }

        /// <summary>
        /// How much it matters that a note sounds right (docs/sample-capacity.md#importance): how long and how loud it plays
        /// each time, more for a sample that hits and for a note on a downbeat.
        /// </summary>
        private double Importance(PlannedNote note)
        {
            double heard = note.Plays.Sum(p => Math.Min(p.Seconds, MaxHeardSeconds) * p.Volume);
            double hit = 1 + (PercussiveBoost * _menu[note.Sample].Percussive);
            double beat = note.Row % RowsPerBar == 0 ? DownbeatBoost : 1;
            return heard * hit * beat;
        }

        /// <summary>True when the note's pitch or volume moves while it sounds: a start past frame 0 alone doesn't, as a copy can hold that.</summary>
        private bool Bends(SongNote play)
        {
            for (int t = play.FirstTick; t < play.FirstTick + play.Ticks; t++)
            {
                ChannelTick tick = _playback.Ticks[t].Channels[play.Channel];
                if (Math.Abs(tick.Pitch.PlaybackRateHz - play.RateHz) > play.RateHz * 1e-9 || Math.Abs(tick.Volume - play.Volume) > 1e-9)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>True when the note's pitch moves while it sounds.</summary>
        private bool Slides(SongNote play)
        {
            for (int t = play.FirstTick; t < play.FirstTick + play.Ticks; t++)
            {
                if (Math.Abs(_playback.Ticks[t].Channels[play.Channel].Pitch.PlaybackRateHz - play.RateHz) > play.RateHz * 1e-9)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The size of every copy a recording could store, as the capacity check counts them.</summary>
        private void SizeCopies()
        {
            IEnumerable<SongNote> wanted = _planned.Where(n => _menu[n.Sample].RecordingDb.Count > 0).SelectMany(n => n.Plays);
            foreach (((int Sample, double RateHz, int StartFrame, int VolumeSteps) key, double seconds) in CapacityCheck.Copies(wanted, _song, _storedAs))
            {
                _copyBytes[key] = CapacityCheck.CopyBytes(key, seconds, _song, _limits.Target);
            }
        }

        /// <summary>Exit 6 when the <c>--require-dac</c> samples alone can't fit: they can never become anything else (docs/sample-capacity.md#required-fm-dac-and-psg-samples).</summary>
        private void CheckRequired()
        {
            List<PlannedNote> required = [.. _planned.Where(n => _options.RequireDac.Contains(n.Sample) && _menu[n.Sample].RecordingDb.Count > 0)];
            if (required.Count == 0)
            {
                return;
            }

            HashSet<(int Sample, double RateHz, int StartFrame, int VolumeSteps)> copies = [.. required.SelectMany(n => n.Plays).SelectMany(CopiesOf)];
            long bytes = copies.Sum(c => (long)_copyBytes[c]);
            string names = string.Join(", ", required.Select(n => n.Sample).Distinct().Order().Select(s => Label(s, _song.Samples[s].Name)));
            List<string> problems = [];
            if (_limits.PcmVoices < 1)
            {
                problems.Add("a PCM voice (none left after --reserve-sfx-channels)");
            }

            if (copies.Count > _limits.PcmSlots)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{copies.Count} sample slots (limit {_limits.PcmSlots})"));
            }

            if (_limits.PcmBytes is { } budget && bytes > budget)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{bytes} PCM bytes (budget {budget}; raise it with --max-pcm-bytes)"));
            }

            if (problems.Count > 0)
            {
                throw new ConversionException(ExitCode.DoesNotFit, $"The samples required as DAC ({names}) can't fit {PcmTargets.Name(_limits.Target)}: they need {string.Join(", ", problems)}. Require fewer samples.");
            }

            foreach ((int Sample, double RateHz, int StartFrame, int VolumeSteps) copy in copies)
            {
                _stored.Add(copy);
                _bytes += _copyBytes[copy];
            }
        }

        /// <summary>The copy a play needs, if it needs one: a note too quiet or past the sample's end stores nothing.</summary>
        private IEnumerable<(int Sample, double RateHz, int StartFrame, int VolumeSteps)> CopiesOf(SongNote play) =>
            CapacityCheck.Copies([play], _song, _storedAs).Keys;

        /// <summary>
        /// One section: the track whose notes matter most first, taking the closest sound with room for all its notes, so it
        /// doesn't change sound within the section; when none has, each note takes the first with room for it. A track stays
        /// with the sound it had unless another is clearly closer (<see cref="SwitchCostDb"/>).
        /// </summary>
        private void Allocate(IEnumerable<PlannedNote> section)
        {
            foreach (IGrouping<(int Channel, int Sample), PlannedNote> track in section.GroupBy(n => (n.Channel, n.Sample))
                .OrderByDescending(t => t.Average(n => n.Importance))
                .ThenBy(t => t.Key.Channel)
                .ThenBy(t => t.Key.Sample))
            {
                VoiceKind[] ranked = Ranked(track);
                List<PlannedNote> notes = [.. track.OrderBy(n => n.Row)];
                VoiceKind? whole = ranked.Cast<VoiceKind?>().FirstOrDefault(k => k is { } kind && notes.All(n => Allowed(kind, n)) && HasRoomForAll(kind, notes));
                foreach (PlannedNote note in notes)
                {
                    VoiceKind[] allowed = [.. ranked.Where(k => Allowed(k, note))];
                    IEnumerable<VoiceKind> tries = whole is { } kind ? [kind] : allowed;
                    note.Kind = tries.Cast<VoiceKind?>().FirstOrDefault(k => k is { } tried && Take(tried, note));
                    Moved += allowed.Length > 0 && note.Kind == allowed[0] ? 0 : 1;
                }

                _lastKind[track.Key] = track.Select(n => n.Kind).FirstOrDefault(k => k is not null) ?? _lastKind.GetValueOrDefault(track.Key);
            }
        }

        /// <summary>The sounds the track may use, closest first over its notes in this section.</summary>
        private VoiceKind[] Ranked(IGrouping<(int Channel, int Sample), PlannedNote> track)
        {
            VoiceKind? had = _lastKind.GetValueOrDefault(track.Key);
            double total = track.Sum(n => n.Importance);
            return [.. AllKinds
                .Where(k => track.Any(n => Allowed(k, n)))
                .OrderBy(k => track.Sum(n => n.Importance * Distance(k, n)) + (had is { } h && h != k ? total * SwitchCostDb : 0))];
        }

        /// <summary>True when the user's lists and the menu let <paramref name="note"/> use <paramref name="kind"/>.</summary>
        private bool Allowed(VoiceKind kind, PlannedNote note)
        {
            SampleSounds sounds = _menu[note.Sample];
            RegionSounds? region = sounds.RegionAt(note.Plays[0].RateHz);
            bool dac = _options.RequireDac.Contains(note.Sample);
            bool fm = _options.RequireFm.Contains(note.Sample);
            bool psg = _options.RequirePsg.Contains(note.Sample);
            return kind switch
            {
                VoiceKind.Dac => !fm && !psg && _options.UseDac && _limits.PcmVoices > 0 && sounds.RecordingDb.Count > 0,
                VoiceKind.Fm => !dac && !psg && region?.Patch is not null && (fm || sounds.SteadyPitch),
                VoiceKind.PsgTone => !dac && !fm && region?.ToneDb is not null && (psg || sounds.SteadyPitch),
                _ => !dac && !fm && region?.NoiseDb is not null,
            };
        }

        /// <summary>How far <paramref name="kind"/> is from <paramref name="note"/> in dB, with what it loses on a note that bends.</summary>
        private double Distance(VoiceKind kind, PlannedNote note)
        {
            SampleSounds sounds = _menu[note.Sample];
            RegionSounds? region = sounds.RegionAt(note.Plays[0].RateHz);
            double db = kind switch
            {
                // The sample itself: David's ears rank it first, whatever the ruler reads (docs/adr/0087-greedy-allocator.md).
                VoiceKind.Dac => 0,
                VoiceKind.Fm => region?.FmDb ?? UnknownDb,
                VoiceKind.PsgTone => region?.ToneDb ?? UnknownDb,
                _ => region?.NoiseDb ?? UnknownDb,
            };
            return db + (note.Bent && kind is VoiceKind.Dac or VoiceKind.PsgTone ? BentCostDb : 0);
        }

        /// <summary>True when <paramref name="kind"/> has room for every one of <paramref name="notes"/>, which don't overlap each other when they share a channel.</summary>
        private bool HasRoomForAll(VoiceKind kind, List<PlannedNote> notes)
        {
            if (!notes.SelectMany(n => n.Plays).SelectMany(p => Enumerable.Range(p.FirstTick, p.Ticks)).All(t => HasRoom(kind, t)))
            {
                return false;
            }

            if (kind != VoiceKind.Dac)
            {
                return true;
            }

            List<(int Sample, double RateHz, int StartFrame, int VolumeSteps)> fresh = [.. notes.SelectMany(n => n.Plays).SelectMany(CopiesOf).Distinct().Where(c => !_stored.Contains(c))];
            long bytes = fresh.Sum(c => (long)_copyBytes[c]);
            return _stored.Count + fresh.Count <= _limits.PcmSlots && (_limits.PcmBytes is not { } budget || _bytes + bytes <= budget);
        }

        /// <summary>Gives <paramref name="note"/> the room it needs for <paramref name="kind"/> on every tick it sounds, if it is there.</summary>
        private bool Take(VoiceKind kind, PlannedNote note)
        {
            List<int> ticks = [.. note.Plays.SelectMany(p => Enumerable.Range(p.FirstTick, p.Ticks))];
            if (!ticks.All(t => HasRoom(kind, t)) || (kind == VoiceKind.Dac && !Store(note)))
            {
                return false;
            }

            int[] counts = kind switch
            {
                VoiceKind.Dac => _dacAt,
                VoiceKind.Fm => _fmAt,
                VoiceKind.PsgTone => _toneAt,
                _ => _noiseAt,
            };
            foreach (int t in ticks)
            {
                counts[t]++;
            }

            return true;
        }

        /// <summary>True when <paramref name="kind"/> has room on tick <paramref name="t"/>. On a DMF the DAC and FM6 are one channel.</summary>
        private bool HasRoom(VoiceKind kind, int t) => kind switch
        {
            VoiceKind.Dac => _dacAt[t] < _limits.PcmVoices && (!_dmf || _dacAt[t] + _fmAt[t] < _limits.FmChannels),
            VoiceKind.Fm => _fmAt[t] + (_dmf ? _dacAt[t] : 0) < _limits.FmChannels,
            VoiceKind.PsgTone => _toneAt[t] < _limits.PsgTones,
            _ => _noiseAt[t] < _limits.PsgNoise,
        };

        /// <summary>Stores the copies <paramref name="note"/> needs, if the target's slots and bytes hold them.</summary>
        private bool Store(PlannedNote note)
        {
            List<(int Sample, double RateHz, int StartFrame, int VolumeSteps)> fresh = [.. note.Plays.SelectMany(CopiesOf).Distinct().Where(c => !_stored.Contains(c))];
            long bytes = fresh.Sum(c => (long)_copyBytes[c]);
            if (_stored.Count + fresh.Count > _limits.PcmSlots || (_limits.PcmBytes is { } budget && _bytes + bytes > budget))
            {
                return false;
            }

            _stored.UnionWith(fresh);
            _bytes += bytes;
            return true;
        }

        private void Count(PlannedNote note)
        {
            if (!_played.TryGetValue(note.Sample, out Dictionary<VoiceKind, int>? counts))
            {
                counts = [];
                _played[note.Sample] = counts;
            }

            if (note.Kind is { } kind)
            {
                counts[kind] = counts.GetValueOrDefault(kind) + 1;
            }
        }

        /// <summary>
        /// Gives each note a column, in the order they start: the counts above say there are enough for the notes at any
        /// moment, and going in time order then always finds one (an order the song plays twice can still clash, and is cut).
        /// </summary>
        private void AssignColumns()
        {
            List<(int From, int To)>[] busy = _busy;
            int[] onChannel = [.. Enumerable.Repeat(-1, _song.Channels.Count)];
            foreach (PlannedNote note in _planned.Where(n => n.Kind is not null).OrderBy(n => n.FirstTick).ThenBy(n => n.Channel))
            {
                List<(int From, int To)> spans = [.. note.Plays.Select(p => (p.FirstTick, p.FirstTick + p.Ticks))];
                int column = ColumnsFor(note, onChannel[note.Channel]).FirstOrDefault(c => !spans.Any(s => busy[c].Any(b => s.From < b.To && b.From < s.To)), -1);
                if (column < 0)
                {
                    note.Kind = null;
                    Moved++;
                    continue;
                }

                note.Column = column;
                busy[column].AddRange(spans);
                onChannel[note.Channel] = column;
            }
        }

        /// <summary>
        /// Gives second voices to the notes that can take one (docs/adr/0088-layering.md), after every note has
        /// its own voice, so a layer never costs a note anything. Most important tracks first, section by section; a track
        /// takes layers only when every note it has in the section can, so the tone doesn't come and go note by note: on
        /// hawks_claw David heard that as a stutter, on a sample playing as a mix of FM and recordings.
        /// </summary>
        private void AddLayers()
        {
            int[] layerOn = [.. Enumerable.Repeat(-1, _song.Channels.Count)];
            foreach (IGrouping<int, PlannedNote> section in _planned.GroupBy(n => n.Order).OrderBy(g => g.Key))
            {
                foreach (IGrouping<(int Channel, int Sample), PlannedNote> track in section.GroupBy(n => (n.Channel, n.Sample))
                    .OrderByDescending(t => t.Average(n => n.Importance))
                    .ThenBy(t => t.Key.Channel)
                    .ThenBy(t => t.Key.Sample))
                {
                    if (track.All(TakesLayer))
                    {
                        layerOn[track.Key.Channel] = PlaceLayers([.. track.OrderBy(n => n.FirstTick)], layerOn[track.Key.Channel]);
                    }
                }
            }
        }

        /// <summary>Gives every one of <paramref name="notes"/> a free tone column, staying on the one the channel last used (<paramref name="last"/>), or leaves them all without. Returns the column the channel used last.</summary>
        private int PlaceLayers(List<PlannedNote> notes, int last)
        {
            List<(PlannedNote Note, int Column, List<(int From, int To)> Spans)> placed = [];
            int previous = last;
            foreach (PlannedNote note in notes)
            {
                List<(int From, int To)> spans = [.. note.Plays.Select(p => (p.FirstTick, p.FirstTick + p.Ticks))];
                int stay = previous;
                int column = Enumerable.Range((int)GenesisChannel.Psg1, _limits.PsgTones)
                    .OrderBy(c => c == stay ? 0 : 1)
                    .ThenBy(c => c)
                    .FirstOrDefault(c => !spans.Any(s => _busy[c].Any(b => s.From < b.To && b.From < s.To)) && !placed.Any(p => p.Column == c && p.Spans.Any(o => spans.Any(s => s.From < o.To && o.From < s.To))), -1);
                if (column < 0)
                {
                    return last;
                }

                placed.Add((note, column, spans));
                previous = column;
            }

            foreach ((PlannedNote note, int column, List<(int From, int To)> spans) in placed)
            {
                note.LayerColumn = column;
                _busy[column].AddRange(spans);
            }

            return previous;
        }

        /// <summary>True when <paramref name="note"/> can take a tone under it: an FM note with a steady pitch the tone channels reach. A required sample keeps to its one kind, and a tone can't follow a slide.</summary>
        private bool TakesLayer(PlannedNote note) =>
            note is { Kind: VoiceKind.Fm, Column: >= 0, Slides: false }
            && !_options.RequireFm.Contains(note.Sample)
            && _menu[note.Sample].SteadyPitch
            && _menu[note.Sample].RegionAt(note.Plays[0].RateHz)?.ToneDb is not null;

        /// <summary>
        /// The columns that can play <paramref name="note"/>, best first: the one its channel just played on, then its
        /// channel's own. FM takes FM6 last of all, on a DMF as on XGM: it is the first DAC voice's only column, and FM
        /// settled there would leave every recording after it nowhere to play.
        /// </summary>
        private IEnumerable<int> ColumnsFor(PlannedNote note, int previous)
        {
            int[] columns = note.Kind switch
            {
                VoiceKind.Dac => [(int)GenesisChannel.Fm6, .. Enumerable.Range((int)GenesisChannel.Pcm2, Math.Max(0, _limits.PcmVoices - 1))],
                VoiceKind.Fm => [.. Enumerable.Range(0, _limits.FmChannels)],
                VoiceKind.PsgTone => [.. Enumerable.Range((int)GenesisChannel.Psg1, _limits.PsgTones)],
                _ => [(int)GenesisChannel.Psg4],
            };
            return columns
                .OrderBy(c => note.Kind == VoiceKind.Fm && c == (int)GenesisChannel.Fm6 ? 1 : 0)
                .ThenBy(c => c == previous ? 0 : c == note.Channel ? 1 : 2)
                .ThenBy(c => c);
        }

        /// <summary>Turns the choices into the plan's rows.</summary>
        private GenesisPlan Write()
        {
            List<FmPatch> patches = [];
            Dictionary<FmPatch, int> patchIndex = [];
            List<DacSample> recordings = [];
            Dictionary<(int Sample, double Rate), int> recordingIndex = [];
            int?[] on = new int?[_song.Channels.Count];
            int?[] layerOn = new int?[_song.Channels.Count];
            int?[] owner = new int?[_columns];
            PlanVoice[] voiceOn = new PlanVoice[_columns];
            int[] sampleOn = new int[_columns];
            List<IReadOnlyList<PlanRow>> patterns = [];
            for (int order = 0; order < _song.Orders.Count; order++)
            {
                IReadOnlyList<Row> rows = _song.Patterns[_song.Orders[order]].Rows;
                PlanRow[] planned = new PlanRow[rows.Count];
                for (int r = 0; r < rows.Count; r++)
                {
                    planned[r] = WriteRow(rows[r], order, r);
                }

                patterns.Add(planned);
            }

            // Counted only now: placing the notes on columns can still cut one.
            _planned.ForEach(Count);
            ReportNotes();
            HashSet<int> wanted = [.. _planned.Where(n => FirstChoice(n) == VoiceKind.Dac).Select(n => n.Sample)];
            return new GenesisPlan(_song.Title, patterns, _song.LoopOrder, _song.Timing, patches, recordings) { PcmSamples = wanted, TunedByEar = true };

            PlanRow WriteRow(Row row, int order, int r)
            {
                List<Effect> flow = [];
                PlanCell[] cells = [.. Enumerable.Repeat(PlanCell.Empty, _columns)];
                List<Effect>[] own = [.. row.Cells.Select(_ => new List<Effect>())];
                for (int c = 0; c < row.Cells.Count; c++)
                {
                    foreach (Effect effect in row.Cells[c].Effects)
                    {
                        (IsFlow(effect) ? flow : own[c]).Add(effect);
                    }
                }

                // Notes first, so an effect or a stop follows the column as it is after this row's notes.
                for (int c = 0; c < row.Cells.Count; c++)
                {
                    if (row.Cells[c].Note is not null)
                    {
                        WriteNote(cells, c, order, r, row.Cells[c], own[c]);
                    }
                }

                for (int c = 0; c < row.Cells.Count; c++)
                {
                    if (row.Cells[c].Note is null && on[c] is { } column && owner[column] == c)
                    {
                        Cell cell = row.Cells[c];
                        double? volume = cell.Volume ?? (cell.Instrument is { } i ? _song.Instruments[i].DefaultVolume : null);
                        if (volume is not null || own[c].Count > 0)
                        {
                            cells[column] = cells[column] with { Volume = volume, Effects = own[c], Source = c };
                            if (layerOn[c] is { } layer && owner[layer] == c)
                            {
                                cells[layer] = cells[layer] with { Volume = volume, Effects = own[c], Source = c };
                            }
                        }
                    }
                }

                return new PlanRow(cells, flow);
            }

            void WriteNote(PlanCell[] cells, int c, int order, int r, Cell cell, List<Effect> effects)
            {
                if (_noteAt.TryGetValue((order, r, c), out PlannedNote? note))
                {
                    if (note.Kind is not { } kind)
                    {
                        Stop(cells, c);
                        return;
                    }

                    if (on[c] is { } previous && previous != note.Column && owner[previous] == c && cells[previous].Note is null)
                    {
                        cells[previous] = cells[previous] with { NoteOff = true, Source = c };
                    }

                    PlanVoice voice = new(kind, kind switch
                    {
                        VoiceKind.Dac => IndexOf(recordings, recordingIndex, (StoredAs(note.Sample), note.Plays[0].RateHz)),
                        VoiceKind.Fm => IndexOfPatch(patches, patchIndex, note),
                        _ => 0,
                    });
                    cells[note.Column] = new PlanCell(note.Pitch, voice, note.Volume, false, effects, c, note.Sample);
                    on[c] = note.Column;
                    owner[note.Column] = c;
                    voiceOn[note.Column] = voice;
                    sampleOn[note.Column] = note.Sample;
                    WriteLayer(cells, c, note, effects);
                    return;
                }

                if (cell.Effects.OfType<TonePortamento>().Any())
                {
                    WriteSlide(cells, c, cell, effects);
                    return;
                }

                int sample = cell.Instrument is { } i ? _song.Instruments[i].Sample : -1;
                if (sample >= 0 && _options.Ignore.Contains(sample))
                {
                    Stop(cells, c);
                }
            }

            // The second voice follows the note; a note without one stops the one its channel had.
            void WriteLayer(PlanCell[] cells, int c, PlannedNote note, List<Effect> effects)
            {
                if (layerOn[c] is { } old && old != note.LayerColumn && owner[old] == c && cells[old].Note is null)
                {
                    cells[old] = cells[old] with { NoteOff = true, Source = c };
                    owner[old] = null;
                }

                layerOn[c] = null;
                if (note.LayerColumn >= 0)
                {
                    PlanVoice layer = new(VoiceKind.PsgTone, 0);
                    cells[note.LayerColumn] = new PlanCell(note.Pitch, layer, note.Volume, false, effects, c, note.Sample) { Level = ToneLayerLevel };
                    layerOn[c] = note.LayerColumn;
                    owner[note.LayerColumn] = c;
                    voiceOn[note.LayerColumn] = layer;
                    sampleOn[note.LayerColumn] = note.Sample;
                }
            }

            // A tone portamento bends the note playing: only FM can follow, and the bent note's distance kept others off it.
            void WriteSlide(PlanCell[] cells, int c, Cell cell, List<Effect> effects)
            {
                if (on[c] is { } column && owner[column] == c && voiceOn[column].Kind == VoiceKind.Fm && cell.Note is { } target)
                {
                    cells[column] = new PlanCell(target, voiceOn[column], cell.Volume, false, effects, c, sampleOn[column]);
                }
                else
                {
                    _slides++;
                }
            }

            void Stop(PlanCell[] cells, int c)
            {
                if (on[c] is { } column && owner[column] == c && cells[column].Note is null)
                {
                    cells[column] = cells[column] with { NoteOff = true, Source = c };
                    owner[column] = null;
                }

                if (layerOn[c] is { } layer && owner[layer] == c && cells[layer].Note is null)
                {
                    cells[layer] = cells[layer] with { NoteOff = true, Source = c };
                    owner[layer] = null;
                }

                on[c] = null;
                layerOn[c] = null;
            }
        }

        /// <summary>The sound a note would take with the Genesis all to itself.</summary>
        private VoiceKind? FirstChoice(PlannedNote note) => AllKinds
            .Where(k => Allowed(k, note))
            .Cast<VoiceKind?>()
            .OrderBy(k => k is { } kind ? Distance(kind, note) : 0)
            .FirstOrDefault();

        private int StoredAs(int sample) => _storedAs.GetValueOrDefault(sample, sample);

        private static int IndexOf(List<DacSample> list, Dictionary<(int Sample, double Rate), int> index, (int Sample, double Rate) key)
        {
            if (!index.TryGetValue(key, out int i))
            {
                i = list.Count;
                list.Add(new DacSample(key.Sample, key.Rate));
                index[key] = i;
            }

            return i;
        }

        private int IndexOfPatch(List<FmPatch> list, Dictionary<FmPatch, int> index, PlannedNote note)
        {
            FmPatch patch = _menu[note.Sample].RegionAt(note.Plays[0].RateHz)?.Patch ?? FmPatchLibrary.Sine;
            if (!index.TryGetValue(patch, out int i))
            {
                i = list.Count;
                list.Add(patch);
                index[patch] = i;
            }

            return i;
        }

        private void ReportNotes()
        {
            foreach ((int sample, Dictionary<VoiceKind, int> counts) in _played.OrderBy(p => p.Key))
            {
                int total = _planned.Count(n => n.Sample == sample);
                int cut = total - counts.Values.Sum();
                if (cut > 0 || counts.Keys.Any(k => k != VoiceKind.Dac))
                {
                    _notes.Add(
                        "Sample {Sample}: of its {Total} notes, {Dac} play as recordings, {Fm} by FM, {Tone} as PSG tones, {Noise} as PSG noise, {Cut} are cut",
                        Label(sample, _song.Samples[sample].Name),
                        total,
                        counts.GetValueOrDefault(VoiceKind.Dac),
                        counts.GetValueOrDefault(VoiceKind.Fm),
                        counts.GetValueOrDefault(VoiceKind.PsgTone),
                        counts.GetValueOrDefault(VoiceKind.PsgNoise),
                        cut);
                }
            }

            foreach (IGrouping<int, PlannedNote> layered in _planned.Where(n => n.LayerColumn >= 0 && n.Kind is not null).GroupBy(n => n.Sample).OrderBy(g => g.Key))
            {
                _notes.Add("Sample {Sample}: {Count} FM notes also sound a quiet PSG tone", Label(layered.Key, _song.Samples[layered.Key].Name), layered.Count());
            }

            Tally("{Count} notes were played before their channel had an instrument; left out", _noInstrument);
            Tally("{Count} notes use an empty sample slot; left out", _silent);
            Tally("{Count} notes are on rows the song never plays; left out", _unplayed);
            Tally("{Count} tone portamentos slide a note FM isn't playing; the note holds its pitch", _slides);
            foreach ((int sample, int count) in _ignored)
            {
                _notes.Add(
                    "Sample {Sample}: ignored (--ignore-samples), so its {Count} notes are left out and their tracks are silent until the next note",
                    Label(sample, _song.Samples[sample].Name),
                    count);
            }

            foreach (int sample in _options.RequireFm.Concat(_options.RequireDac).Concat(_options.RequirePsg).Order())
            {
                if (sample >= 0 && sample < _song.Samples.Count && !_played.ContainsKey(sample))
                {
                    _notes.Add(
                        "Sample {Sample}: required as {Kind}, but it is empty or never played; the requirement has no effect",
                        Label(sample, _song.Samples[sample].Name),
                        _options.RequireFm.Contains(sample) ? "FM" : _options.RequireDac.Contains(sample) ? "DAC" : "PSG");
                }
            }
        }

        private void Tally(string template, int count)
        {
            if (count > 0)
            {
                _notes.Add(template, count);
            }
        }

        private static bool IsFlow(Effect effect) =>
            effect is PositionJump or PatternBreak or PatternLoopStart or PatternLoop or PatternDelay or SetSpeed or SetTempo or StopSong;

        private static string Label(int sample, string name) =>
            string.Create(CultureInfo.InvariantCulture, $"{sample + 1} \"{name}\"");
    }
}
