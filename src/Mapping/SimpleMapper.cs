using System.Globalization;
using Mod2Xgm.Model;

namespace Mod2Xgm.Mapping;

/// <summary>
/// The first mapper (plan T5.3, docs/adr/0040-simple-mapper.md): every note goes to
/// the voice that can play its sample best, decided <b>per instrument</b> by simple rules, until the optimizer
/// (plan M9) decides by measured accuracy.
/// <list type="bullet">
/// <item>A sample that loops is a sustained tone: <b>FM</b>, with a built-in patch picked by brightness.</item>
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
internal sealed class SimpleMapper : IChannelMapper
{
    /// <summary>MOD channels beyond this many have no FM channel (FM1–5; FM6 is the DAC) and are left out.</summary>
    private const int MappedChannels = 5;

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

        Run run = new(song, playback, options, notes);
        return run.Execute();
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
    private sealed record Route(Primary Primary, FmPatch Fallback, bool NoiseLike, bool Required, Why? Reason = null);

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
        private int _dacDropped;
        private int _noiseToFm;
        private int _noiseDropped;

        // The MOD channel whose note the DAC and the noise channel last started, so silencing a track stops only its own sound.
        private int _dacSource = -1;
        private int _noiseSource = -1;

        public Run(Song song, Playback playback, MappingOptions options, ConversionNotes notes)
        {
            _song = song;
            _playback = playback;
            _options = options;
            _notes = notes;
            _routes = [.. song.Instruments.Select(Classify)];
            _instrument = [.. Enumerable.Repeat(-1, song.Channels.Count)];
            _volume = [.. Enumerable.Repeat(1.0, song.Channels.Count)];
            _sounding = [.. Enumerable.Repeat(Sounding.Nothing, song.Channels.Count)];
        }

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
            return new GenesisPlan(_song.Title, patterns, _song.LoopOrder, _song.Timing, _patches, dac);
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
            FmPatch patch = sample.Loop is null ? FmPatchLibrary.ForOneShot(traits.Brightness) : FmPatchLibrary.ForSustained(traits.Brightness);
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

            if (sample.Loop is not null)
            {
                return new Route(Primary.Fm, patch, traits.NoiseLike, false, new Why("Sample {Sample}: it loops, so it is a sustained tone and is played by the {Patch} FM patch", [label, patch.Name]));
            }

            if (_options.UseDac)
            {
                return new Route(Primary.Dac, patch, traits.NoiseLike, false);
            }

            if (traits.NoiseLike)
            {
                return new Route(Primary.Noise, patch, true, false, new Why("Sample {Sample}: it is noise and the DAC is off (--no-dac), so it is played by the PSG noise channel", [label]));
            }

            return new Route(Primary.Fm, patch, false, false, new Why("Sample {Sample}: the DAC is off (--no-dac), so it is played by the {Patch} FM patch", [label, patch.Name]));
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
                }

                pending.Add((c, own));
            }

            Place(candidates, planned);
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
                    else if (route.NoiseLike)
                    {
                        _dacToNoise++;
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
            FmPatch patch = _routes[candidate.Instrument].Fallback;
            int index = _patches.IndexOf(patch);
            if (index < 0)
            {
                index = _patches.Count;
                _patches.Add(patch);
            }

            Sound(candidate, (GenesisChannel)candidate.Channel, new PlanVoice(VoiceKind.Fm, index), Sounding.Fm, planned);
        }

        /// <summary>Starts the note on <paramref name="column"/> and stops what its MOD channel was sounding elsewhere.</summary>
        private void Sound(Candidate candidate, GenesisChannel column, PlanVoice voice, Sounding kind, PlanCell[] planned)
        {
            int c = candidate.Channel;
            if (_sounding[c] != kind)
            {
                GenesisChannel? previous = _sounding[c] switch
                {
                    Sounding.Fm => (GenesisChannel)c,
                    Sounding.Noise => GenesisChannel.Psg4,
                    _ => null,
                };
                if (previous is { } stop && planned[(int)stop].Note is null)
                {
                    planned[(int)stop] = planned[(int)stop] with { NoteOff = true, Source = c };
                }
            }

            _sounding[c] = kind;
            if (column == GenesisChannel.Fm6 && kind == Sounding.Dac)
            {
                _dacSource = c;
            }
            else if (column == GenesisChannel.Psg4)
            {
                _noiseSource = c;
            }

            planned[(int)column] = new PlanCell(candidate.Note, voice, candidate.Volume, false, [], c, _song.Instruments[candidate.Instrument].Sample);
        }

        /// <summary>
        /// Stops what MOD channel <paramref name="c"/> is sounding, for a note of an ignored sample: the track is silent
        /// until its next note. The DAC and the noise channel are stopped only when this track started their sound.
        /// </summary>
        private void Silence(int c, PlanCell[] planned)
        {
            GenesisChannel? column = _sounding[c] switch
            {
                Sounding.Fm => (GenesisChannel)c,
                Sounding.Dac when _dacSource == c => GenesisChannel.Fm6,
                Sounding.Noise when _noiseSource == c => GenesisChannel.Psg4,
                _ => null,
            };
            if (column is { } stop && planned[(int)stop].Note is null)
            {
                planned[(int)stop] = planned[(int)stop] with { NoteOff = true, Source = c };
            }

            _sounding[c] = Sounding.Nothing;
        }

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
            if (_routes[instrument].Reason is { } why && _explained.Add(instrument))
            {
                _notes.Add(why.Template, why.Values);
            }
        }

        private static bool IsFlow(Effect effect) =>
            effect is PositionJump or PatternBreak or PatternLoopStart or PatternLoop or PatternDelay or SetSpeed or SetTempo or StopSong;

        /// <summary>"3 "bass"": the 1-based number trackers show, and the name.</summary>
        private static string Label(int sample, string name) =>
            string.Create(CultureInfo.InvariantCulture, $"{sample + 1} \"{name}\"");
    }
}
