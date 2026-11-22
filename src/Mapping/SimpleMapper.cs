using System.Globalization;
using Mod2Xgm.Model;
using Mod2Xgm.Optimizer;

namespace Mod2Xgm.Mapping;

/// <summary>
/// The first mapper (plan T5.3, docs/adr/0040-simple-mapper.md): every note goes to
/// the voice that can play its sample best, decided <b>per instrument</b> by simple rules, until the optimizer
/// (plan M9) decides by measured accuracy.
/// <list type="bullet">
/// <item>A sample that loops is a sustained tone: <b>FM</b>, with a built-in patch picked by brightness. Not when its loop is
/// noise, or a short tail after a long recording with no clear pitch (a bell hit with a hiss at its end): that is played
/// like a sample that plays once (plan T5.3b, docs/adr/0117-tail-and-noise-loops.md). A tail after a pitched recording
/// stays FM, at the recording's pitch.</item>
/// <item>A sample that plays once is a recording the synthesizer can't copy: the <b>DAC</b> (unless <c>--no-dac</c>, when
/// it is the PSG <b>noise</b> channel if it is noise and FM otherwise).</item>
/// <item><c>--require-fm</c> and <c>--require-dac</c> override this per sample.</item>
/// </list>
/// MOD channel <i>n</i> plays its FM notes on FM<i>n</i>; the DAC (FM6) and the PSG noise channel are shared by all MOD
/// channels. When notes of two MOD channels start on the same row and want the same shared channel, the louder note
/// keeps it (the lower channel wins a tie) and the other moves to the noise channel (if it is noise) or to FM; a
/// <c>--require-dac</c> note that loses is dropped, never moved (ADR 0021). Playing both together as one mixed sample
/// is a different candidate for the optimizer (plan T8.5), not done here.
/// </summary>
/// <param name="loser">Where a drum that loses the DAC to a louder one plays (plan T9.4r, docs/adr/0109-pan-swap-cost.md).</param>
/// <param name="fits">
/// FM patches fitted to the samples (<c>--quality normal</c> or <c>best</c>; plan T5.3q, docs/adr/0137-simple-mapper-fitted-patches.md):
/// each pitch region plays its fit where the fit sounds closer than the built-in patch, and the song's LFO runs at the rate
/// that helps its fits most. <c>null</c> for the built-in patches only.
/// </param>
internal sealed class SimpleMapper(DacLoser loser = DacLoser.Default, FittedFm? fits = null) : IChannelMapper
{
    /// <summary>MOD channels beyond this many have no FM channel (FM1–5; FM6 is the DAC) and are left out.</summary>
    private const int MappedChannels = 5;

    /// <summary>The FM columns a chord's notes may sound on: FM1–FM5, never FM6, which the DAC shares (docs/adr/0133-simple-fm-chords.md).</summary>
    private const int ChordColumns = (int)GenesisChannel.Fm6;

    /// <summary>The Amiga period of ProTracker's C-2 (docs/formats/mod.md#pitch), for saying what pitch a sample sounds at.</summary>
    private const int C2Period = 428;

    /// <summary>The note names of the 12 semitones from C, for conversion notes.</summary>
    private static readonly string[] SemitoneNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];


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

        if (loser != DacLoser.NoiseAllSong)
        {
            return new Run(song, playback, options, notes, loser, new HashSet<int>(), fits).Execute();
        }

        // A first, silent pass finds the drums on a side that lose the DAC somewhere; they play the PSG noise all song.
        Run probe = new(song, playback, options, new ConversionNotes(Serilog.Core.Logger.None), DacLoser.Noise, new HashSet<int>());
        probe.Execute();
        return new Run(song, playback, options, notes, loser, probe.SideLosers, fits).Execute();
    }

    /// <summary>
    /// The samples whose notes the mapper plays on FM with one voice, so a fitted patch may take over from the built-in one
    /// (docs/adr/0137-simple-mapper-fitted-patches.md): the ones worth fitting. A chord's notes play on several voices and
    /// keep their built-in patch.
    /// </summary>
    public static IReadOnlySet<int> FmSamples(Song song, MappingOptions options)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(options);
        Run probe = new(song, new Playback([], null, PlaybackEnd.Stops), options, new ConversionNotes(Serilog.Core.Logger.None), DacLoser.Noise, new HashSet<int>());
        return probe.BuiltIns().Keys.ToHashSet();
    }

    private enum Primary
    {
        Silent,
        Ignored,
        Fm,
        Dac,
        Noise,
    }

    /// <summary>How an instrument's notes are played, and the FM patch to use when they can't be played that way.</summary>
    /// <param name="Primary">The voice its notes go to.</param>
    /// <param name="Fallback">The FM patch, for its notes that are played by FM.</param>
    /// <param name="NoiseLike">True when the sample is noise.</param>
    /// <param name="Required">True when a <c>--require-…</c> option chose the voice.</param>
    /// <param name="Reason">The conversion note that says why.</param>
    /// <param name="Assumption">A conversion note said before <paramref name="Reason"/>: what the mapper took the sample to be.</param>
    /// <param name="Detail">A conversion note said after <paramref name="Reason"/>: how its FM patch was chosen.</param>
    private sealed record Route(Primary Primary, FmPatch Fallback, bool NoiseLike, bool Required, Why? Reason = null, Why? Assumption = null, Why? Detail = null);

    /// <summary>The conversion note that explains a lossy routing choice, said once, when the instrument is first played.</summary>
    private sealed record Why(string Template, object[] Values);

    /// <summary>A note waiting to be given a channel.</summary>
    private sealed record Candidate(int Channel, NotePitch Note, int Instrument, double Volume)
    {
        /// <summary>True when this note beats <paramref name="other"/> for a shared channel: louder, or the lower channel on a tie.</summary>
        public bool Beats(Candidate other)
        {
            int order = Volume.CompareTo(other.Volume);
            return order > 0 || (order == 0 && Channel < other.Channel);
        }
    }

    /// <summary>What a MOD channel last sounded on, to know what to stop and where its effects belong.</summary>
    private enum Sounding
    {
        Nothing,
        Fm,
        Dac,
        Noise,
    }

    /// <summary>One pass over a song; holds the state a pass needs.</summary>
    private sealed class Run
    {
        private readonly Song _song;
        private readonly Playback _playback;
        private readonly MappingOptions _options;
        private readonly ConversionNotes _notes;
        private readonly Route[] _routes;
        private readonly List<FmPatch> _patches = [];
        private readonly Dictionary<int, HarmonicPatch?> _harmonics = [];
        private readonly List<int> _dacSamples = [];
        private readonly Dictionary<int, Dictionary<int, (int Count, double Rate)>> _dacPitches = [];
        private readonly HashSet<int> _played = [];
        private readonly HashSet<int> _explained = [];
        private readonly SortedDictionary<int, int> _ignored = [];
        private readonly int[] _instrument;
        private readonly double[] _volume;
        private readonly Sounding[] _sounding;

        private int _noInstrument;
        private int _silent;
        private int _extraChannel;
        private int _dacToNoise;
        private int _dacToFm;
        private readonly DacLoser _loser;
        private readonly HashSet<int> _sideLosers = [];
        private int _dacDropped;
        private int _noiseToFm;
        private int _noiseDropped;

        // Chord voices (docs/adr/0133-simple-fm-chords.md): each sample's chord notes (null: not a chord), the FM columns each
        // MOD channel's chord sounds on beside its own, the MOD channel each column's chord voice belongs to, and the tallies.
        private readonly Dictionary<int, IReadOnlyList<ChordTone>?> _chords = [];

        // How each chord sample's notes were found, for its conversion note: its harmonics, or the notes read from its loop.
        private readonly Dictionary<int, string> _chordFound = [];
        private readonly List<int>[] _chordColumns;
        private readonly int[] _chordOwner = [.. Enumerable.Repeat(-1, ChordColumns)];
        private readonly SortedDictionary<int, (int Notes, int Short)> _chordNotes = [];
        private int _chordCut;

        // The MOD channel whose note the DAC and the noise channel last started, so silencing a track stops only its own sound.
        private int _dacSource = -1;
        private int _noiseSource = -1;

        // The fitted patches the song plays and its LFO rate (docs/adr/0137-simple-mapper-fitted-patches.md); none without fits.
        private readonly bool _fitsGiven;
        private readonly FitChoice _fits;

        /// <param name="song">The song.</param>
        /// <param name="playback">How it plays.</param>
        /// <param name="options">The user's mapping options.</param>
        /// <param name="notes">Where conversion notes go.</param>
        /// <param name="loser">Where a drum that loses the DAC plays.</param>
        /// <param name="noiseAllSong">Instruments (0-based) that play the PSG noise all song, found by a first pass (<see cref="DacLoser.NoiseAllSong"/>).</param>
        /// <param name="fits">FM patches fitted to the samples, or <c>null</c> for the built-in ones only.</param>
        public Run(Song song, Playback playback, MappingOptions options, ConversionNotes notes, DacLoser loser, IReadOnlySet<int> noiseAllSong, FittedFm? fits = null)
        {
            _song = song;
            _playback = playback;
            _options = options;
            _notes = notes;
            _loser = loser;
            _routes = [.. song.Instruments.Select(Classify).Select((route, i) => noiseAllSong.Contains(i) && route is { Primary: Primary.Dac, NoiseLike: true, Required: false }
                ? route with { Primary = Primary.Noise, Reason = new Why("Sample {Sample}: a drum on one side that loses the DAC to louder ones, so it is played by the PSG noise channel all song and doesn't jump between sides", [Label(song.Instruments[i].Sample, song.Samples[song.Instruments[i].Sample].Name)]) }
                : route)];
            _instrument = [.. Enumerable.Repeat(-1, song.Channels.Count)];
            _volume = [.. Enumerable.Repeat(1.0, song.Channels.Count)];
            _sounding = [.. Enumerable.Repeat(Sounding.Nothing, song.Channels.Count)];
            _chordColumns = [.. Enumerable.Range(0, song.Channels.Count).Select(_ => new List<int>())];
            _fitsGiven = fits is not null;
            _fits = fits is null ? FitChoice.None : FitChoice.Choose(fits, BuiltIns());
        }

        /// <summary>
        /// The built-in patch of every sample a fitted patch may take over from: those whose notes go to FM, with one
        /// voice (not a chord, docs/adr/0137-simple-mapper-fitted-patches.md), by sample index.
        /// </summary>
        public IReadOnlyDictionary<int, BuiltInFm> BuiltIns()
        {
            Dictionary<int, BuiltInFm> builtIn = [];
            for (int i = 0; i < _routes.Length; i++)
            {
                int sample = _song.Instruments[i].Sample;
                if (_routes[i].Primary == Primary.Fm && ChordOf(i) is null && !builtIn.ContainsKey(sample))
                {
                    builtIn[sample] = new BuiltInFm(_routes[i].Fallback, LiftCents(sample), _song.Samples[sample].Loop is not null);
                }
            }

            return builtIn;
        }

        /// <summary>Instruments (0-based) on a side that lost the DAC to the PSG noise in this pass: they would jump to the middle.</summary>
        public IReadOnlySet<int> SideLosers => _sideLosers;

        /// <summary>True when song channel <paramref name="channel"/> plays on one side only, so the PSG (no pan) would move it.</summary>
        private bool OnASide(int channel) => PlanVoicing.Side(_song.Channels[channel].Pan) != 0;

        public GenesisPlan Execute()
        {
            List<IReadOnlyList<PlanRow>> patterns = [];
            for (int order = 0; order < _song.Orders.Count; order++)
            {
                Pattern pattern = _song.Patterns[_song.Orders[order]];
                PlanRow[] rows = new PlanRow[pattern.Rows.Count];
                for (int r = 0; r < rows.Length; r++)
                {
                    rows[r] = MapRow(pattern.Rows[r], order, r);
                }

                patterns.Add(rows);
            }

            ReportTallies();
            DacSample[] dac = [.. _dacSamples.Select(sample => new DacSample(sample, DacRate(sample)))];
            HashSet<int> recordings = [.. _song.Instruments.Where((_, i) => _routes[i].Primary == Primary.Dac).Select(instrument => instrument.Sample)];
            return new GenesisPlan(_song.Title, patterns, _song.LoopOrder, _song.Timing, _patches, dac) { PcmSamples = recordings, LfoRate = _fits.LfoRate };
        }

        /// <summary>Decides how one instrument's notes are played; lossy choices get a note.</summary>
        private Route Classify(Instrument instrument)
        {
            Sample sample = _song.Samples[instrument.Sample];
            string label = Label(instrument.Sample, sample.Name);
            if (_options.Ignore.Contains(instrument.Sample))
            {
                return new Route(Primary.Ignored, FmPatchLibrary.Sine, false, false);
            }

            if (sample.IsEmpty)
            {
                return new Route(Primary.Silent, FmPatchLibrary.Sine, false, false);
            }

            SampleTraits traits = SampleTraits.Analyze(sample);
            LoopKind loop = LoopShape.Of(sample, traits);
            bool pitchedTail = loop == LoopKind.Tail && SamplePitch.Measure(sample).PeriodFrames is not null;
            Why? notTone = pitchedTail ? null : NotTone(sample, loop, label);
            bool sustained = loop == LoopKind.Tone || pitchedTail;
            HarmonicPatch? harmonic = loop == LoopKind.Tone ? Harmonics(instrument.Sample) : null;
            FmPatch patch = harmonic?.Patch ?? (sustained ? FmPatchLibrary.ForSustained(traits.Brightness) : FmPatchLibrary.ForOneShot(traits.Brightness));
            bool requireDac = _options.RequireDac.Contains(instrument.Sample);

            if (_options.RequireFm.Contains(instrument.Sample))
            {
                return new Route(Primary.Fm, patch, traits.NoiseLike, false, new Why("Sample {Sample}: required as FM (--require-fm), so it is played by the {Patch} FM patch", [label, patch.Name]));
            }

            if (requireDac)
            {
                return new Route(Primary.Dac, patch, traits.NoiseLike, true);
            }

            if (_options.RequirePsg.Contains(instrument.Sample))
            {
                return new Route(Primary.Noise, patch, traits.NoiseLike, true, new Why("Sample {Sample}: required as PSG (--require-psg), so it is played by the PSG noise channel", [label]));
            }

            if (pitchedTail && sample.Loop is { } tail)
            {
                return new Route(Primary.Fm, patch, traits.NoiseLike, false, new Why("Sample {Sample}: its {Loop}-frame loop is a short tail after {Lead} frames of recording with a clear pitch, so it is a sustained tone, played by the {Patch} FM patch at the recording's pitch", [label, tail.Length, tail.Start, patch.Name]));
            }

            if (sustained)
            {
                return new Route(Primary.Fm, patch, traits.NoiseLike, false, new Why("Sample {Sample}: it loops, so it is a sustained tone and is played by the {Patch} FM patch", [label, patch.Name]), Detail: HarmonicNote(harmonic, label));
            }

            if (_options.UseDac)
            {
                return new Route(Primary.Dac, patch, traits.NoiseLike, false, new Why("Sample {Sample}: it plays once, so it is played as a recording on the DAC", [label]), notTone);
            }

            if (traits.NoiseLike)
            {
                return new Route(Primary.Noise, patch, true, false, new Why("Sample {Sample}: it is noise and the DAC is off (--no-dac), so it is played by the PSG noise channel", [label]), notTone);
            }

            return new Route(Primary.Fm, patch, false, false, new Why("Sample {Sample}: the DAC is off (--no-dac), so it is played by the {Patch} FM patch", [label, patch.Name]), notTone);
        }

        /// <summary>The sample's loop measured as harmonics of its pitch, once per sample; <c>null</c> when it has no pitch or can't be measured.</summary>
        private HarmonicPatch? Harmonics(int sample)
        {
            if (!_harmonics.TryGetValue(sample, out HarmonicPatch? harmonic))
            {
                Sample data = _song.Samples[sample];
                harmonic = SamplePitch.Measure(data).PeriodFrames is { } period ? HarmonicPatch.Of(data, period) : null;
                _harmonics[sample] = harmonic;
            }

            return harmonic;
        }

        /// <summary>
        /// The note that says whether a sustained tone's patch was built from its harmonics or chosen by brightness, or
        /// <c>null</c> when its harmonics couldn't be measured (docs/adr/0118-harmonic-patches.md).
        /// </summary>
        private static Why? HarmonicNote(HarmonicPatch? harmonic, string label) => harmonic switch
        {
            null => null,
            { Patch: not null, Harmonics: [1] } => new Why("Sample {Sample}: its fundamental holds {Capture:0.#} % of its tone, so it is played as a plain sine", [label, harmonic.Capture * 100]),
            { Patch: not null } => new Why("Sample {Sample}: harmonics {Harmonics} hold {Capture:0.#} % of its tone, so its FM patch sounds those harmonics at their levels", [label, string.Join(", ", harmonic.Harmonics), harmonic.Capture * 100]),
            _ => new Why("Sample {Sample}: its four loudest harmonics ({Harmonics}) hold only {Capture:0.#} % of its tone, so its FM patch is chosen by how bright it is", [label, string.Join(", ", harmonic.Harmonics), harmonic.Capture * 100]),
        };

        /// <summary>The note that says why a looping sample isn't played as a sustained tone, or <c>null</c> when it is one or doesn't loop.</summary>
        private static Why? NotTone(Sample sample, LoopKind kind, string label) => (kind, sample.Loop) switch
        {
            (LoopKind.Noise, { } loop) => new Why("Sample {Sample}: its {Loop}-frame loop is noise, not a repeating tone, so it is played like a sample that plays once", [label, loop.Length]),
            (LoopKind.Tail, { } loop) => new Why("Sample {Sample}: its {Loop}-frame loop is a short tail after {Lead} frames of recording with no clear pitch, so it is played like a sample that plays once", [label, loop.Length, loop.Start]),
            _ => null,
        };

        /// <summary>The note that says what pitch an FM-played sample's notes are placed at.</summary>
        private Why PitchNote(int sample)
        {
            string label = Label(sample, _song.Samples[sample].Name);
            if (SamplePitch.Measure(_song.Samples[sample]) is { PeriodFrames: { } frames } pitch)
            {
                double hz = pitch.FrequencyAt(AmigaPitch.PalClockHz / C2Period) ?? 0;
                HarmonicPatch? harmonic = _harmonics.GetValueOrDefault(sample);
                double lift = SamplePitch.LibraryLiftCents(harmonic);
                if (pitch.Chord)
                {
                    // What the loop's partials make is said either way, so a chord left as one note can be checked by ear.
                    LoopChord? loop = SamplePitch.LoopChordOf(_song.Samples[sample]);
                    return new Why("Sample {Sample}: no one cycle repeats in its loop (a chord, or a chorused tone), so its FM notes play its lowest strong note (C-2 sounds at {Hz:0.#} Hz); its partials make {Notes} notes in {Series} harmonic series, holding {Share:0} % of its power", [label, hz, loop?.Notes.Count ?? 0, loop?.Series ?? 0, (loop?.Share ?? 0) * 100]);
                }

                if (harmonic is not null && lift > 0)
                {
                    return new Why("Sample {Sample}: its tone repeats every {Frames:0.##} frames, a cycle below every note in it (a chord), so its FM notes play its lowest strong harmonic, {Harmonic} (C-2 sounds at {Hz:0.#} Hz)", [label, frames, harmonic.Lowest, hz * Math.Pow(2, lift / 1200)]);
                }

                return new Why("Sample {Sample}: its tone repeats every {Frames:0.##} frames, so its FM notes play at that pitch (C-2 sounds at {Hz:0.#} Hz)", [label, frames, hz]);
            }

            return new Why("Sample {Sample}: no repeating tone found in it, so its FM notes play two octaves above the MOD note's name", [label]);
        }

        private PlanRow MapRow(Row row, int order, int rowIndex)
        {
            List<Effect> flow = [];
            List<Effect>[] effects = [.. Enumerable.Range(0, GenesisPlan.ChannelCount).Select(_ => new List<Effect>())];
            PlanCell[] planned = [.. Enumerable.Repeat(PlanCell.Empty, GenesisPlan.ChannelCount)];
            List<Candidate> candidates = [];
            List<(int Channel, List<Effect> Effects)> pending = [];
            int?[] source = new int?[GenesisPlan.ChannelCount];

            for (int c = 0; c < row.Cells.Count; c++)
            {
                Cell cell = row.Cells[c];
                List<Effect> own = [];
                foreach (Effect effect in cell.Effects)
                {
                    (IsFlow(effect) ? flow : own).Add(effect);
                }

                if (c >= MappedChannels)
                {
                    _extraChannel += cell.Note is null ? 0 : 1;
                    continue;
                }

                bool volumeSet = false;
                if (cell.Instrument is { } instrument)
                {
                    _instrument[c] = instrument;
                    _volume[c] = _song.Instruments[instrument].DefaultVolume;
                    volumeSet = true;
                }

                if (cell.Volume is { } volume)
                {
                    _volume[c] = volume;
                    volumeSet = true;
                }

                if (cell.Note is { } written)
                {
                    NotePitch note = PlayedPitch(written, new NotePosition(order, rowIndex, c), _instrument[c]);
                    if (_instrument[c] < 0)
                    {
                        _noInstrument++;
                    }
                    else if (_routes[_instrument[c]].Primary == Primary.Silent)
                    {
                        _silent++;
                    }
                    else if (_routes[_instrument[c]].Primary == Primary.Ignored)
                    {
                        int sample = _song.Instruments[_instrument[c]].Sample;
                        _ignored[sample] = _ignored.GetValueOrDefault(sample) + 1;
                        Silence(c, planned);
                    }
                    else
                    {
                        _played.Add(_song.Instruments[_instrument[c]].Sample);
                        Explain(_instrument[c]);
                        candidates.Add(new Candidate(c, note, _instrument[c], _volume[c]));
                    }
                }
                else if (volumeSet && _sounding[c] == Sounding.Fm)
                {
                    planned[c] = planned[c] with { Volume = _volume[c], Source = c };
                    foreach (int column in _chordColumns[c].Where(column => planned[column].Note is null))
                    {
                        planned[column] = planned[column] with { Volume = _volume[c], Source = c };
                    }
                }

                pending.Add((c, own));
            }

            Place(candidates, planned);
            foreach (Candidate candidate in candidates)
            {
                Chord(candidate, planned);
            }

            foreach ((int channel, List<Effect> own) in pending.Where(p => p.Effects.Count > 0))
            {
                GenesisChannel target = _sounding[channel] switch
                {
                    Sounding.Dac => GenesisChannel.Fm6,
                    Sounding.Noise => GenesisChannel.Psg4,
                    _ => (GenesisChannel)channel,
                };
                effects[(int)target].AddRange(own);
                source[(int)target] ??= channel;

                // The chord's other voices follow its channel's effects, as its own voice does.
                foreach (int column in _sounding[channel] == Sounding.Fm ? _chordColumns[channel] : [])
                {
                    effects[column].AddRange(own);
                    source[column] ??= channel;
                }
            }

            PlanCell[] result = [.. planned.Select((cell, i) => effects[i].Count == 0 ? cell : cell with { Effects = effects[i], Source = cell.Source ?? source[i] })];
            return new PlanRow(result, flow);
        }

        /// <summary>
        /// The pitch a note is planned at: the tick player's, finetune included (ADR 0061). A row the song never plays has
        /// none, so its note is the written one moved by the instrument's finetune, to agree with the notes that play.
        /// </summary>
        private NotePitch PlayedPitch(NotePitch written, NotePosition position, int instrument)
        {
            if (_playback.NotePitches.TryGetValue(position, out NotePitch played))
            {
                return played;
            }

            double cents = instrument < 0 ? 0 : _song.Instruments[instrument].FineTuneCents;
            double semitones = written.Semitone + ((written.Cents + cents) / 100);
            int semitone = (int)Math.Floor(semitones + 0.5);
            return new NotePitch(semitone, (semitones - semitone) * 100, written.PlaybackRateHz * Math.Pow(2, cents / 1200));
        }

        /// <summary>Gives every candidate a channel, settling the shared ones.</summary>
        private void Place(List<Candidate> candidates, PlanCell[] planned)
        {
            List<Candidate> noise = [];
            List<Candidate> fm = [];
            List<Candidate> dac = [];
            foreach (Candidate candidate in candidates)
            {
                switch (_routes[candidate.Instrument].Primary)
                {
                    case Primary.Dac:
                        dac.Add(candidate);
                        break;
                    case Primary.Noise:
                        noise.Add(candidate);
                        break;
                    default:
                        fm.Add(candidate);
                        break;
                }
            }

            if (dac.Count > 0)
            {
                Candidate winner = dac.Aggregate((best, next) => next.Beats(best) ? next : best);
                PlaceOnDac(winner, planned);
                foreach (Candidate loser in dac.Where(d => d != winner))
                {
                    Route route = _routes[loser.Instrument];
                    if (route.Required)
                    {
                        _dacDropped++;
                    }
                    else if (route.NoiseLike && !(_loser == DacLoser.Fm && OnASide(loser.Channel)))
                    {
                        _dacToNoise++;
                        if (OnASide(loser.Channel))
                        {
                            _sideLosers.Add(loser.Instrument);
                        }

                        noise.Add(loser);
                    }
                    else
                    {
                        _dacToFm++;
                        fm.Add(loser);
                    }
                }
            }

            if (noise.Count > 0)
            {
                Candidate winner = noise.Aggregate((best, next) => next.Beats(best) ? next : best);
                PlaceOnNoise(winner, planned);
                foreach (Candidate loser in noise.Where(n => n != winner))
                {
                    if (_routes[loser.Instrument].Required)
                    {
                        _noiseDropped++;
                    }
                    else
                    {
                        _noiseToFm++;
                        fm.Add(loser);
                    }
                }
            }

            foreach (Candidate candidate in fm)
            {
                PlaceOnFm(candidate, planned);
            }
        }

        private void PlaceOnDac(Candidate candidate, PlanCell[] planned)
        {
            int sample = _song.Instruments[candidate.Instrument].Sample;
            int index = _dacSamples.IndexOf(sample);
            if (index < 0)
            {
                index = _dacSamples.Count;
                _dacSamples.Add(sample);
            }

            if (!_dacPitches.TryGetValue(sample, out Dictionary<int, (int Count, double Rate)>? pitches))
            {
                pitches = [];
                _dacPitches[sample] = pitches;
            }

            (int count, double rate) = pitches.GetValueOrDefault(candidate.Note.Semitone, (0, candidate.Note.PlaybackRateHz));
            pitches[candidate.Note.Semitone] = (count + 1, rate);
            Sound(candidate, GenesisChannel.Fm6, new PlanVoice(VoiceKind.Dac, index), Sounding.Dac, planned);
        }

        private void PlaceOnNoise(Candidate candidate, PlanCell[] planned) =>
            Sound(candidate, GenesisChannel.Psg4, new PlanVoice(VoiceKind.PsgNoise, 0), Sounding.Noise, planned);

        private void PlaceOnFm(Candidate candidate, PlanCell[] planned)
        {
            int sample = _song.Instruments[candidate.Instrument].Sample;
            FmPatch? fitted = _routes[candidate.Instrument].Primary == Primary.Fm ? _fits.PatchFor(sample) : null;
            FmPatch patch = fitted ?? _routes[candidate.Instrument].Fallback;
            int index = _patches.IndexOf(patch);
            if (index < 0)
            {
                index = _patches.Count;
                _patches.Add(patch);
            }

            Sound(candidate, (GenesisChannel)candidate.Channel, new PlanVoice(VoiceKind.Fm, index), Sounding.Fm, planned);
            if (fitted is not null)
            {
                // A fit was made at the sample's own pitch, so it isn't lifted as a built-in patch for a chord's cycle is.
                planned[candidate.Channel] = planned[candidate.Channel] with { Cents = 0 };
            }
        }

        /// <summary>Starts the note on <paramref name="column"/> and stops what its MOD channel was sounding elsewhere.</summary>
        private void Sound(Candidate candidate, GenesisChannel column, PlanVoice voice, Sounding kind, PlanCell[] planned)
        {
            int c = candidate.Channel;
            if (_sounding[c] != kind)
            {
                // A recording the track left plays on to its end, a looped one for seconds, where the MOD's next note cuts it.
                if (Playing(c) is { } stop && planned[(int)stop].Note is null)
                {
                    planned[(int)stop] = planned[(int)stop] with { NoteOff = true, Source = c };
                }
            }

            _sounding[c] = kind;
            if ((int)column < ChordColumns && _chordOwner[(int)column] is var owner and >= 0 && owner != c)
            {
                // A track's own note comes first: the chord on this column loses that voice until its next note.
                _chordColumns[owner].Remove((int)column);
                _chordOwner[(int)column] = -1;
                _chordCut++;
            }

            if (column == GenesisChannel.Fm6 && kind == Sounding.Dac)
            {
                _dacSource = c;
            }
            else if (column == GenesisChannel.Psg4)
            {
                _noiseSource = c;
            }

            int sample = _song.Instruments[candidate.Instrument].Sample;
            planned[(int)column] = new PlanCell(candidate.Note, voice, candidate.Volume, false, [], c, sample)
            {
                Cents = kind == Sounding.Fm ? LiftCents(sample) : 0,
            };
        }

        /// <summary>How far above its measured pitch <paramref name="sample"/>'s library FM patch plays (<see cref="SamplePitch.LibraryLiftCents"/>); 0 when it has no tone loop.</summary>
        private double LiftCents(int sample) => _harmonics.TryGetValue(sample, out HarmonicPatch? harmonic) ? SamplePitch.LibraryLiftCents(harmonic) : 0;

        /// <summary>
        /// Stops what MOD channel <paramref name="c"/> is sounding, for a note of an ignored sample: the track is silent
        /// until its next note. The DAC and the noise channel are stopped only when this track started their sound.
        /// </summary>
        private void Silence(int c, PlanCell[] planned)
        {
            if (Playing(c) is { } stop && planned[(int)stop].Note is null)
            {
                planned[(int)stop] = planned[(int)stop] with { NoteOff = true, Source = c };
            }

            StopChord(c, [], planned);
            _sounding[c] = Sounding.Nothing;
        }

        /// <summary>
        /// Plays the rest of the chord of <paramref name="candidate"/>'s note, once every note of the row has its channel
        /// (docs/adr/0133-simple-fm-chords.md). The note's own voice plays the chord's lowest note; each of the others goes
        /// to an FM column nothing else sounds on (FM1–FM5: its previous ones first, then those no track of the song owns),
        /// at its share of the level. With fewer free columns than notes, the top notes are left out. A note that isn't a
        /// chord lets go of the chord voices its channel had.
        /// </summary>
        private void Chord(Candidate candidate, PlanCell[] planned)
        {
            int c = candidate.Channel;
            PlanCell own = planned[c];
            if (_sounding[c] != Sounding.Fm || own is not { Note: not null, Voice.Kind: VoiceKind.Fm } || own.Source != c
                || ChordOf(candidate.Instrument) is not { } tones)
            {
                StopChord(c, [], planned);
                return;
            }

            List<int> columns = [.. _chordColumns[c]
                .Concat(Enumerable.Range(0, ChordColumns).OrderBy(k => k < _song.Channels.Count ? 1 : 0).ThenBy(k => k))
                .Distinct()
                .Where(k => k != c && Free(k, c, planned))
                .Take(tones.Count - 1)];
            StopChord(c, columns, planned);
            int sample = _song.Instruments[candidate.Instrument].Sample;
            planned[c] = own with { Cents = tones[0].Cents, Level = tones[0].Level };
            for (int i = 0; i < columns.Count; i++)
            {
                planned[columns[i]] = new PlanCell(own.Note, own.Voice, own.Volume, false, [], c, sample) { Cents = tones[i + 1].Cents, Level = tones[i + 1].Level };
                _chordOwner[columns[i]] = c;
            }

            _chordColumns[c].Clear();
            _chordColumns[c].AddRange(columns);
            (int notes, int fewer) = _chordNotes.GetValueOrDefault(sample);
            _chordNotes[sample] = (notes + 1, fewer + (columns.Count + 1 < tones.Count ? 1 : 0));
        }

        /// <summary>
        /// True when FM column <paramref name="k"/> has nothing on it this row and nothing sounding but MOD channel
        /// <paramref name="c"/>'s own chord voice: no track's own FM note, no other track's chord.
        /// </summary>
        private bool Free(int k, int c, PlanCell[] planned) =>
            planned[k].Note is null
            && (k >= _song.Channels.Count || _sounding[k] != Sounding.Fm)
            && (_chordOwner[k] == -1 || _chordOwner[k] == c);

        /// <summary>Lets go of MOD channel <paramref name="c"/>'s chord voices, except those on <paramref name="keep"/>.</summary>
        private void StopChord(int c, IReadOnlyCollection<int> keep, PlanCell[] planned)
        {
            foreach (int column in _chordColumns[c].Where(column => !keep.Contains(column)))
            {
                if (planned[column].Note is null)
                {
                    planned[column] = planned[column] with { NoteOff = true, Source = c };
                }

                _chordOwner[column] = -1;
            }

            _chordColumns[c].RemoveAll(column => !keep.Contains(column));
        }

        /// <summary>
        /// The notes of the chord <paramref name="instrument"/>'s FM notes stand for, lowest first, each with its share of
        /// the level (their powers add up to 1, so the chord is as loud as one voice was); <c>null</c> when it isn't a chord.
        /// A chord is a sustained tone whose cycle sits below every note in it (docs/adr/0125-chord-samples.md); its notes
        /// are read from its harmonics as the optimizer reads them (<see cref="SoundMenu.ChordTones"/>, docs/adr/0114-fm-chords.md).
        /// </summary>
        private IReadOnlyList<ChordTone>? ChordOf(int instrument)
        {
            int sample = _song.Instruments[instrument].Sample;
            if (_routes[instrument].Primary != Primary.Fm)
            {
                return null;
            }

            if (!_chords.TryGetValue(sample, out IReadOnlyList<ChordTone>? tones))
            {
                tones = null;
                if (Harmonics(sample) is { Patch: null, Lowest: > 1, LevelsDb.Count: > 0 } harmonic
                    && SoundMenu.ChordTones([[.. harmonic.LevelsDb]]) is { Count: > 1 } found)
                {
                    double[] amplitude = [.. found.Select(h => Math.Pow(10, harmonic.LevelsDb[h - 1] / 20))];
                    double total = Math.Sqrt(amplitude.Sum(a => a * a));
                    tones = [.. found.Select((h, i) => new ChordTone(h, amplitude[i] / total))];
                    _chordFound[sample] = string.Create(CultureInfo.InvariantCulture, $"harmonics {string.Join(", ", found)} of its cycle");
                }
                else if (SamplePitch.Measure(_song.Samples[sample]) is { Chord: true, PeriodFrames: { } bottom }
                    && SamplePitch.LoopChordOf(_song.Samples[sample]) is { IsChord: true } loop)
                {
                    // A loop with no one cycle whose partials make three or four notes (docs/adr/0135-loop-chords.md): each
                    // note at its distance from the bottom note the sample's FM notes are placed at.
                    tones = [.. loop.Notes.Select(n => new ChordTone(0, Math.Sqrt(n.Share / loop.Share)) { Cents = 1200 * Math.Log2(n.CyclesPerFrame * bottom) })];
                    _chordFound[sample] = string.Create(CultureInfo.InvariantCulture, $"{string.Join(", ", loop.Notes.Select(n => NoteName(n.CyclesPerFrame * AmigaPitch.PalClockHz / C2Period)))} at C-2, read from its loop");
                }

                _chords[sample] = tones;
            }

            return tones;
        }

        /// <summary>
        /// The column still sounding MOD channel <paramref name="c"/>'s last note, or <c>null</c>. The DAC and the noise
        /// channel count only while this track started their sound: another track's drum since then is not its to stop.
        /// </summary>
        private GenesisChannel? Playing(int c) => _sounding[c] switch
        {
            Sounding.Fm => (GenesisChannel)c,
            Sounding.Dac when _dacSource == c => GenesisChannel.Fm6,
            Sounding.Noise when _noiseSource == c => GenesisChannel.Psg4,
            _ => null,
        };

        /// <summary>The rate of the pitch the song plays <paramref name="sample"/> at most often (the lowest pitch on a tie).</summary>
        private double DacRate(int sample)
        {
            Dictionary<int, (int Count, double Rate)> pitches = _dacPitches[sample];
            return pitches.OrderByDescending(p => p.Value.Count).ThenBy(p => p.Key).First().Value.Rate;
        }

        private void ReportTallies()
        {
            Tally("{Count} notes were played before their channel had an instrument; left out", _noInstrument);
            Tally("{Count} notes use an empty sample slot; left out", _silent);
            Tally("{Count} notes are on MOD channels above {Mapped}, which have no FM channel; left out", _extraChannel, MappedChannels);
            Tally("{Count} drum notes started on the same row as a louder one and could not use the DAC; played by the PSG noise channel instead", _dacToNoise);
            Tally("{Count} drum notes started on the same row as a louder one and could not use the DAC; played by FM instead", _dacToFm);
            Tally("{Count} --require-dac notes started on the same row as a louder one and could not use the DAC; left out (the DAC plays one sample at a time)", _dacDropped);
            Tally("{Count} --require-psg notes started on the same row as a louder one and could not use the PSG noise channel; left out (it plays one sound at a time)", _noiseDropped);
            Tally("{Count} noise notes started on the same row as a louder one and could not use the PSG noise channel; played by FM instead", _noiseToFm);

            foreach ((int sample, (int notes, int fewer)) in _chordNotes)
            {
                // _chordNotes holds only samples ChordOf found a chord in.
                _notes.Add(
                    "Sample {Sample}: a chord ({Found}), so each of its {Count} FM notes plays the chord's notes on FM channels of their own; {Fewer} found fewer free FM channels than notes and left out the top ones",
                    Label(sample, _song.Samples[sample].Name),
                    _chordFound[sample],
                    notes,
                    fewer);
            }

            Tally("{Count} chord voices stopped early, where the track that owns their FM channel played a note", _chordCut);
            if (_fits.LfoRate is { } rate)
            {
                // The LFO is one setting for the whole chip, so the other fits play without it (docs/adr/0137-simple-mapper-fitted-patches.md).
                _notes.Add("The song's LFO runs at rate {Rate}, for the fits that sound closer with it (samples: {Samples}); every other FM sound plays without it", rate, _fits.LfoSamples);
            }

            foreach ((int sample, int count) in _ignored)
            {
                _notes.Add(
                    "Sample {Sample}: ignored (--ignore-samples), so its {Count} notes are left out and their tracks are silent until the next note",
                    Label(sample, _song.Samples[sample].Name),
                    count);
            }

            foreach (int sample in _options.RequireFm.Concat(_options.RequireDac).Concat(_options.RequirePsg).Order())
            {
                if (sample >= 0 && sample < _song.Samples.Count && !_played.Contains(sample))
                {
                    _notes.Add(
                        "Sample {Sample}: required as {Kind}, but it is empty or never played; the requirement has no effect",
                        Label(sample, _song.Samples[sample].Name),
                        _options.RequireFm.Contains(sample) ? "FM" : _options.RequireDac.Contains(sample) ? "DAC" : "PSG");
                }
            }
        }

        private void Tally(string template, int count, params object[] more)
        {
            if (count > 0)
            {
                _notes.Add(template, [count, .. more]);
            }
        }

        /// <summary>Says why an instrument is played the way it is, once, the first time a note plays it.</summary>
        private void Explain(int instrument)
        {
            if (!_explained.Add(instrument))
            {
                return;
            }

            Route route = _routes[instrument];
            foreach (Why why in new[] { route.Assumption, route.Reason, route.Detail }.OfType<Why>())
            {
                _notes.Add(why.Template, why.Values);
            }

            if (route.Primary == Primary.Fm)
            {
                int sample = _song.Instruments[instrument].Sample;
                Why pitch = PitchNote(sample);
                _notes.Add(pitch.Template, pitch.Values);
                ExplainFits(instrument, sample);
            }
        }

        /// <summary>Says whether an FM sample plays a fitted patch for all its notes, and why (docs/adr/0137-simple-mapper-fitted-patches.md).</summary>
        private void ExplainFits(int instrument, int sample)
        {
            if (!_fitsGiven)
            {
                return;
            }

            string label = Label(sample, _song.Samples[sample].Name);
            if (ChordOf(instrument) is not null)
            {
                _notes.Add("Sample {Sample}: its notes play as a chord on several FM voices, so it keeps its built-in patch instead of a fitted one", label);
                return;
            }

            if (_fits.Samples.GetValueOrDefault(sample) is not { } choice)
            {
                return;
            }

            if (choice.Fitted is { } fitted)
            {
                _notes.Add(
                    "Sample {Sample}: plays {Patch}, an FM patch fitted to pitch region {Region}{Lfo}, for all its notes: {FitDb:0.00} dB from the sample by the fitter's log-mel score over its {Regions} pitch regions ({Notes} notes), where the built-in {BuiltIn} is {BuiltInDb:0.00} dB",
                    label,
                    fitted.Name,
                    choice.FitRegion + 1,
                    fitted.UsesLfo ? " with the song's LFO" : string.Empty,
                    choice.FitDb,
                    choice.Regions,
                    choice.Notes,
                    _routes[instrument].Fallback.Name,
                    choice.BuiltInDb);
            }
            else
            {
                _notes.Add(
                    "Sample {Sample}: keeps the built-in {BuiltIn}: {BuiltInDb:0.00} dB from the sample by the fitter's log-mel score over its {Regions} pitch regions ({Notes} notes), no farther than its closest fit's {FitDb:0.00} dB",
                    label,
                    _routes[instrument].Fallback.Name,
                    choice.BuiltInDb,
                    choice.Regions,
                    choice.Notes,
                    choice.FitDb);
            }
        }

        private static bool IsFlow(Effect effect) =>
            effect is PositionJump or PatternBreak or PatternLoopStart or PatternLoop or PatternDelay or SetSpeed or SetTempo or StopSong;

        /// <summary>"3 "bass"": the 1-based number trackers show, and the name.</summary>
        /// <summary>The nearest note to <paramref name="hz"/>, as a tracker names it (A-4 is 440 Hz), e.g. <c>G3</c>.</summary>
    private static string NoteName(double hz)
    {
        int midi = (int)Math.Round(69 + (12 * Math.Log2(hz / 440)));
        return string.Create(CultureInfo.InvariantCulture, $"{SemitoneNames[((midi % 12) + 12) % 12]}{(midi / 12) - 1}");
    }

    private static string Label(int sample, string name) =>
            string.Create(CultureInfo.InvariantCulture, $"{sample + 1} \"{name}\"");
    }
}

/// <summary>
/// Where the simple mapper plays a drum that loses the DAC to a louder one on the same row (plan T9.4r,
/// docs/adr/0109-pan-swap-cost.md). The PSG has no pan, so a drum on one side that falls to it jumps to the middle.
/// </summary>
internal enum DacLoser
{
    /// <summary>The PSG noise channel, as in ADR 0040: a drum on one side jumps to the middle for that note.</summary>
    Noise,

    /// <summary>A drum on one side plays FM on its own channel, which keeps its side; a centred one still takes the noise.</summary>
    Fm,

    /// <summary>A drum on one side that loses the DAC anywhere plays the PSG noise all song: steadily in the middle, never jumping.</summary>
    NoiseAllSong,

    /// <summary>The mapper's choice until the rounds pick one: <see cref="Noise"/>, as before T9.4r.</summary>
    Default = Noise,
}
