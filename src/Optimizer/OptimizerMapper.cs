using System.Globalization;
using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;
using Mod2Xgm.Render;

namespace Mod2Xgm.Optimizer;

/// <summary>
/// The greedy allocator (plan T9.3, docs/adr/0087-greedy-allocator.md, docs/multi-pass.md#passes): section by section (one
/// order each), the most important tracks (a sample on a channel) choose first, each taking the sound closest to the
/// MOD for its notes. A note whose sound has no room takes the next best; with none left it is cut. A sample with no
/// steady pitch never takes a tuned voice unless the user requires it, so its notes can be cut while tuned voices sit
/// free (David: it plays as wrong notes). <c>--require-*</c> samples only ever use their kind. An FM note may also sound a
/// quiet PSG tone, or a chord a second FM voice, on a channel no note needs (docs/adr/0088-layering.md, docs/adr/0089-fm-layer.md).
/// A recording that finds every PCM voice busy is first mixed with one already playing, so both share its voice
/// (docs/adr/0094-pre-mixed-voice-steals.md).
/// </summary>
/// <param name="limits">What the target holds.</param>
/// <param name="menu">What each sample can sound like.</param>
/// <param name="shrink">How recordings are shrunk to fit before notes give them up (plan T9.4a); <see cref="ShrinkRules.Default"/> when not given.</param>
/// <param name="switching">How a sound keeps its voice through the song (plan T9.4c); the converter's choice when not given.</param>
/// <param name="preMix">How recordings that find no PCM voice are pre-mixed (plan T9.4b); <see cref="PreMixRules.Default"/> when not given.</param>
/// <param name="layers">False: no note gets a second voice under it (docs/adr/0088-layering.md), for the listening rounds that compare the two (plan T9.4l).</param>
/// <param name="pan">How pan swaps the MOD doesn't make are priced (plan T9.4r, docs/adr/0109-pan-swap-cost.md); <see cref="PanRules.Default"/> when not given.</param>
/// <param name="levels">How much quieter the plan plays on XGM and XGM2, a retry's rule (plan T9.4s2, docs/adr/0112-retry-list.md); <see cref="LevelRules.None"/> when not given.</param>
/// <param name="keep">Which tracks choose their sound first, a squeeze round's rule (plan T9.4t1, docs/adr/0113-squeeze-rounds.md); <see cref="KeepFirst.Importance"/> when not given.</param>
/// <param name="bake">How recording bytes are shared out and which drum bakes may be used (plan T9.4t10, docs/adr/0115-drum-bakes.md); <see cref="BakeRules.None"/> when not given.</param>
internal sealed class OptimizerMapper(TargetLimits limits, IReadOnlyList<SampleSounds> menu, ShrinkRules? shrink = null, VoiceSwitching? switching = null, PreMixRules? preMix = null, bool layers = true, PanRules? pan = null, LevelRules? levels = null, KeepFirst keep = KeepFirst.Importance, BakeRules? bake = null) : IChannelMapper
{
    /// <summary>What a fully percussive sample's notes add to their importance, as a multiple of a note with no hit: a replaced attack is easy to hear, and David chose this over smaller values (docs/sample-capacity.md#importance).</summary>
    private const double PercussiveBoost = 8;

    /// <summary>A sample counts as a drum for <see cref="KeepFirst"/> when it hits at least this much (<see cref="SampleSounds.Percussive"/>, 0–1).</summary>
    private const double DrumPercussive = 0.5;

    /// <summary>The most of a note's length that counts towards its importance: a drum is as short as the long notes it would otherwise lose to are long (docs/sample-capacity.md#importance).</summary>
    private const double MaxHeardSeconds = 0.25;

    /// <summary>Importance of a note on a bar's first row (<see cref="RowsPerBar"/>) against any other (docs/sample-capacity.md#importance).</summary>
    private const double DownbeatBoost = 1.5;

    /// <summary>Rows between downbeats: MOD patterns of 64 rows are almost always four bars of 16.</summary>
    private const int RowsPerBar = 16;

    /// <summary>How much closer another sound must be, in dB of log-mel distance, for a track to change sound between sections (David: sticky).</summary>
    private const double SwitchCostDb = 3;

    /// <summary>
    /// A recording whose MOD volume stays this far below the volume it started at, to its note's end, is cut: the DAC can't
    /// follow a fade, and full volume is further from a note faded 18 dB or more than silence is (docs/adr/0095-voices-stop-with-the-mod-note.md).
    /// </summary>
    private const double FadedOutDb = 18;

    /// <summary>What a recording or a PSG tone costs a note that slides or wobbles, in dB: they hold their starting pitch and FM doesn't (David).</summary>
    private const double BentCostDb = 12;

    /// <summary>A PSG tone's level under an FM note, as a factor on what it would play at alone: 12 dB down, David's pick over 6 dB, which stuttered on hawks_claw and was too crisp on kc (docs/adr/0088-layering.md).</summary>
    internal const double ToneLayerLevel = 0.25;

    /// <summary>Where the DAC plays when recordings from both sides sound at once: both speakers, David's pick over the louder side in round 12 (docs/adr/0091-shrinking-recordings.md).</summary>
    private const DacPanClash PanClash = DacPanClash.Centre;

    /// <summary>How a sound keeps its voice through the song: in the song's order, keeping the voice it has, David's pick in rounds 13–15 (docs/adr/0092-stable-voices.md).</summary>
    private const VoiceSwitching Switching = VoiceSwitching.SongRank;

    /// <summary>The most recordings (or pre-mixes) a note tries to be mixed with before it gives up its recording: each try renders a mix.</summary>
    private const int MaxMixTries = 4;

    /// <summary>A distance for a sound with no measurement: as far as the ruler goes.</summary>
    private const double UnknownDb = NoteScorer.RangeDb;

    /// <summary>The order sounds are tried in when they tie: the least changed first.</summary>
    private static readonly VoiceKind[] AllKinds = [VoiceKind.Dac, VoiceKind.Fm, VoiceKind.PsgTone, VoiceKind.PsgNoise];

    /// <summary>
    /// Notes the last <see cref="Map"/> played with anything but their first choice, or cut, because of a limit: what
    /// <c>--no-consolidate</c> refuses (docs/adr/0087-greedy-allocator.md).
    /// </summary>
    public int NotesMoved { get; private set; }

    /// <summary>Each sample's FM confidence in the last <see cref="Map"/>, by sample number (docs/adr/0096-fm-confidence.md).</summary>
    public IReadOnlyList<SampleConfidence> Confidence { get; private set; } = [];

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

        PreMixRules mixing = preMix ?? PreMixRules.Default;
        long reserve = 0;
        NotesMoved = 0;
        Confidence = [];
        if (mixing is { Enabled: true, MakeRoom: true } && limits.PcmBytes is not null)
        {
            // A first, silent pass finds the bytes the pre-mixes want; the shrinker then leaves them that room.
            Run probe = new(limits, menu, song, playback, options, new ConversionNotes(Serilog.Core.Logger.None), shrink ?? ShrinkRules.Default, switching ?? Switching, mixing, layers, 0, pan ?? PanRules.Default, levels ?? LevelRules.None, keep, bake ?? BakeRules.None);
            try
            {
                probe.Execute();
            }
            finally
            {
                // The same as the run below finds, and kept if the song can't fit (docs/adr/0096-fm-confidence.md).
                Confidence = probe.Confidence;
            }

            reserve = probe.MixBytes + probe.RefusedMixBytes;
        }

        Run run = new(limits, menu, song, playback, options, notes, shrink ?? ShrinkRules.Default, switching ?? Switching, mixing, layers, reserve, pan ?? PanRules.Default, levels ?? LevelRules.None, keep, bake ?? BakeRules.None);
        try
        {
            return run.Execute();
        }
        finally
        {
            // Kept even when the song can't fit, so the report can show it (docs/adr/0096-fm-confidence.md).
            NotesMoved = run.Moved;
            Confidence = run.Confidence;
        }
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

        /// <summary>The closest sound it was allowed, which it lost to a limit when <see cref="Kind"/> differs.</summary>
        public VoiceKind? Closest { get; set; }

        /// <summary>The column it plays on, or −1.</summary>
        public int Column { get; set; } = -1;

        /// <summary>The first tick it sounds on.</summary>
        public int FirstTick => Plays.Min(p => p.FirstTick);

        /// <summary>True when its pitch moves while it sounds: a PSG tone couldn't follow.</summary>
        public bool Slides { get; set; }

        /// <summary>The columns of the voices that sound with it (docs/adr/0088-layering.md): a second voice, or a chord's other notes (docs/adr/0114-fm-chords.md); empty for none.</summary>
        public List<int> LayerColumns { get; } = [];

        /// <summary>True when it plays its region's chord voices (docs/adr/0114-fm-chords.md): its own voice the lowest note, <see cref="LayerColumns"/> the next ones up.</summary>
        public bool Chord { get; set; }

        /// <summary>What the second voice is, when it has one.</summary>
        public VoiceKind LayerKind { get; set; }

        /// <summary>True when its recording plays from both speakers, held in the middle so it doesn't jump sides (docs/adr/0109-pan-swap-cost.md).</summary>
        public bool Centred { get; set; }
    }

    /// <summary>Notes mixed into one recording that plays on one PCM voice (docs/adr/0094-pre-mixed-voice-steals.md).</summary>
    private sealed class MixedNotes(PlannedNote first)
    {
        /// <summary>The notes, each played once.</summary>
        public List<PlannedNote> Notes { get; } = [first];

        /// <summary>The first tick the voice plays.</summary>
        public int Start { get; set; } = first.FirstTick;

        /// <summary>The tick after the voice's last.</summary>
        public int End { get; set; } = first.FirstTick + first.Plays[0].Ticks;

        /// <summary>The mix, once made.</summary>
        public StealMix? Mix { get; set; }

        /// <summary>The note whose cell plays the mix: the first to start.</summary>
        public PlannedNote Lead => Notes.OrderBy(n => n.FirstTick).ThenBy(n => n.Channel).First();
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
        private readonly ShrinkRules _rules;
        private readonly VoiceSwitching _switching;
        private readonly HashSet<StoredCopy> _stored = [];

        /// <summary>With <c>rule:bytes=score</c>, the copies each sample (as stored) has bytes held for until every section has its sounds (docs/adr/0115-drum-bakes.md).</summary>
        private readonly Dictionary<int, HashSet<StoredCopy>> _held = [];

        /// <summary>With <c>rule:bake=beat</c>, the drum hits that play in a baked beat (docs/adr/0115-drum-bakes.md): no section allocates them.</summary>
        private readonly HashSet<PlannedNote> _baked = [];
        private Dictionary<StoredCopy, int> _copyBytes = [];
        private ShrinkResult? _shrunk;
        private readonly Dictionary<int, int> _storedAs = [];
        private long _bytes;

        private readonly List<PlannedNote> _planned = [];
        private readonly Dictionary<(int Order, int Row, int Channel), PlannedNote> _noteAt = [];
        private readonly Dictionary<int, Dictionary<VoiceKind, int>> _played = [];
        private readonly SortedDictionary<int, int> _ignored = [];
        private readonly Dictionary<(int Channel, int Sample), VoiceKind?> _lastKind = [];

        // Pre-mixes (plan T9.4b): the notes in each, the recordings once each by their bytes, and where tails start.
        private readonly PreMixRules _preMix;
        private readonly PanRules _pans;

        // How much quieter the plan plays than its own levels: a retry's rule, XGM and XGM2 only (plan T9.4s2).
        private readonly LevelRules _levels;
        private readonly KeepFirst _keep;

        /// <summary>How bytes are shared out and which drum bakes may be used (docs/adr/0115-drum-bakes.md).</summary>
        private readonly BakeRules _bake;
        private int _mixesSkipped;

        // The samples held in the middle whose recordings play one step quieter: those whose quieter copies fit (plan T9.4r).
        private readonly HashSet<int> _quieter = [];

        // What sounds on each tick, from the last PlanSwaps (plan T9.4r).
        private List<(PlannedNote Note, SongNote Play)>?[] _soundingAt = [];
        private readonly bool _layers;
        private readonly long _reserve;
        private readonly List<PlannedNote> _onDac = [];
        private readonly Dictionary<PlannedNote, MixedNotes> _mixOf = [];
        private readonly Dictionary<Fingerprint, (DacSample Sample, int Bytes, int Uses)> _mixes = [];
        private readonly Dictionary<(int Order, int Row, int Channel), MixedNotes> _tailAt = [];

        // Each track's mean note importance over the whole song, for VoiceSwitching.SongRank (plan T9.4c).
        private Dictionary<(int Channel, int Sample), double> _songImportance = [];

        private int _unplayed;
        private int _noInstrument;
        private int _silent;
        private int _slides;

        public Run(TargetLimits limits, IReadOnlyList<SampleSounds> menu, Song song, Playback playback, MappingOptions options, ConversionNotes notes, ShrinkRules rules, VoiceSwitching switching, PreMixRules preMix, bool layers, long reserve, PanRules pan, LevelRules levels, KeepFirst keep, BakeRules bake)
        {
            _pans = pan;
            _levels = levels;
            _keep = keep;
            _bake = bake;
            _preMix = preMix;
            _layers = layers;
            _reserve = reserve;
            _rules = rules;
            _switching = switching;
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

        /// <summary>Bytes the pre-mixes take.</summary>
        public long MixBytes => _mixes.Values.Sum(m => (long)m.Bytes);

        /// <summary>Bytes of pre-mixes that were made but didn't fit the budget.</summary>
        public long RefusedMixBytes { get; private set; }

        /// <summary>Each sample's FM confidence, by sample number, once the notes are collected.</summary>
        public IReadOnlyList<SampleConfidence> Confidence { get; private set; } = [];

        public GenesisPlan Execute()
        {
            Collect();
            Confidence = Confidences();
            SizeCopies();
            CheckRequired();
            BakeBeats();
            HoldBytes();
            // Each track's place in the song's order (docs/adr/0092-stable-voices.md); the whole song as one section gives each
            // track one sound throughout, where it has room.
            _songImportance = _planned.GroupBy(n => (n.Channel, n.Sample)).ToDictionary(t => t.Key, t => t.Average(n => n.Importance));
            IEnumerable<IGrouping<int, PlannedNote>> sections = _switching == VoiceSwitching.WholeSong
                ? _planned.GroupBy(_ => 0)
                : _planned.GroupBy(n => n.Order).OrderBy(g => g.Key);
            foreach (IGrouping<int, PlannedNote> section in sections)
            {
                Allocate(section);
            }

            // Bytes held for a sample that found no voice for them are free again for the rescue.
            _held.Clear();
            Rescue();
            PlanPans();
            PlanQuieterHolds();

            foreach (MixedNotes mixed in _mixOf.Values.Distinct())
            {
                if (mixed.Mix is { Tail: not null, Outlast: { } outlast } mix)
                {
                    PlaybackTick at = _playback.Ticks[mix.SwitchTick];
                    _tailAt[(at.Order, at.Row, outlast.Channel)] = mixed;
                }
            }

            AssignColumns();
            if (_layers)
            {
                AddLayers();
            }

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

        /// <summary>
        /// Each sample's FM confidence: its regions weighed by the importance of the notes the song plays in each, with its
        /// share of the song's importance and of its own in bent notes (docs/adr/0096-fm-confidence.md). A note the song
        /// plays several times shares its importance out among its plays, by how much each is heard, so each play counts
        /// in its own region and only the plays that bend count as bent.
        /// </summary>
        private SampleConfidence[] Confidences()
        {
            double total = _planned.Sum(n => n.Importance);
            ILookup<int, PlannedNote> bySample = _planned.ToLookup(n => n.Sample);
            return [.. _menu.Values.OrderBy(s => s.Sample).Select(sounds =>
            {
                double[] weights = new double[sounds.Regions.Count];
                double heard = 0;
                double bent = 0;
                foreach (PlannedNote note in bySample[sounds.Sample])
                {
                    double[] shares = [.. note.Plays.Select(p => Math.Min(p.Seconds, MaxHeardSeconds) * p.Volume)];
                    double all = shares.Sum();
                    for (int i = 0; i < note.Plays.Count; i++)
                    {
                        double importance = note.Importance * (all > 0 ? shares[i] / all : 1.0 / shares.Length);
                        if (sounds.RegionIndexAt(note.Plays[i].RateHz) is { } r)
                        {
                            weights[r] += importance;
                        }

                        bent += Bends(note.Plays[i]) ? importance : 0;
                    }

                    heard += note.Importance;
                }

                return SampleConfidence.Of(sounds, weights, total > 0 ? heard / total : 0, heard > 0 ? bent / heard : 0, _options);
            })];
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

        /// <summary>
        /// The copy each recording would store, as the capacity check counts them, shrunk where the target can't hold
        /// them all (plan T9.4a; nothing changes for a song that fits), and the size of each.
        /// </summary>
        private void SizeCopies()
        {
            // As the capacity check counts them: notes whose closest sound is a recording, not those that may only fall back to one.
            List<PlannedNote> wanted = [.. _planned.Where(n => FirstChoice(n) == VoiceKind.Dac)];
            List<((int Sample, double RateHz, int StartFrame, int VolumeSteps, NoteFade? Fade) Key, double Seconds)> copies = [];
            foreach (SongNote play in wanted.SelectMany(n => n.Plays))
            {
                copies.AddRange(CapacityCheck.Copies([play], _song, _storedAs, _limits.Target).Select(c => (c.Key, c.Value)));
            }

            // With rule:bytes=score, the samples shrunk first are those whose other sounds come closest to them (docs/adr/0115-drum-bakes.md).
            Dictionary<int, double> importance = wanted.GroupBy(n => StoredAs(n.Sample)).ToDictionary(g => g.Key, g => g.Sum(n => n.Importance * (_bake.BytesByScore ? FallbackDb(n) : 1)));
            // With MakeRoom, the shrinker leaves the pre-mixes the room a first pass found they want (PreMixRules.MakeRoom).
            TargetLimits shrinkTo = _reserve > 0 && _limits.PcmBytes is { } budget ? _limits with { PcmBytes = Math.Max(1, budget - _reserve) } : _limits;
            // A forced half speed names a sample as the recipe gives it; a twin with the same sound is stored as one copy,
            // so the copy takes it (docs/harness.md#listening-answers).
            ShrinkRules rules = _rules.ForcedHalfSpeed.Count == 0 ? _rules : _rules with { ForcedHalfSpeed = _rules.ForcedHalfSpeed.Select(StoredAs).ToHashSet() };
            _shrunk = RecordingShrinker.Shrink(copies, importance, _song, shrinkTo, rules, new HashSet<int>(_options.RequireDac.Select(StoredAs)));
            _copyBytes = new Dictionary<StoredCopy, int>(_shrunk.Bytes);
            RecordingShrinker.Report(_shrunk, _song, _rules, _notes);

            // A note that may only fall back to a recording stores its copy as the song asks for it.
            // So does a --require-dac note with no PCM voice left, so CheckRequired can say why it can't play.
            IEnumerable<SongNote> fallback = _planned.Where(n => FirstChoice(n) != VoiceKind.Dac && (Allowed(VoiceKind.Dac, n) || _options.RequireDac.Contains(n.Sample))).SelectMany(n => n.Plays);
            foreach (((int Sample, double RateHz, int StartFrame, int VolumeSteps, NoteFade? Fade) key, double seconds) in CapacityCheck.Copies(fallback, _song, _storedAs, _limits.Target))
            {
                StoredCopy copy = _shrunk.Stored.TryGetValue(key, out StoredCopy shrunk) ? shrunk : new StoredCopy(key.Sample, key.RateHz, key.StartFrame, key.VolumeSteps, false, key.Fade);
                _copyBytes[copy] = Math.Max(_copyBytes.GetValueOrDefault(copy), CapacityCheck.CopyBytes((copy.Sample, copy.RateHz, copy.StartFrame, copy.VolumeSteps, copy.Fade), seconds, _song, _limits.Target, copy.HalfSpeed));
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

            HashSet<StoredCopy> copies = [.. required.SelectMany(n => n.Plays).SelectMany(CopiesOf)];
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

            foreach (StoredCopy copy in copies)
            {
                _stored.Add(copy);
                _bytes += _copyBytes[copy];
            }
        }

        /// <summary>The copy a play is stored as, if it needs one: a note too quiet or past the sample's end stores nothing.</summary>
        private IEnumerable<StoredCopy> CopiesOf(SongNote play) =>
            CapacityCheck.Copies([play], _song, _storedAs, _limits.Target).Keys
                .Select(k => _shrunk is not null && _shrunk.Stored.TryGetValue(k, out StoredCopy copy) ? copy : new StoredCopy(k.Sample, k.RateHz, k.StartFrame, k.VolumeSteps, false, k.Fade));

        /// <summary>
        /// One section: the track whose notes matter most first (with <see cref="VoiceSwitching.SongRank"/>, most over the
        /// whole song), taking the closest sound with room for all its notes, so it doesn't change sound within the section;
        /// when none has, each note takes the first with room for it. A track keeps the sound it had while it has room
        /// (<see cref="VoiceSwitching.SongRank"/>, docs/adr/0092-stable-voices.md), or per section unless another is clearly
        /// closer (<see cref="SwitchCostDb"/>).
        /// </summary>
        private void Allocate(IEnumerable<PlannedNote> section)
        {
            bool keep = _switching == VoiceSwitching.KeepPlaying;
            bool songRank = _switching == VoiceSwitching.SongRank;
            foreach (IGrouping<(int Channel, int Sample), PlannedNote> track in section.Where(n => !_baked.Contains(n)).GroupBy(n => (n.Channel, n.Sample))

                // A track that already has a sound chooses before a newcomer, so it keeps it (plan T9.4c).
                .OrderBy(t => keep && _lastKind.GetValueOrDefault(t.Key) is not null ? 0 : 1)
                .ThenBy(t => KeptLater(t.Key.Sample))
                .ThenByDescending(t => songRank ? _songImportance[t.Key] : t.Average(n => n.Importance))
                .ThenBy(t => t.Key.Channel)
                .ThenBy(t => t.Key.Sample))
            {
                VoiceKind[] ranked = Ranked(track);
                List<PlannedNote> notes = [.. track.OrderBy(n => n.Order).ThenBy(n => n.Row)];
                VoiceKind? whole = ranked.Cast<VoiceKind?>().FirstOrDefault(k => k is { } kind && notes.All(n => Allowed(kind, n)) && HasRoomForAll(kind, notes));
                foreach (PlannedNote note in notes)
                {
                    VoiceKind[] allowed = [.. ranked.Where(k => Allowed(k, note))];
                    IEnumerable<VoiceKind> tries = whole is { } kind ? [kind] : allowed;
                    note.Closest = allowed.Length > 0 ? allowed[0] : null;
                    note.Kind = tries.Cast<VoiceKind?>().FirstOrDefault(k => k is { } tried && Take(tried, note));
                    Moved += note.Closest is not null && note.Kind == note.Closest ? 0 : 1;
                }

                // The sound the track ends the section on, so the next section keeps that one.
                _lastKind[track.Key] = notes.Select(n => n.Kind).LastOrDefault(k => k is not null) ?? _lastKind.GetValueOrDefault(track.Key);
            }
        }

        /// <summary>
        /// 0 when <paramref name="sample"/>'s tracks choose before the others under <see cref="_keep"/>, else 1; always 0 for
        /// <see cref="KeepFirst.Importance"/>, so the converter's own order is unchanged (docs/adr/0113-squeeze-rounds.md).
        /// </summary>
        private int KeptLater(int sample)
        {
            bool drum = _menu[sample].Percussive >= DrumPercussive;
            return _keep switch
            {
                KeepFirst.Drums => drum ? 0 : 1,
                KeepFirst.Tonal => drum ? 1 : 0,
                _ => 0,
            };
        }

        /// <summary>The sounds the track may use, closest first over its notes in this section.</summary>
        private VoiceKind[] Ranked(IGrouping<(int Channel, int Sample), PlannedNote> track)
        {
            VoiceKind? had = _lastKind.GetValueOrDefault(track.Key);
            double total = track.Sum(n => n.Importance);
            // Keeping its sound, a track changes only for lack of room, never because another is closer (plan T9.4c); per
            // section (and once for the whole song) it changes when another is clearly closer (SwitchCostDb).
            bool keep = _switching is not (VoiceSwitching.PerSection or VoiceSwitching.WholeSong);
            return [.. AllKinds
                .Where(k => track.Any(n => Allowed(k, n)))
                .OrderBy(k => keep && had is { } kept && kept != k ? 1 : 0)
                .ThenBy(k => track.Sum(n => n.Importance * Distance(k, n)) + (had is { } h && h != k ? total * SwitchCostDb : 0))];
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

            List<StoredCopy> fresh = [.. notes.SelectMany(n => n.Plays).SelectMany(CopiesOf).Distinct().Where(c => !_stored.Contains(c))];
            long bytes = fresh.Sum(c => (long)_copyBytes[c]);
            (long heldBytes, int heldSlots) = HeldFor(StoredAs(notes[0].Sample));
            return _stored.Count + _mixes.Count + fresh.Count + heldSlots <= _limits.PcmSlots && (_limits.PcmBytes is not { } budget || _bytes + bytes + heldBytes <= budget);
        }

        /// <summary>
        /// How far <paramref name="note"/>'s closest sound other than a recording is from it, in dB: what it loses without one.
        /// A note with no other sound loses as much as the ruler measures.
        /// </summary>
        private double FallbackDb(PlannedNote note) =>
            AllKinds.Where(k => k != VoiceKind.Dac && Allowed(k, note)).Select(k => Distance(k, note)).DefaultIfEmpty(UnknownDb).Min();

        /// <summary>
        /// With <c>rule:bytes=score</c> (plan T9.4t10, docs/adr/0115-drum-bakes.md), holds the budget's bytes for the samples
        /// that lose most per byte without their recordings: each sample's notes that want one, their importance times
        /// <see cref="FallbackDb"/>, over the bytes of the copies they need, the most first while the bytes and slots last.
        /// Other samples' notes then take only what is left, until every section has its sounds.
        /// </summary>
        private void HoldBytes()
        {
            if (!_bake.BytesByScore || _limits.PcmBytes is not { } budget)
            {
                return;
            }

            long left = budget - _bytes;
            int slots = _limits.PcmSlots - _stored.Count;
            List<string> held = [];
            foreach (IGrouping<int, PlannedNote> sample in _planned
                .Where(n => FirstChoice(n) == VoiceKind.Dac && !_baked.Contains(n))
                .GroupBy(n => StoredAs(n.Sample))
                .OrderByDescending(g => Loss(g) / Math.Max(1, Copies(g).Sum(c => (long)_copyBytes[c])))
                .ThenBy(g => g.Key))
            {
                HashSet<StoredCopy> copies = Copies(sample);
                long bytes = copies.Sum(c => (long)_copyBytes[c]);
                if (bytes > 0 && bytes <= left && copies.Count <= slots)
                {
                    _held[sample.Key] = copies;
                    left -= bytes;
                    slots -= copies.Count;
                    held.Add(Label(sample.Key, _song.Samples[sample.Key].Name));
                }
            }

            if (held.Count > 0)
            {
                _notes.Add("Recording space is held first for the samples whose other sounds fall furthest from them, per byte: {Samples}", string.Join(", ", held));
            }

            double Loss(IEnumerable<PlannedNote> notes) => notes.Sum(n => n.Importance * FallbackDb(n));

            HashSet<StoredCopy> Copies(IEnumerable<PlannedNote> notes) => [.. notes.SelectMany(n => n.Plays).SelectMany(CopiesOf).Where(c => !_stored.Contains(c))];
        }

        /// <summary>
        /// With <c>rule:bake=beat</c> (plan T9.4t10, docs/adr/0115-drum-bakes.md), when the recordings the song wants don't fit:
        /// each bar's drum hits (two or more, each played once) rendered as one recording, a pre-mix that holds one PCM voice
        /// for the bar, a bar that sounds the same as another stored once. Used only when those recordings take fewer bytes
        /// than the copies only their hits need; a bar with no PCM voice free throughout, or no bytes or slot left, keeps its
        /// hits. Its notes then play as recordings before any section chooses.
        /// </summary>
        private void BakeBeats()
        {
            if (!_bake.Beat || !_preMix.Enabled || _limits.PcmBytes is not { } budget || _limits.PcmVoices < 1)
            {
                return;
            }

            List<PlannedNote> wanted = [.. _planned.Where(n => FirstChoice(n) == VoiceKind.Dac)];
            long wantedBytes = _bytes + wanted.SelectMany(n => n.Plays).SelectMany(CopiesOf).Distinct().Where(c => !_stored.Contains(c)).Sum(c => (long)_copyBytes[c]);
            if (wantedBytes <= budget)
            {
                return;
            }

            List<(List<PlannedNote> Notes, StealMix Mix)> beats = [];
            foreach (IGrouping<(int Order, int Bar), PlannedNote> bar in wanted
                .Where(n => _menu[n.Sample].Percussive >= DrumPercussive && n.Plays.Count == 1)
                .GroupBy(n => (n.Order, Bar: n.Row / RowsPerBar))
                .Where(g => g.Count() >= 2)
                .OrderBy(g => g.Min(n => n.FirstTick)))
            {
                List<PlannedNote> notes = [.. bar.OrderBy(n => n.FirstTick).ThenBy(n => n.Channel)];
                if (StealMixer.Beat(_song, _playback, [.. notes.Select(n => n.Plays[0])], _limits.Target, _preMix) is { } mix)
                {
                    beats.Add((notes, mix));
                }
            }

            HashSet<PlannedNote> covered = [.. beats.SelectMany(b => b.Notes)];
            HashSet<StoredCopy> stillNeeded = [.. wanted.Where(n => !covered.Contains(n)).SelectMany(n => n.Plays).SelectMany(CopiesOf)];
            long saved = covered.SelectMany(n => n.Plays).SelectMany(CopiesOf).Distinct().Where(c => !stillNeeded.Contains(c) && !_stored.Contains(c)).Sum(c => (long)_copyBytes[c]);
            long beatBytes = beats.DistinctBy(b => b.Mix.HeadKey).Sum(b => (long)StealMixer.Bytes(b.Mix.Head, _limits.Target));
            if (beats.Count == 0 || beatBytes >= saved)
            {
                if (beats.Count > 0)
                {
                    _notes.Add("Drums are not baked into beats: {Beats} bars take {BeatBytes} bytes, more than the {Bytes} their hits' own copies take", beats.Count, beatBytes, saved);
                }

                return;
            }

            int baked = 0;
            foreach ((List<PlannedNote> notes, StealMix mix) in beats)
            {
                int start = notes.Min(n => n.FirstTick);
                int end = notes.Max(n => n.FirstTick + n.Plays[0].Ticks);
                MixedNotes beat = new(notes[0]) { Start = start, End = end };
                if (!Enumerable.Range(start, end - start).All(t => HasRoom(VoiceKind.Dac, t)) || !StoreMix(beat, mix))
                {
                    continue;
                }

                beat.Notes.AddRange(notes.Skip(1));
                for (int t = start; t < end; t++)
                {
                    _dacAt[t]++;
                }

                foreach (PlannedNote note in notes)
                {
                    _mixOf[note] = beat;
                    _baked.Add(note);
                    note.Kind = VoiceKind.Dac;
                    note.Closest = VoiceKind.Dac;
                }

                baked++;
            }

            if (baked > 0)
            {
                _notes.Add("{Bars} bars of drums play as {Beats} baked beats ({Bytes} bytes) in place of their hits' own copies", baked, _mixOf.Values.Distinct().Count(), beatBytes);
            }
        }

        /// <summary>The bytes and slots held for samples other than <paramref name="storedAs"/> and not stored yet (<see cref="HoldBytes"/>).</summary>
        private (long Bytes, int Slots) HeldFor(int storedAs)
        {
            long bytes = 0;
            int slots = 0;
            foreach ((int sample, HashSet<StoredCopy> copies) in _held)
            {
                if (sample == storedAs)
                {
                    continue;
                }

                foreach (StoredCopy copy in copies.Where(c => !_stored.Contains(c)))
                {
                    bytes += _copyBytes[copy];
                    slots++;
                }
            }

            return (bytes, slots);
        }

        /// <summary>
        /// After every section has its sounds, gives back their recording to notes that lost it for want of a PCM voice
        /// (plan T9.4b, docs/adr/0094-pre-mixed-voice-steals.md), track by track in the song's order: each is mixed with
        /// recordings already playing, or takes a voice left free. No other note changes, so a song that fits is untouched. A
        /// track that plays recordings somewhere gets back every note it can; one that never does only when all its notes
        /// can, so it keeps one sound (docs/adr/0092-stable-voices.md). A rescued note gives up the FM or PSG voice it had.
        /// </summary>
        private void Rescue()
        {
            if (!_preMix.Enabled)
            {
                return;
            }

            HashSet<(int Channel, int Sample)> onDac = [.. _planned.Where(n => n.Kind == VoiceKind.Dac).Select(n => (n.Channel, n.Sample))];
            ILookup<(int Channel, int Sample), PlannedNote> tracks = _planned.ToLookup(n => (n.Channel, n.Sample));

            // A track whose closest sound was a recording somewhere wants recordings everywhere: a section that ranks it
            // after it lost its voice keeps the sound it was moved to (docs/adr/0092-stable-voices.md), which isn't closer.
            foreach ((int Channel, int Sample) key in _planned
                .Where(n => n.Closest == VoiceKind.Dac && n.Kind != VoiceKind.Dac)
                .Select(n => (n.Channel, n.Sample))
                .Distinct()
                .OrderByDescending(k => _songImportance[k])
                .ThenBy(k => k.Channel)
                .ThenBy(k => k.Sample)
                .ToList())
            {
                List<PlannedNote> lost = [.. tracks[key].Where(n => n.Kind != VoiceKind.Dac).OrderBy(n => n.FirstTick)];
                bool whole = !onDac.Contains(key);
                if (whole && lost.Any(n => !Allowed(VoiceKind.Dac, n)))
                {
                    continue;
                }

                // A track with no recording anywhere gets them for every note or none, so it keeps one sound; a note that
                // can't be rescued puts back every voice, byte and mix the ones before it took.
                Saved? saved = whole ? Save(lost) : null;
                foreach (PlannedNote note in lost.Where(n => Allowed(VoiceKind.Dac, n)))
                {
                    if (!RescueOne(note) && saved is not null)
                    {
                        Restore(saved);
                        break;
                    }
                }
            }

            // The FM and PSG voices rescued notes gave up go to notes that were cut, most important first.
            foreach (PlannedNote note in _planned.Where(n => n.Kind is null).OrderByDescending(n => n.Importance).ThenBy(n => n.FirstTick).ToList())
            {
                note.Kind = AllKinds
                    .Where(k => Allowed(k, note))
                    .OrderBy(k => k == note.Closest ? 0 : 1)
                    .ThenBy(k => Distance(k, note))
                    .Cast<VoiceKind?>()
                    .FirstOrDefault(k => k is { } kind && Take(kind, note));
                Moved -= note.Kind is not null && note.Kind == note.Closest ? 1 : 0;
            }
        }

        /// <summary>
        /// Gives <paramref name="note"/> its recording back, with a PCM voice left free or mixed with a recording already
        /// playing, and lets go of the FM or PSG voice it had. False, changing nothing, when neither fits.
        /// </summary>
        private bool RescueOne(PlannedNote note)
        {
            VoiceKind? had = note.Kind;
            if (!Take(VoiceKind.Dac, note) && !Mix(note))
            {
                return false;
            }

            if (had is { } kind)
            {
                int[] counts = Counts(kind);
                foreach (int t in note.Plays.SelectMany(p => Enumerable.Range(p.FirstTick, p.Ticks)))
                {
                    counts[t]--;
                }
            }

            Moved += (note.Closest == VoiceKind.Dac ? 0 : 1) - (note.Closest is not null && had == note.Closest ? 0 : 1);
            note.Kind = VoiceKind.Dac;
            return true;
        }

        /// <summary>
        /// Plans the side each recording is heard on (plan T9.4r, docs/adr/0109-pan-swap-cost.md): while the plan makes a pan
        /// swap the MOD doesn't make and the rules don't let stand, the instrument whose swaps matter most takes the cheapest
        /// fix: its recordings held in the middle, or it or an instrument it clashes with played with FM. Each fix holds more
        /// notes in the middle or takes more off the DAC, so it ends. Only XGM and XGM2 share one DAC pan; a DMF is left as it is.
        /// </summary>
        private void PlanPans()
        {
            if (!_pans.Active || _dmf)
            {
                ReportSkippedMixes();
                return;
            }

            Dictionary<int, double> importance = _planned.GroupBy(n => n.Sample).ToDictionary(g => g.Key, g => g.Sum(n => n.Importance));
            HashSet<int> stuck = [];
            SortedDictionary<int, int> centred = [];
            SortedDictionary<int, int> moved = [];
            List<PanSwap> left;
            for (int round = 0; round < (4 * _menu.Count) + 8; round++)
            {
                left = [.. PlanSwaps().Swaps.Where(Unfixed)];
                IGrouping<int, PanSwap>? worst = left
                    .Where(s => !stuck.Contains(s.Sample))
                    .GroupBy(s => s.Sample)
                    .OrderByDescending(g => importance.GetValueOrDefault(g.Key))
                    .ThenBy(g => g.Key)
                    .FirstOrDefault();
                if (worst is null)
                {
                    break;
                }

                if (!FixPan(worst.Key, [.. worst], centred, moved))
                {
                    stuck.Add(worst.Key);
                }
            }

            left = [.. PlanSwaps().Swaps.Where(Unfixed)];
            foreach ((int sample, int notes) in centred)
            {
                _notes.Add("Sample {Sample} plays from both speakers in {Notes} notes, held in the middle so it doesn't jump between sides", Label(sample, _song.Samples[sample].Name), notes);
            }

            foreach ((int sample, int notes) in moved)
            {
                _notes.Add("Sample {Sample} plays FM in place of its recording in {Notes} notes, so the recordings it clashed with don't jump between sides", Label(sample, _song.Samples[sample].Name), notes);
            }

            ReportSkippedMixes();
            if (left.Count > 0)
            {
                _notes.Add("{Count} pan swaps the MOD doesn't make are left: no fix had room", left.Count);
            }

            // A recording held in the middle all song that also plays FM notes on its side still jumps; with the hold per
            // order, its trips back to its side between orders are the rule's own.
            bool Unfixed(PanSwap s) =>
                (s.Cause is PanCause.SharedDac or PanCause.PreMix or PanCause.Psg || (s.Cause == PanCause.SteadyMiddle && _pans.Span == SteadyMiddleSpan.Song)) && !_pans.Free(s);
        }

        /// <summary>
        /// Lets each sample held in the middle play one step quieter, most important first, where its quieter copies fit the
        /// target's slots and bytes: they are other copies than the ones the shrinker sized (docs/adr/0109-pan-swap-cost.md).
        /// A sample they don't fit stays held at its own level, and a conversion note says so.
        /// </summary>
        private void PlanQuieterHolds()
        {
            Dictionary<int, double> importance = _planned.Where(n => n.Kind == VoiceKind.Dac && Held(n))
                .GroupBy(n => n.Sample)
                .ToDictionary(g => g.Key, g => g.Sum(n => n.Importance));
            foreach (int sample in importance.Keys.OrderByDescending(s => importance[s]).ThenBy(s => s))
            {
                _quieter.Add(sample);
                Dictionary<StoredCopy, int> used = UsedCopies();
                if (used.Count + _mixes.Count > _limits.PcmSlots || (_limits.PcmBytes is { } budget && used.Values.Sum(b => (long)b) + MixBytes > budget))
                {
                    _quieter.Remove(sample);
                    _notes.Add("Sample {Sample} plays at its own level from both speakers: its copies one step quieter don't fit", Label(sample, _song.Samples[sample].Name));
                }
            }
        }

        /// <summary>
        /// The stored copies the plan's recordings play, with their bytes: a note held in the middle, of a sample in
        /// <see cref="_quieter"/>, plays its copy one step down. A copy the allocator counted that no note plays stays counted,
        /// unless only such held notes played it.
        /// </summary>
        private Dictionary<StoredCopy, int> UsedCopies()
        {
            Dictionary<StoredCopy, int> used = [];
            HashSet<StoredCopy> vacated = [];
            // A baked beat's hits play its pre-mix, not copies of their own (docs/adr/0115-drum-bakes.md).
            foreach (PlannedNote note in _planned.Where(n => n.Kind == VoiceKind.Dac && !_baked.Contains(n)))
            {
                bool quieter = _quieter.Contains(note.Sample) && Held(note);
                foreach (((int Sample, double RateHz, int StartFrame, int VolumeSteps, NoteFade? Fade) key, _) in note.Plays.SelectMany(p => CapacityCheck.Copies([p], _song, _storedAs, _limits.Target)))
                {
                    StoredCopy copy = _shrunk is not null && _shrunk.Stored.TryGetValue(key, out StoredCopy shrunk) ? shrunk : new StoredCopy(key.Sample, key.RateHz, key.StartFrame, key.VolumeSteps, false, key.Fade);
                    int bytes = _copyBytes.GetValueOrDefault(copy);
                    if (quieter)
                    {
                        vacated.Add(copy);
                        copy = Quieter(key.VolumeSteps, key.Sample, copy);
                    }

                    used[copy] = Math.Max(used.GetValueOrDefault(copy), bytes);
                }
            }

            foreach (StoredCopy copy in _stored.Where(c => !vacated.Contains(c)))
            {
                used.TryAdd(copy, _copyBytes.GetValueOrDefault(copy));
            }

            return used;
        }

        /// <summary>
        /// The copy a held note plays one step quieter, as the Genesis player picks it: the note's own steps plus one, less a
        /// merged look-alike's gain, at the shrinker's loudness share (<see cref="RecordingShrinker.StoredSteps"/>).
        /// </summary>
        private StoredCopy Quieter(int steps, int sample, StoredCopy copy)
        {
            double gainDb = copy.Sample == sample ? 0 : _shrunk?.MergeGainDb.GetValueOrDefault(sample) ?? 0;
            bool loudness = _shrunk?.Steps.Any(s => s.Move == ShrinkMove.ShareLoudness && s.Sample == copy.Sample) == true;
            return copy with { VolumeSteps = RecordingShrinker.StoredSteps(steps + 1, gainDb, loudness ? _rules.LoudnessShareSteps : 1) };
        }

        /// <summary>Adds the conversion note for the pre-mixes <see cref="SkipsMix"/> refused, if any.</summary>
        private void ReportSkippedMixes()
        {
            if (_mixesSkipped > 0)
            {
                _notes.Add("{Count} pre-mixes of notes from both sides not made, so their instruments don't jump between sides", _mixesSkipped);
            }
        }

        /// <summary>Takes the cheapest fix for <paramref name="sample"/>'s <paramref name="swaps"/>; false when none can be made.</summary>
        private bool FixPan(int sample, List<PanSwap> swaps, SortedDictionary<int, int> centred, SortedDictionary<int, int> moved)
        {
            List<(double Cost, int Rank, int Sample, bool Middle)> fixes = [];
            if (_pans.Fixes.HasFlag(PanFixes.SteadyMiddle) && MiddleNotes(sample, swaps) is { Count: > 0 } middle)
            {
                fixes.Add((_pans.OffSideDb * middle.Where(n => SideOf(n) != 0).Sum(n => n.Importance), 0, sample, true));
            }

            if (_pans.Fixes.HasFlag(PanFixes.MoveToFm))
            {
                foreach (int other in Culprits(sample, swaps).Prepend(sample).Distinct())
                {
                    if (FmCost(other) is { } cost)
                    {
                        fixes.Add((cost, 1, other, false));
                    }
                }
            }

            foreach ((double _, int _, int which, bool hold) in fixes.OrderBy(f => f.Cost).ThenBy(f => f.Rank).ThenBy(f => f.Sample))
            {
                if (hold)
                {
                    List<PlannedNote> notes = MiddleNotes(which, swaps);
                    notes.ForEach(n => n.Centred = true);
                    centred[which] = centred.GetValueOrDefault(which) + notes.Count;
                    return true;
                }

                if (MoveToFm(which, out int count))
                {
                    moved[which] = moved.GetValueOrDefault(which) + count;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The recordings of <paramref name="sample"/> a hold in the middle takes: every one not yet held, or, with
        /// <see cref="SteadyMiddleSpan.Order"/>, those in the orders where <paramref name="swaps"/> keep it off its side.
        /// </summary>
        private List<PlannedNote> MiddleNotes(int sample, List<PanSwap> swaps)
        {
            IEnumerable<PlannedNote> notes = _planned.Where(n => n.Sample == sample && n is { Kind: VoiceKind.Dac, Centred: false });
            if (_pans.Span == SteadyMiddleSpan.Song)
            {
                return [.. notes];
            }

            HashSet<int> orders = [.. swaps.SelectMany(OffTicks).Select(t => _playback.Ticks[t].Order)];
            return [.. notes.Where(n => orders.Contains(n.Order))];
        }

        /// <summary>The other samples whose recordings sound while <paramref name="sample"/> is off its side by the shared DAC pan.</summary>
        private IEnumerable<int> Culprits(int sample, List<PanSwap> swaps) =>
            swaps.Where(s => s.Cause == PanCause.SharedDac)
                .SelectMany(OffTicks)
                .Distinct()
                .SelectMany(t => _soundingAt[t] ?? [])
                .Where(x => x.Note.Kind == VoiceKind.Dac && x.Note.Sample != sample)
                .Select(x => x.Note.Sample)
                .Distinct()
                .Order();

        /// <summary>The ticks <paramref name="swap"/>'s instrument spends off its side: after it for a swap away or across, before it for a swap back.</summary>
        private IEnumerable<int> OffTicks(PanSwap swap)
        {
            double end = swap.Kind == PanSwapKind.Back ? swap.Seconds - swap.GapSeconds : swap.Seconds + swap.OffSeconds;
            double start = swap.Kind == PanSwapKind.Back ? end - swap.OffSeconds : swap.Seconds;
            for (int t = TickAt(start); t < _playback.Ticks.Count && _playback.Ticks[t].TimeSeconds < end - 1e-9; t++)
            {
                yield return t;
            }
        }

        /// <summary>The tick that plays at <paramref name="seconds"/>.</summary>
        private int TickAt(double seconds)
        {
            int low = 0;
            int high = _playback.Ticks.Count - 1;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (_playback.Ticks[mid].TimeSeconds <= seconds + 1e-9)
                {
                    low = mid;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return Math.Max(0, low);
        }

        /// <summary>
        /// What moving every recording of <paramref name="sample"/> to FM costs: its notes' importance times how much further
        /// FM is from them. <c>null</c> when it can't: a note may not take FM, or is in a pre-mix.
        /// </summary>
        private double? FmCost(int sample)
        {
            List<PlannedNote> notes = [.. _planned.Where(n => n.Sample == sample && n.Kind == VoiceKind.Dac)];
            if (notes.Count == 0 || notes.Any(n => !Allowed(VoiceKind.Fm, n) || _mixOf.ContainsKey(n)))
            {
                return null;
            }

            return notes.Sum(n => n.Importance * (Distance(VoiceKind.Fm, n) - Distance(VoiceKind.Dac, n)));
        }

        /// <summary>Plays every recording of <paramref name="sample"/> with FM; false, changing nothing, when FM has no room for them all.</summary>
        private bool MoveToFm(int sample, out int count)
        {
            List<PlannedNote> notes = [.. _planned.Where(n => n.Sample == sample && n.Kind == VoiceKind.Dac)];
            count = notes.Count;
            Saved saved = Save(notes);
            foreach (PlannedNote note in notes)
            {
                foreach (int t in note.Plays.SelectMany(p => Enumerable.Range(p.FirstTick, p.Ticks)))
                {
                    _dacAt[t]--;
                }

                _onDac.Remove(note);
            }

            foreach (PlannedNote note in notes)
            {
                if (!Take(VoiceKind.Fm, note))
                {
                    Restore(saved);
                    return false;
                }

                note.Kind = VoiceKind.Fm;
            }

            return true;
        }

        /// <summary>
        /// True when the pan rules refuse to mix <paramref name="note"/> into <paramref name="mixed"/>: the mix would hold
        /// notes from different sides and play centred, and keeping the note's own sound costs less than holding their
        /// instruments in the middle (docs/adr/0109-pan-swap-cost.md).
        /// </summary>
        private bool SkipsMix(MixedNotes mixed, PlannedNote note)
        {
            if (!_pans.Active || _dmf || !_pans.Fixes.HasFlag(PanFixes.SkipPreMix))
            {
                return false;
            }

            List<PlannedNote> all = [.. mixed.Notes, note];
            if (all.Select(SideOf).Distinct().Count() < 2)
            {
                return false;
            }

            if (!_pans.Fixes.HasFlag(PanFixes.SteadyMiddle))
            {
                return true;
            }

            double skip = note.Importance * ((note.Kind is { } kind ? Distance(kind, note) : UnknownDb) - Distance(VoiceKind.Dac, note));
            double held = all.Where(n => SideOf(n) != 0).Select(n => n.Sample).Distinct()
                .Sum(s => _planned.Where(n => n.Sample == s && n is { Kind: VoiceKind.Dac, Centred: false } && (_pans.Span == SteadyMiddleSpan.Song || n.Order == note.Order)).Sum(n => n.Importance));
            return skip < _pans.OffSideDb * (held + note.Importance);
        }

        /// <summary>The side the MOD plays <paramref name="note"/> on when it starts: −1 left, 0 both, +1 right.</summary>
        private int SideOf(PlannedNote note) => PlanVoicing.Side(_playback.Ticks[note.FirstTick].Channels[note.Channel].Pan);

        /// <summary>True when <paramref name="note"/>'s recording plays from both speakers: held in the middle, or in a pre-mix that is.</summary>
        private bool Held(PlannedNote note) => note.Centred || (_mixOf.TryGetValue(note, out MixedNotes? mixed) && mixed.Notes.Any(n => n.Centred));

        /// <summary>True when <paramref name="note"/> is in a pre-mix of notes from both sides, which plays centred.</summary>
        private bool InCentredMix(PlannedNote note) => _mixOf.TryGetValue(note, out MixedNotes? mixed) && mixed.Mix?.Head.Mix is { Centred: true };

        /// <summary>
        /// The pan swaps the plan makes, worked out as the Genesis player plays it (<c>GenesisPlanPlayer</c>): each tick, the
        /// recordings sounding share the DAC's side when they agree and play from both speakers when they don't; FM keeps its
        /// own side and the PSG plays from both. Fills <see cref="_soundingAt"/>.
        /// </summary>
        private PanSwapCount PlanSwaps()
        {
            int ticks = _playback.Ticks.Count;
            _soundingAt = new List<(PlannedNote Note, SongNote Play)>?[ticks];
            foreach (PlannedNote note in _planned.Where(n => n.Kind is not null))
            {
                foreach (SongNote play in note.Plays)
                {
                    // A recording whose MOD volume fades out for good is cut there (docs/adr/0095-voices-stop-with-the-mod-note.md).
                    int end = Math.Min(ticks, (note.Kind == VoiceKind.Dac ? FadedOut(play) : null) ?? (play.FirstTick + play.Ticks));
                    for (int t = play.FirstTick; t < end; t++)
                    {
                        (_soundingAt[t] ??= []).Add((note, play));
                    }
                }
            }

            PanLog log = new();
            List<int> sides = [];
            for (int t = 0; t < ticks; t++)
            {
                if (_soundingAt[t] is not { } here)
                {
                    continue;
                }

                PlaybackTick tick = _playback.Ticks[t];
                sides.Clear();
                sides.AddRange(here.Where(x => x.Note.Kind == VoiceKind.Dac).Select(x => Held(x.Note) || InCentredMix(x.Note) ? 0 : PlanVoicing.Side(tick.Channels[x.Play.Channel].Pan)));
                int dac = sides.Count > 0 && sides.TrueForAll(s => s == sides[0]) ? sides[0] : 0;
                foreach ((PlannedNote note, SongNote play) in here)
                {
                    ChannelTick ch = tick.Channels[play.Channel];
                    int original = PlanVoicing.Side(ch.Pan);
                    (int heard, PanCause cause) = note.Kind switch
                    {
                        VoiceKind.Fm => (original, PanCause.None),
                        VoiceKind.Dac when Held(note) => (0, PanCause.SteadyMiddle),
                        VoiceKind.Dac when InCentredMix(note) => (0, PanCause.PreMix),
                        VoiceKind.Dac => (dac, PanCause.SharedDac),
                        _ => (0, PanCause.Psg),
                    };
                    log.Add(new PanTick(t, tick.TimeSeconds, tick.DurationSeconds, note.Sample, play.Channel, original, heard, heard == original ? PanCause.None : cause, ch.Volume));
                }
            }

            return PanSwaps.Count(log);
        }

        /// <summary>What a rescue can change (not the bytes refused mixes wanted), so a track that can't be rescued whole is put back as it was.</summary>
        private sealed record Saved(
            int[] DacAt,
            int[] FmAt,
            int[] ToneAt,
            int[] NoiseAt,
            List<StoredCopy> Stored,
            long Bytes,
            List<KeyValuePair<Fingerprint, (DacSample Sample, int Bytes, int Uses)>> Mixes,
            List<PlannedNote> OnDac,
            List<KeyValuePair<PlannedNote, MixedNotes>> MixOf,
            List<(MixedNotes Group, List<PlannedNote> Notes, int Start, int End, StealMix? Mix)> Groups,
            List<(PlannedNote Note, VoiceKind? Kind)> Kinds,
            int Moved);

        /// <summary>Saves what rescuing <paramref name="notes"/> can change (<see cref="Restore"/>).</summary>
        private Saved Save(List<PlannedNote> notes) => new(
            [.. _dacAt],
            [.. _fmAt],
            [.. _toneAt],
            [.. _noiseAt],
            [.. _stored],
            _bytes,
            [.. _mixes],
            [.. _onDac],
            [.. _mixOf],
            [.. _mixOf.Values.Distinct().Select(m => (m, m.Notes.ToList(), m.Start, m.End, m.Mix))],
            [.. notes.Select(n => (n, n.Kind))],
            Moved);

        /// <summary>Puts back what <paramref name="saved"/> holds.</summary>
        private void Restore(Saved saved)
        {
            saved.DacAt.CopyTo(_dacAt, 0);
            saved.FmAt.CopyTo(_fmAt, 0);
            saved.ToneAt.CopyTo(_toneAt, 0);
            saved.NoiseAt.CopyTo(_noiseAt, 0);
            _stored.Clear();
            _stored.UnionWith(saved.Stored);
            _bytes = saved.Bytes;
            _mixes.Clear();
            foreach ((Fingerprint key, (DacSample Sample, int Bytes, int Uses) value) in saved.Mixes)
            {
                _mixes[key] = value;
            }

            _onDac.Clear();
            _onDac.AddRange(saved.OnDac);
            _mixOf.Clear();
            foreach ((PlannedNote note, MixedNotes group) in saved.MixOf)
            {
                _mixOf[note] = group;
            }

            foreach ((MixedNotes group, List<PlannedNote> notes, int start, int end, StealMix? mix) in saved.Groups)
            {
                group.Notes.Clear();
                group.Notes.AddRange(notes);
                group.Start = start;
                group.End = end;
                group.Mix = mix;
            }

            foreach ((PlannedNote note, VoiceKind? kind) in saved.Kinds)
            {
                note.Kind = kind;
            }

            // The bytes refused mixes wanted stay counted: a budget may make room for them (PreMixRules.MakeRoom).
            Moved = saved.Moved;
        }

        /// <summary>How many voices of <paramref name="kind"/> each tick has taken.</summary>
        private int[] Counts(VoiceKind kind) => kind switch
        {
            VoiceKind.Dac => _dacAt,
            VoiceKind.Fm => _fmAt,
            VoiceKind.PsgTone => _toneAt,
            _ => _noiseAt,
        };

        /// <summary>Gives <paramref name="note"/> the room it needs for <paramref name="kind"/> on every tick it sounds, if it is there.</summary>
        private bool Take(VoiceKind kind, PlannedNote note)
        {
            List<int> ticks = [.. note.Plays.SelectMany(p => Enumerable.Range(p.FirstTick, p.Ticks))];
            if (!ticks.All(t => HasRoom(kind, t)) || (kind == VoiceKind.Dac && !Store(note)))
            {
                return false;
            }

            int[] counts = Counts(kind);
            foreach (int t in ticks)
            {
                counts[t]++;
            }

            if (kind == VoiceKind.Dac && note.Plays.Count == 1)
            {
                _onDac.Add(note);
            }

            return true;
        }

        /// <summary>
        /// Mixes <paramref name="note"/>, which finds every PCM voice busy somewhere, with recordings already playing, so it
        /// shares their voice (plan T9.4b, docs/adr/0094-pre-mixed-voice-steals.md): with one playing note or pre-mix whose
        /// voice covers every busy tick, the one with fewest notes first. The mix must keep to the rules, and its bytes and
        /// slots must fit. False when none does.
        /// </summary>
        private bool Mix(PlannedNote note)
        {
            SongNote play = note.Plays[0];
            int from = play.FirstTick;
            List<MixedNotes> candidates = MixCandidates(note);
            foreach (MixedNotes mixed in candidates)
            {
                if (SkipsMix(mixed, note))
                {
                    _mixesSkipped++;
                    continue;
                }

                if (StealMixer.Make(_song, _playback, [.. mixed.Notes.Select(n => n.Plays[0]), play], _limits.Target, _preMix) is not { } mix || !StoreMix(mixed, mix))
                {
                    continue;
                }

                foreach (int t in Enumerable.Range(from, play.Ticks).Where(t => t < mixed.Start || t >= mixed.End))
                {
                    _dacAt[t]++;
                }

                mixed.Notes.Add(note);
                mixed.Start = Math.Min(mixed.Start, from);
                mixed.End = Math.Max(mixed.End, from + play.Ticks);
                foreach (PlannedNote n in mixed.Notes)
                {
                    _mixOf[n] = mixed;
                }

                _onDac.Add(note);
                return true;
            }

            return false;
        }

        /// <summary>
        /// The playing recordings <paramref name="note"/> could be mixed with, best first: fewest notes, then the longest
        /// overlap. Each covers every tick the note finds no PCM voice on and leaves room for the rest; none when the note
        /// plays more than once or every tick has a voice.
        /// </summary>
        private List<MixedNotes> MixCandidates(PlannedNote note)
        {
            if (!_preMix.Enabled || note.Plays.Count != 1)
            {
                return [];
            }

            SongNote play = note.Plays[0];
            int from = play.FirstTick;
            int to = from + play.Ticks;
            List<int> busy = [.. Enumerable.Range(from, play.Ticks).Where(t => !HasRoom(VoiceKind.Dac, t))];
            if (busy.Count == 0)
            {
                // With rule:bake=together, a drum hit with a voice free but no bytes for its own copy may still be mixed
                // with a recording that starts with it (docs/adr/0115-drum-bakes.md).
                return _bake.Together && _menu[note.Sample].Percussive >= DrumPercussive
                    ? [.. _onDac
                        .Where(n => n.Kind == VoiceKind.Dac && n.FirstTick == from && n.Plays.Count == 1)
                        .Select(n => _mixOf.GetValueOrDefault(n) ?? new MixedNotes(n))
                        .DistinctBy(m => m.Lead)
                        .Where(m => m.Notes.Count < StealMixer.MaxNotes && Enumerable.Range(from, play.Ticks).Where(t => t < m.Start || t >= m.End).All(t => HasRoom(VoiceKind.Dac, t)))
                        .OrderBy(m => m.Notes.Count)
                        .ThenBy(m => m.Start)
                        .Take(MaxMixTries)]
                    : [];
            }

            return [.. _onDac
                .Where(n => n.Kind == VoiceKind.Dac && n.FirstTick < to && from < n.FirstTick + n.Plays[0].Ticks)
                .Select(n => _mixOf.GetValueOrDefault(n) ?? new MixedNotes(n))
                .DistinctBy(m => m.Lead)
                .Where(m => m.Notes.Count < StealMixer.MaxNotes
                    && busy.All(t => t >= m.Start && t < m.End)
                    && Enumerable.Range(from, play.Ticks).Where(t => t < m.Start || t >= m.End).All(t => HasRoom(VoiceKind.Dac, t)))
                .OrderBy(m => m.Notes.Count)
                .ThenByDescending(m => Math.Min(m.End, to) - Math.Max(m.Start, from))
                .ThenBy(m => m.Start)
                .Take(MaxMixTries)];
        }

        /// <summary>
        /// Stores <paramref name="mix"/> for <paramref name="mixed"/> in place of its old one, if the slots and bytes hold it:
        /// the same bytes are stored once, and an old mix no other notes play is let go.
        /// </summary>
        private bool StoreMix(MixedNotes mixed, StealMix mix)
        {
            List<(Fingerprint Key, DacSample Sample)> wanted = [(mix.HeadKey, mix.Head)];
            if (mix.Tail is { } tail)
            {
                wanted.Add((mix.TailKey, tail));
            }

            List<Fingerprint> old = [];
            if (mixed.Mix is { } had)
            {
                old.Add(had.HeadKey);
                if (had.Tail is not null)
                {
                    old.Add(had.TailKey);
                }
            }

            List<Fingerprint> freed = [.. old.Where(k => _mixes[k].Uses == 1 && wanted.All(w => w.Key != k))];
            List<(Fingerprint Key, DacSample Sample)> fresh = [.. wanted.Where(w => !_mixes.ContainsKey(w.Key)).DistinctBy(w => w.Key)];
            long added = fresh.Sum(w => (long)StealMixer.Bytes(w.Sample, _limits.Target));
            long bytes = _bytes + added - freed.Sum(k => (long)_mixes[k].Bytes);
            if (_stored.Count + _mixes.Count - freed.Count + fresh.Count > _limits.PcmSlots || (_limits.PcmBytes is { } budget && bytes > budget))
            {
                RefusedMixBytes += added;
                return false;
            }

            foreach (Fingerprint k in old)
            {
                (DacSample sample, int size, int uses) = _mixes[k];
                if (uses == 1)
                {
                    _mixes.Remove(k);
                    _bytes -= size;
                }
                else
                {
                    _mixes[k] = (sample, size, uses - 1);
                }
            }

            foreach ((Fingerprint key, DacSample sample) in wanted)
            {
                if (_mixes.TryGetValue(key, out (DacSample Sample, int Bytes, int Uses) known))
                {
                    _mixes[key] = (known.Sample, known.Bytes, known.Uses + 1);
                }
                else
                {
                    int size = StealMixer.Bytes(sample, _limits.Target);
                    _mixes[key] = (sample, size, 1);
                    _bytes += size;
                }
            }

            // The same bytes play the one stored recording, so the plan lists it once.
            mixed.Mix = mix with { Head = _mixes[mix.HeadKey].Sample, Tail = mix.Tail is null ? null : _mixes[mix.TailKey].Sample };
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
            List<StoredCopy> fresh = [.. note.Plays.SelectMany(CopiesOf).Distinct().Where(c => !_stored.Contains(c))];
            long bytes = fresh.Sum(c => (long)_copyBytes[c]);
            (long heldBytes, int heldSlots) = HeldFor(StoredAs(note.Sample));
            if (_stored.Count + _mixes.Count + fresh.Count + heldSlots > _limits.PcmSlots || (_limits.PcmBytes is { } budget && _bytes + bytes + heldBytes > budget))
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
                // A pre-mix holds one column from its first note's start to its last note's end; its first note places it.
                MixedNotes? mixed = _mixOf.GetValueOrDefault(note);
                if (mixed is not null && mixed.Lead != note)
                {
                    continue;
                }

                List<(int From, int To)> spans = mixed is null ? [.. note.Plays.Select(p => (p.FirstTick, p.FirstTick + p.Ticks))] : [(mixed.Start, mixed.End)];
                int column = ColumnsFor(note, onChannel[note.Channel]).FirstOrDefault(c => !spans.Any(s => busy[c].Any(b => s.From < b.To && b.From < s.To)), -1);
                List<PlannedNote> together = mixed?.Notes ?? [note];
                if (column < 0)
                {
                    foreach (PlannedNote cut in together)
                    {
                        cut.Kind = null;
                        Moved++;
                    }

                    continue;
                }

                foreach (PlannedNote placed in together)
                {
                    placed.Column = column;
                }

                busy[column].AddRange(spans);
                onChannel[note.Channel] = column;
            }
        }

        /// <summary>
        /// Gives second voices to the notes that can take one (docs/adr/0088-layering.md), after every note has
        /// its own voice, so a layer never costs a note anything. Most important tracks first, section by section; a track
        /// takes layers only when every note it has in the section can, so the tone doesn't come and go note by note: on
        /// hawks_claw David heard that as a stutter, on a sample playing as a mix of FM and recordings. A chord's notes take
        /// FM voices of their own first (docs/adr/0114-fm-chords.md): as many of its notes as every note of the track has free
        /// FM columns for, the top ones dropped first (David, 2026-11-15), or with none free, the layers it would have had.
        /// </summary>
        private void AddLayers()
        {
            List<int>[] toneOn = [.. _song.Channels.Select(_ => new List<int>())];
            List<int>[] fmOn = [.. _song.Channels.Select(_ => new List<int>())];
            foreach (IGrouping<int, PlannedNote> section in _planned.GroupBy(n => n.Order).OrderBy(g => g.Key))
            {
                foreach (IGrouping<(int Channel, int Sample), PlannedNote> track in section.GroupBy(n => (n.Channel, n.Sample))
                    .OrderByDescending(t => t.Average(n => n.Importance))
                    .ThenBy(t => t.Key.Channel)
                    .ThenBy(t => t.Key.Sample))
                {
                    List<PlannedNote> notes = [.. track.OrderBy(n => n.FirstTick)];
                    int channel = track.Key.Channel;
                    if (notes.All(n => ChordOf(n) is not null) && PlaceChord(notes, fmOn[channel]) is { } chord)
                    {
                        fmOn[channel] = chord;
                    }
                    else if (notes.All(TakesSecondFm))
                    {
                        fmOn[channel] = PlaceLayers(notes, VoiceKind.Fm, fmOn[channel], 1) ?? fmOn[channel];
                    }
                    else if (notes.All(TakesLayer))
                    {
                        toneOn[channel] = PlaceLayers(notes, VoiceKind.PsgTone, toneOn[channel], 1) ?? toneOn[channel];
                    }
                }
            }
        }

        /// <summary>
        /// Gives <paramref name="notes"/>, a track's notes of a chord, FM columns for as many of the chord's other notes as
        /// every one of them has room for, the top notes left out first (docs/adr/0114-fm-chords.md). Returns the columns the
        /// channel used last, or <c>null</c>, changing nothing, when not even one more note has room.
        /// </summary>
        private List<int>? PlaceChord(List<PlannedNote> notes, List<int> last)
        {
            for (int count = notes.Min(n => ChordOf(n)!.Tones.Count) - 1; count >= 1; count--) // Every note has a chord: AddLayers checked.
            {
                if (PlaceLayers(notes, VoiceKind.Fm, last, count) is { } used)
                {
                    notes.ForEach(n => n.Chord = true);
                    return used;
                }
            }

            return null;
        }

        /// <summary>
        /// Gives every one of <paramref name="notes"/> <paramref name="count"/> free columns of <paramref name="kind"/>, staying
        /// on those the channel last used (<paramref name="last"/>), or leaves them all without and returns <c>null</c>. FM6
        /// stays free as the first DAC voice's only column. Returns the columns the channel used last.
        /// </summary>
        private List<int>? PlaceLayers(List<PlannedNote> notes, VoiceKind kind, List<int> last, int count)
        {
            int[] columns = kind == VoiceKind.Fm
                ? [.. Enumerable.Range(0, Math.Min(_limits.FmChannels, (int)GenesisChannel.Fm6))]
                : [.. Enumerable.Range((int)GenesisChannel.Psg1, _limits.PsgTones)];
            List<(PlannedNote Note, List<int> Columns, List<(int From, int To)> Spans)> placed = [];
            List<int> previous = last;
            foreach (PlannedNote note in notes)
            {
                List<(int From, int To)> spans = [.. note.Plays.Select(p => (p.FirstTick, p.FirstTick + p.Ticks))];
                List<int> stay = previous;
                List<int> chosen = [];
                for (int i = 0; i < count; i++)
                {
                    int column = columns
                        .OrderBy(c => stay.IndexOf(c) is var at && at >= 0 ? at : stay.Count)
                        .ThenBy(c => c)
                        .FirstOrDefault(c => !chosen.Contains(c) && !spans.Any(s => _busy[c].Any(b => s.From < b.To && b.From < s.To)) && !placed.Any(p => p.Columns.Contains(c) && p.Spans.Any(o => spans.Any(s => s.From < o.To && o.From < s.To))), -1);
                    if (column < 0)
                    {
                        return null;
                    }

                    chosen.Add(column);
                }

                placed.Add((note, chosen, spans));
                previous = chosen;
            }

            foreach ((PlannedNote note, List<int> chosen, List<(int From, int To)> spans) in placed)
            {
                note.LayerColumns.Clear();
                note.LayerColumns.AddRange(chosen);
                note.LayerKind = kind;
                chosen.ForEach(c => _busy[c].AddRange(spans));
            }

            return previous;
        }

        /// <summary>
        /// The chord voices <paramref name="note"/> can play (docs/adr/0114-fm-chords.md): those of its region, when it is an FM
        /// note, which the other voices can follow unless it slides (the slide's cell has one column); <c>null</c> otherwise.
        /// </summary>
        private ChordVoices? ChordOf(PlannedNote note) =>
            note is { Kind: VoiceKind.Fm, Column: >= 0, Slides: false } ? _menu[note.Sample].RegionAt(note.Plays[0].RateHz)?.Chord : null;

        /// <summary>True when <paramref name="note"/> can take a tone under it: an FM note with a steady pitch the tone channels reach. A required sample keeps to its one kind, and a tone can't follow a slide.</summary>
        private bool TakesLayer(PlannedNote note) =>
            note is { Kind: VoiceKind.Fm, Column: >= 0, Slides: false }
            && !_options.RequireFm.Contains(note.Sample)
            && _menu[note.Sample].SteadyPitch
            && _menu[note.Sample].RegionAt(note.Plays[0].RateHz)?.ToneDb is not null;

        /// <summary>True when <paramref name="note"/> can take a second FM voice: an FM note of a chord, which the voice can follow unless it slides (the slide's cell has one column).</summary>
        private bool TakesSecondFm(PlannedNote note) =>
            note is { Kind: VoiceKind.Fm, Column: >= 0, Slides: false }
            && _menu[note.Sample].RegionAt(note.Plays[0].RateHz)?.Second is not null;

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
            Dictionary<DacSample, int> recordingIndex = [];
            int?[] on = new int?[_song.Channels.Count];
            List<int>[] layerOn = [.. _song.Channels.Select(_ => new List<int>())];
            int?[] owner = new int?[_columns];
            PlanVoice[] voiceOn = new PlanVoice[_columns];
            int[] sampleOn = new int[_columns];
            Dictionary<(int Order, int Row, int Column), (int Tick, int Channel)> cuts = Cuts();
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
            return new GenesisPlan(_song.Title, patterns, _song.LoopOrder, _song.Timing, patches, recordings) { PcmSamples = wanted, TunedByEar = true, DacPanClash = PanClash, SongGainDb = _dmf ? 0 : -_levels.SongDb };

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

                // A pre-mix's tail starts on its row, on the column the mix played on (docs/adr/0094-pre-mixed-voice-steals.md).
                for (int c = 0; c < row.Cells.Count; c++)
                {
                    if (_tailAt.TryGetValue((order, r, c), out MixedNotes? mixed) && mixed is { Mix: { Tail: { } tail, Outlast: { } outlast }, Lead.Kind: not null })
                    {
                        int column = mixed.Lead.Column;
                        PlanVoice voice = new(VoiceKind.Dac, IndexOf(recordings, recordingIndex, tail));
                        PlannedNote last = mixed.Notes.First(n => n.Plays[0] == outlast);
                        cells[column] = new PlanCell(last.Pitch, voice, 1.0, false, [], c, last.Sample) { Centred = last.Centred };
                        on[c] = column;
                        owner[column] = c;
                        voiceOn[column] = voice;
                        sampleOn[column] = last.Sample;
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
                    // A pre-mix and its tail hold their notes' volumes and effects in their bytes.
                    if (row.Cells[c].Note is null && on[c] is { } column && owner[column] == c && !IsMix(voiceOn[column]))
                    {
                        Cell cell = row.Cells[c];
                        double? volume = cell.Volume ?? (cell.Instrument is { } i ? _song.Instruments[i].DefaultVolume : null);
                        if (volume is not null || own[c].Count > 0)
                        {
                            cells[column] = cells[column] with { Volume = volume, Effects = own[c], Source = c };
                            foreach (int layer in layerOn[c].Where(l => owner[l] == c))
                            {
                                cells[layer] = cells[layer] with { Volume = volume, Effects = own[c], Source = c };
                            }
                        }
                    }
                }

                for (int column = 0; column < _columns; column++)
                {
                    if (cuts.TryGetValue((order, r, column), out (int Tick, int Channel) cut))
                    {
                        Cut(cells, column, cut.Tick, cut.Channel);
                    }
                }

                return new PlanRow(cells, flow);
            }

            // The MOD's note has gone silent before its channel's next note: the voice stops there too (docs/adr/0095-voices-stop-with-the-mod-note.md).
            void Cut(PlanCell[] cells, int column, int tick, int c)
            {
                bool main = on[c] == column;
                if (owner[column] != c || (!main && !layerOn[c].Contains(column)) || (cells[column].Note is not null && cells[column].Source != c))
                {
                    return;
                }

                if (cells[column].Note is null && tick == 0)
                {
                    cells[column] = cells[column] with { NoteOff = true, Source = c };
                    owner[column] = null;
                    if (main)
                    {
                        on[c] = null;
                    }
                    else
                    {
                        layerOn[c].Remove(column);
                    }

                    return;
                }

                // One cut per cell, the earliest: the MOD's own ECx and this stop merge, so the render and the DMF agree on it.
                int at = cells[column].Effects.OfType<NoteCut>().Select(e => e.Tick).Append(tick).Min();
                cells[column] = cells[column] with { Effects = [.. cells[column].Effects.Where(e => e is not NoteCut), new NoteCut(at, Ends: true)], Source = c };
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

                    MixedNotes? mixed = _mixOf.GetValueOrDefault(note);
                    if (mixed is not null && mixed.Lead != note)
                    {
                        // The pre-mix plays this note: its channel's last note ends here, as it would.
                        Stop(cells, c);
                        return;
                    }

                    if (on[c] is { } previous && previous != note.Column && owner[previous] == c && cells[previous].Note is null)
                    {
                        cells[previous] = cells[previous] with { NoteOff = true, Source = c };
                    }

                    if (mixed?.Mix is { } mix)
                    {
                        // The pre-mix plays from its start at full volume: its notes' levels and effects are in its bytes,
                        // but a note delay still says when it starts. No channel owns it, so no channel's stop cuts it.
                        PlanVoice premix = new(VoiceKind.Dac, IndexOf(recordings, recordingIndex, mix.Head));
                        cells[note.Column] = new PlanCell(note.Pitch, premix, 1.0, false, [.. effects.OfType<NoteDelay>()], c, note.Sample) { Centred = mixed.Notes.Any(n => n.Centred) };
                        WriteLayer(cells, c, note, effects);
                        on[c] = null;
                        owner[note.Column] = null;
                        voiceOn[note.Column] = premix;
                        sampleOn[note.Column] = note.Sample;
                        return;
                    }

                    RegionSounds? region = _menu[note.Sample].RegionAt(note.Plays[0].RateHz);
                    ChordVoices? chord = note.Chord ? region?.Chord : null;
                    PlanVoice voice = new(kind, kind switch
                    {
                        VoiceKind.Dac => IndexOf(recordings, recordingIndex, Recording(note)),
                        VoiceKind.Fm => IndexOfPatch(patches, patchIndex, chord?.Patch ?? region?.Patch ?? FmPatchLibrary.Sine),
                        _ => 0,
                    });
                    bool second = chord is null && note is { LayerColumns.Count: > 0, LayerKind: VoiceKind.Fm };
                    double level = chord?.Tones[0].Level ?? (second ? region?.FirstLevel ?? 1 : 1);
                    double cents = chord?.Tones[0].Cents ?? (second ? -(region?.SpreadCents ?? 0) / 2 : 0);
                    cells[note.Column] = new PlanCell(note.Pitch, voice, note.Volume, false, effects, c, note.Sample) { Level = level, Cents = cents, Centred = kind == VoiceKind.Dac && note.Centred };
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

            // The voices that sound with the note follow it; a note without them stops those its channel had.
            void WriteLayer(PlanCell[] cells, int c, PlannedNote note, List<Effect> effects)
            {
                foreach (int old in layerOn[c].Where(l => !note.LayerColumns.Contains(l) && owner[l] == c && cells[l].Note is null))
                {
                    cells[old] = cells[old] with { NoteOff = true, Source = c };
                    owner[old] = null;
                }

                layerOn[c] = [];
                RegionSounds? region = _menu[note.Sample].RegionAt(note.Plays[0].RateHz);
                ChordVoices? chord = note.Chord ? region?.Chord : null;
                for (int i = 0; i < note.LayerColumns.Count; i++)
                {
                    int column = note.LayerColumns[i];
                    bool fm = note.LayerKind == VoiceKind.Fm;
                    PlanVoice layer = fm
                        ? new(VoiceKind.Fm, IndexOfPatch(patches, patchIndex, chord?.Patch ?? region?.Second ?? FmPatchLibrary.Sine))
                        : new(VoiceKind.PsgTone, 0);
                    double level = chord?.Tones[i + 1].Level ?? (fm ? 1 : ToneLayerLevel);
                    double cents = chord?.Tones[i + 1].Cents ?? (fm ? (region?.SpreadCents ?? 0) / 2 : 0);
                    cells[column] = new PlanCell(note.Pitch, layer, note.Volume, false, effects, c, note.Sample) { Level = level, Cents = cents };
                    layerOn[c].Add(column);
                    owner[column] = c;
                    voiceOn[column] = layer;
                    sampleOn[column] = note.Sample;
                }
            }

            // True for a recording the mapper mixed: a pre-mix or a tail.
            bool IsMix(PlanVoice voice) => voice.Kind == VoiceKind.Dac && voice.Index < recordings.Count && recordings[voice.Index].Mix is not null;

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

                foreach (int layer in layerOn[c].Where(l => owner[l] == c && cells[l].Note is null))
                {
                    cells[layer] = cells[layer] with { NoteOff = true, Source = c };
                    owner[layer] = null;
                }

                on[c] = null;
                layerOn[c] = [];
            }
        }

        /// <summary>
        /// Where a note's voice must stop before its channel's next note (docs/adr/0095-voices-stop-with-the-mod-note.md): an
        /// FM voice (or FM layer) when its one-shot sample has played out, as the MOD falls silent there and the key would
        /// otherwise stay on; a recording when the MOD's volume falls to 0 for good, as the DAC keeps the volume it started
        /// with. Keyed by the row and column, with the tick in the row and the song channel.
        /// </summary>
        /// <remarks>
        /// The renderer reads a row afresh each time it plays it (each pass of a pattern loop, <c>E6x</c>, and each repeat of a
        /// row held by <c>EEx</c>) and cuts on the same tick of every read. So a cut is kept only when every read of its row
        /// wants the voice stopped on that tick; otherwise the voice is left to sound, as before cuts. A stop followed by another
        /// start on the channel later in the same read of the row (a retrigger, <c>E9x</c>, or a delayed note, <c>EDx</c>) is
        /// left out too: that start takes the voice over there, and the cell keeps one cut, for the note that starts last.
        /// </remarks>
        private Dictionary<(int Order, int Row, int Column), (int Tick, int Channel)> Cuts()
        {
            Dictionary<(int Order, int Row, int Column), Dictionary<int, int>> wanted = [];
            foreach (PlannedNote note in _planned.Where(n => n is { Kind: not null, Column: >= 0 }))
            {
                foreach (SongNote play in note.Plays)
                {
                    int? at = note.Kind switch
                    {
                        VoiceKind.Fm => PlayedOut(play),
                        VoiceKind.Dac => FadedOut(play),
                        _ => null,
                    };
                    if (at is not { } tick || StartsLaterInRow(tick, play.Channel))
                    {
                        continue;
                    }

                    Want(note.Column, tick, play.Channel);
                    if (note.Kind == VoiceKind.Fm)
                    {
                        note.LayerColumns.ForEach(l => Want(l, tick, play.Channel));
                    }
                }
            }

            Dictionary<(int Order, int Row), List<int>> reads = [];
            for (int t = 0; t < _playback.Ticks.Count; t++)
            {
                if (_playback.Ticks[t] is { Tick: 0 } first)
                {
                    if (!reads.TryGetValue((first.Order, first.Row), out List<int>? list))
                    {
                        list = [];
                        reads[(first.Order, first.Row)] = list;
                    }

                    list.Add(t);
                }
            }

            Dictionary<(int Order, int Row, int Column), (int Tick, int Channel)> cuts = [];
            foreach (((int Order, int Row, int Column) key, Dictionary<int, int> stops) in wanted)
            {
                List<int> rowReads = reads.GetValueOrDefault((key.Order, key.Row)) ?? [];
                foreach (int tick in stops.Keys.Select(at => _playback.Ticks[at].Tick).Distinct().Order())
                {
                    if (rowReads.Count > 0 && rowReads.All(s => stops.ContainsKey(s + tick) && SameRead(s, s + tick)))
                    {
                        cuts[key] = (tick, stops[rowReads[0] + tick]);
                        break;
                    }
                }
            }

            return cuts;

            void Want(int column, int tick, int channel)
            {
                PlaybackTick end = _playback.Ticks[tick];
                (int, int, int) key = (end.Order, end.Row, column);
                if (!wanted.TryGetValue(key, out Dictionary<int, int>? stops))
                {
                    stops = [];
                    wanted[key] = stops;
                }

                stops.TryAdd(tick, channel);
            }
        }

        /// <summary>True when song channel <paramref name="channel"/> starts a sample after tick <paramref name="t"/>, in the same read of its row.</summary>
        private bool StartsLaterInRow(int t, int channel)
        {
            for (int j = t + 1; j < _playback.Ticks.Count && SameRead(t, j); j++)
            {
                if (_playback.Ticks[j].Channels[channel].Started is not null)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when tick <paramref name="to"/> comes after tick <paramref name="from"/> in the same read of a row: the same
        /// row, with its ticks counting up and not starting over (as they do on each repeat of an <c>EEx</c> row).
        /// </summary>
        private bool SameRead(int from, int to)
        {
            for (int j = from + 1; j <= to; j++)
            {
                PlaybackTick tick = _playback.Ticks[j];
                if (tick.Order != _playback.Ticks[from].Order || tick.Row != _playback.Ticks[from].Row || tick.Tick <= _playback.Ticks[j - 1].Tick)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The tick after <paramref name="play"/>'s sample has played out into silence, or <c>null</c> when the channel's next
        /// note (or the song's end) comes first, or when the note ended because the channel swapped to another sample without a
        /// new note (an instrument-only cell), which keeps sounding.
        /// </summary>
        private int? PlayedOut(SongNote play)
        {
            int end = play.FirstTick + play.Ticks;
            if (end >= _playback.Ticks.Count)
            {
                return null;
            }

            ChannelTick channel = _playback.Ticks[end].Channels[play.Channel];
            return channel.Started is null && channel.Next.IsSilence && (channel.Next.Sample is null || channel.Next.Sample == play.Sample) ? end : null;
        }

        /// <summary>
        /// The first tick from which <paramref name="play"/>'s volume stays <see cref="FadedOutDb"/> or more below the volume it
        /// started at, to its end, or <c>null</c> when it never does.
        /// </summary>
        private int? FadedOut(SongNote play)
        {
            double quiet = play.Volume * Math.Pow(10, -FadedOutDb / 20);
            int? silent = null;
            for (int t = play.FirstTick + play.Ticks - 1; t > play.FirstTick && _playback.Ticks[t].Channels[play.Channel].Volume <= quiet; t--)
            {
                silent = t;
            }

            return silent;
        }

        /// <summary>The sound a note would take with the Genesis all to itself.</summary>
        private VoiceKind? FirstChoice(PlannedNote note) => AllKinds
            .Where(k => Allowed(k, note))
            .Cast<VoiceKind?>()
            .OrderBy(k => k is { } kind ? Distance(kind, note) : 0)
            .FirstOrDefault();

        private int StoredAs(int sample) => _storedAs.GetValueOrDefault(sample, sample);

        /// <summary>The recording a note plays: its first stored copy, with what the shrinker did to it (plan T9.4a).</summary>
        private DacSample Recording(PlannedNote note)
        {
            if (note.Plays.SelectMany(CopiesOf).Cast<StoredCopy?>().FirstOrDefault() is not { } copy)
            {
                return new DacSample(StoredAs(note.Sample), note.Plays[0].RateHz) { GainDb = _dmf ? 0 : _levels.RecordingGainDb(note.Sample) };
            }

            bool loudness = _shrunk?.Steps.Any(s => s.Move == ShrinkMove.ShareLoudness && s.Sample == copy.Sample) == true;
            return new DacSample(copy.Sample, copy.RateHz)
            {
                HalfSpeed = copy.HalfSpeed,
                LoudnessShareSteps = loudness ? _rules.LoudnessShareSteps : 0,
                HeldSteps = _quieter.Contains(note.Sample) && note.Centred ? 1 : 0,
                // Only a note that plays a merged look-alike's sound takes its gain. The DMF writer sets loudness from the
                // notes' volumes alone: the shrinker merges only look-alikes within half a step there.
                // A retry's rule may play the sample quieter still (plan T9.4s2).
                GainDb = _dmf ? 0 : (copy.Sample == StoredAs(note.Sample) ? 0 : _shrunk?.MergeGainDb.GetValueOrDefault(StoredAs(note.Sample)) ?? 0) + _levels.RecordingGainDb(note.Sample),
            };
        }

        private static int IndexOf(List<DacSample> list, Dictionary<DacSample, int> index, DacSample key)
        {
            if (!index.TryGetValue(key, out int i))
            {
                i = list.Count;
                list.Add(key);
                index[key] = i;
            }

            return i;
        }

        private static int IndexOfPatch(List<FmPatch> list, Dictionary<FmPatch, int> index, FmPatch patch)
        {
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

            foreach (IGrouping<string, StealMix> group in _mixOf.Values.Distinct()
                .Where(m => m.Lead.Kind is not null)
                .Select(m => (Key: string.Join(" + ", m.Notes.Select(n => n.Sample).Order().Select(s => Label(s, _song.Samples[s].Name))), m.Mix))
                .Where(m => m.Mix is not null)
                .GroupBy(m => m.Key, m => m.Mix!) // Only pre-mixes with a mix are left.
                .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                _notes.Add(
                    "Samples {Samples} sound together with no PCM voice to spare, so they were mixed into one recording {Count} times ({Tails} with a tail; turned down up to {ClipDb:0.#} dB so the mix doesn't clip)",
                    group.Key,
                    group.Count(),
                    group.Count(m => m.Tail is not null),
                    group.Max(m => m.ClipDb));
            }

            foreach (IGrouping<int, PlannedNote> chorded in _planned.Where(n => n is { Chord: true, Kind: not null }).GroupBy(n => n.Sample).OrderBy(g => g.Key))
            {
                // A chord note's voices are its region's, so every chorded note has them.
                int[] sizes = [.. chorded.Select(n => _menu[n.Sample].RegionAt(n.Plays[0].RateHz)!.Chord!.Tones.Count)];
                _notes.Add(
                    "Sample {Sample}: a chord of {Tones} notes, so its {Count} FM notes play its notes as FM voices of their own; {Fewer} of them leave out the top notes, for want of free FM channels",
                    Label(chorded.Key, _song.Samples[chorded.Key].Name),
                    sizes.Max(),
                    chorded.Count(),
                    chorded.Zip(sizes).Count(p => p.First.LayerColumns.Count + 1 < p.Second));
            }

            foreach (IGrouping<(int Sample, VoiceKind Kind), PlannedNote> layered in _planned.Where(n => n is { LayerColumns.Count: > 0, Chord: false, Kind: not null }).GroupBy(n => (n.Sample, n.LayerKind)).OrderBy(g => g.Key.Sample))
            {
                _notes.Add(
                    layered.Key.Kind == VoiceKind.Fm ? "Sample {Sample}: a chord, so its {Count} FM notes also sound a second FM voice" : "Sample {Sample}: {Count} FM notes also sound a quiet PSG tone",
                    Label(layered.Key.Sample, _song.Samples[layered.Key.Sample].Name),
                    layered.Count());
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
