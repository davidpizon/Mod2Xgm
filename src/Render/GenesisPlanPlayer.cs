using System.Globalization;
using Mod2Xgm.Analysis;
using Mod2Xgm.Audio;
using Mod2Xgm.Dmf;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;
using Mod2Xgm.Optimizer;
using Mod2Xgm.Render.Pcm;
using Mod2Xgm.Render.Psg;

namespace Mod2Xgm.Render;

/// <summary>
/// Turns a plan and its playback into register writes, tick by tick (docs/adr/0059-genesis-renderer.md). Each Genesis
/// channel follows the song channel its last note came from (<see cref="PlanCell.Source"/>): a note starts on the tick
/// the tick player starts its sample (so <c>EDx</c> delays it and <c>E9x</c> restarts it), and until the next note or
/// note-off or note cut (docs/adr/0095-voices-stop-with-the-mod-note.md) the channel takes that song channel's pitch, volume
/// and pan from every tick. What the hardware can't change stays as the note started: a DAC note's pitch (its sample's one
/// rate, ADR 0040) and, on the DMF, its volume (XGM and XGM2 bake a fade into its copy, docs/adr/0120-faded-recordings.md), a noise note's rate, a PSG tone's pitch (as its DMF notes, which can't slide; docs/adr/0087-greedy-allocator.md).
/// </summary>
internal sealed class GenesisPlanPlayer
{
    /// <summary>Attenuation of one TL step, in dB (docs/genesis-audio.md).</summary>
    private const double TlStepDb = 0.75;

    private const int MaxTl = 127;

    /// <summary>
    /// The most a sounding FM note's level moves in one tick, in TL steps (0.75 dB each): a bigger jump of the MOD's volume
    /// is spread over the ticks after it, as TL changes in one sample and the step clicks (docs/adr/0123-smooth-fm-volume.md).
    /// </summary>
    private const int MaxStepPerTick = 8;

    /// <summary>Below this attenuation, in TL steps (48 dB), a level goes straight to where it is going: the step is too quiet to hear.</summary>
    private const int QuietSteps = 64;

    private const int PsgOff = PsgLevels.Off;

    /// <summary>A looped DAC sample is played out this long at most, as the DMF's copies are; a longer note goes quiet.</summary>
    private const double LoopSeconds = 10;

    /// <summary>The priority XGM's music PCM plays at, the highest, so a new note always takes over (the XGM writer, M11, sets the real one).</summary>
    private const int PcmPriority = 15;

    /// <summary>
    /// The release rate an FM note is let go with on the tick before its channel's next key-on, on XGM and XGM2 (96 dB in
    /// about 12 ms, inside a 20 ms tick; docs/adr/0124-release-before-key-on.md). A key-on resets the operators' phase but
    /// starts the attack from the level the envelope is at, so keying on over a note that still sounds steps its wave in
    /// one sample: a click.
    /// </summary>
    private const int EarlyReleaseRate = 14;

    // YM3438 registers (docs/genesis-audio.md).
    private const byte RegKeyOn = 0x28;
    private const byte RegLfo = 0x22;
    private const byte RegCh3Mode = 0x27;
    private const byte RegTl = 0x40;
    private const byte RegFnumLow = 0xa0;
    private const byte RegBlockFnumHigh = 0xa4;
    private const byte RegPanLfo = 0xb4;
    private const byte PanLeft = 0x80;
    private const byte PanRight = 0x40;

    /// <summary>The PSG noise channel's control byte (noise register), before its low 4 bits.</summary>
    private const byte PsgNoiseControl = 0xe0;

    /// <summary>A PSG channel's volume latch byte for channel 0, before the channel (bits 6–5) and the volume (low 4 bits).</summary>
    private const byte PsgVolumeLatch = 0x90;

    /// <summary>A PSG tone channel's period latch byte for channel 0, before the channel (bits 6–5) and the period's low 4 bits.</summary>
    private const byte PsgPeriodLatch = 0x80;

    /// <summary>The noise register's white-noise bit.</summary>
    private const byte PsgWhiteNoise = 0x04;

    private readonly GenesisPlan _plan;
    private readonly Song _song;
    private readonly IReadOnlyList<PlaybackTick> _ticks;
    private readonly IReadOnlyList<double> _durations;
    private readonly GenesisChips _chips;
    private readonly ResamplingMode _resampler;
    private readonly ConversionNotes _notes;
    private readonly PlanVoicing _voicing;
    private readonly Track[] _tracks;
    private readonly Dictionary<(int Dac, int Offset, double Volume, NoteFade? Fade), Pcm?> _pcm = [];
    private readonly Dictionary<(int Channel, int Tick), NoteFade> _fades = [];
    private readonly Dictionary<(int Sample, int Frames), double> _noisePeaks = [];
    private readonly Dictionary<string, int> _tallies = [];
    private readonly PanLog? _pans;

    /// <summary>Prepares the chips for a render: LFO and channel 3's special mode off, every channel on both sides.</summary>
    /// <param name="plan">Which channel plays each note, with which voice.</param>
    /// <param name="song">The plan's song: its samples.</param>
    /// <param name="playback">The song played tick by tick.</param>
    /// <param name="durations">Each tick's length in seconds.</param>
    /// <param name="chips">The hardware to write to.</param>
    /// <param name="resampler">How DAC samples are brought to the driver's rate.</param>
    /// <param name="notes">Collects what the render can't play as the plan says.</param>
    /// <param name="pans">Gets the side every sounding note plays on, tick by tick, when given (docs/adr/0108-pan-swap-count.md).</param>
    public GenesisPlanPlayer(GenesisPlan plan, Song song, Playback playback, IReadOnlyList<double> durations, GenesisChips chips, ResamplingMode resampler, ConversionNotes notes, PanLog? pans = null)
    {
        _pans = pans;
        _plan = plan;
        _song = song;
        _ticks = playback.Ticks;
        _durations = durations;
        _chips = chips;
        _resampler = resampler;
        _notes = notes;
        _voicing = new PlanVoicing(plan, song);
        if (chips.Driver != PcmDriver.Dmf)
        {
            FindFades(song, playback);
        }

        _tracks = [.. Enumerable.Range(0, GenesisPlan.ColumnCount).Select(_ => new Track())];
        _tickStarts = new double[durations.Count + 1];
        for (int t = 0; t < durations.Count; t++)
        {
            _tickStarts[t + 1] = _tickStarts[t] + durations[t];
        }

        if (_ticks.Count > 0 && _ticks[0].Channels.Count != song.Channels.Count)
        {
            throw new ArgumentException($"The playback has {_ticks[0].Channels.Count} channels; the song has {song.Channels.Count}.", nameof(playback));
        }

        chips.Ports.WriteRegister(0, RegLfo, 0);
        chips.Ports.WriteRegister(0, RegCh3Mode, 0);
        for (int c = 0; c <= (int)GenesisChannel.Fm6; c++)
        {
            WriteFm(c, RegPanLfo, PanLeft | PanRight);
            _tracks[c].Pan = PanLeft | PanRight;
        }
    }

    /// <summary>
    /// Every note whose volume moves while it sounds, by where it starts, with its fade over the render's tick lengths: XGM
    /// and XGM2 bake it into the note's copy (docs/adr/0120-faded-recordings.md). The DMF writer keeps its copies 3 dB apart
    /// and plays every note at the volume it starts with, so the render of a DMF does too.
    /// </summary>
    private void FindFades(Song song, Playback playback)
    {
        foreach (SongNote note in SongNote.Find(song, playback).Where(n => n.Fade is not null))
        {
            int ticks = Math.Min(note.Ticks, _ticks.Count - note.FirstTick);
            double[] volumes = [.. Enumerable.Range(note.FirstTick, ticks).Select(t => _ticks[t].Channels[note.Channel].Volume)];
            double[] seconds = [.. Enumerable.Range(note.FirstTick, ticks).Select(t => _durations[t])];
            if (NoteFade.Of(volumes, seconds) is { } fade)
            {
                _fades[(note.Channel, note.FirstTick)] = fade;
            }
        }
    }

    /// <summary>
    /// The start of tick <paramref name="t"/>: on a row's first tick, reads the plan's row; lets go of the keys of FM
    /// notes that start or restart on this tick. True when it let go of any, so the caller plays a sample before
    /// <see cref="FinishTick"/>.
    /// </summary>
    public bool BeginTick(int t)
    {
        if (_ticks[t].Tick == 0)
        {
            ReadRow(t);
        }

        bool released = false;
        for (int c = 0; c <= (int)GenesisChannel.Fm6; c++)
        {
            Track track = _tracks[c];
            bool starts = track.StartTick == t && track.Pending is { Voice.Kind: VoiceKind.Fm };
            if (starts || (track.Kind == VoiceKind.Fm && Restarts(track, t)))
            {
                KeyOff(c);
                released = true;
            }
        }

        if (_chips.Driver != PcmDriver.Dmf && t + 1 < _ticks.Count)
        {
            for (int c = 0; c <= (int)GenesisChannel.Fm6; c++)
            {
                Track track = _tracks[c];
                if (track is { Kind: VoiceKind.Fm, Sounding: true, ReleasedEarly: false } && track.StartedAt != t && track.StartTick != t && KeysOnAt(c, t + 1))
                {
                    ReleaseEarly(c);
                }
            }
        }

        return released;
    }

    /// <summary>
    /// True when FM channel <paramref name="c"/> keys a note on at tick <paramref name="t"/>, the tick after the one being
    /// played: a note of the plan's row that starts then (the same choice <see cref="ReadRow"/> makes on the row's first
    /// tick), a note of this row delayed to then (<c>EDx</c>), or this row's note restarted then (<c>E9x</c>).
    /// </summary>
    private bool KeysOnAt(int c, int t)
    {
        Track track = _tracks[c];
        PlaybackTick tick = _ticks[t];
        if (tick.Tick != 0)
        {
            return (track.StartTick == t && track.Pending is { Voice.Kind: VoiceKind.Fm }) || Restarts(track, t);
        }

        IReadOnlyList<PlanRow> rows = _plan.Patterns[tick.Order];
        if (tick.Row >= rows.Count || c >= rows[tick.Row].Cells.Count
            || rows[tick.Row].Cells[c] is not { Note: not null, Voice.Kind: VoiceKind.Fm, Source: { } source, Sample: not null } cell)
        {
            return false;
        }

        if (StartTickIn(t, source) is { } at)
        {
            return at == t;
        }

        // A tone portamento slides the sounding note, and a note delayed past its row's end never plays.
        bool glides = track.Sounding && track.Source == source && cell.Effects.OfType<TonePortamento>().Any();
        return !glides && !cell.Effects.OfType<NoteDelay>().Any();
    }

    /// <summary>
    /// Lets go of FM channel <paramref name="c"/>'s note at <see cref="EarlyReleaseRate"/>, a tick before the channel's next
    /// key-on (docs/adr/0124-release-before-key-on.md), so the note is silent when the next one keys on.
    /// </summary>
    private void ReleaseEarly(int c)
    {
        Track track = _tracks[c];
        FmVoice.WriteRelease(_chips.Ports, c, _plan.FmPatches[track.Voice.Index], EarlyReleaseRate);
        KeyOff(c);
        track.ReleasedEarly = true;
        Tally("{Count} FM notes let go a tick early, so the next note on their channel keys on over silence instead of clicking");
    }

    /// <summary>The rest of tick <paramref name="t"/>: starts notes, then sets every sounding channel's pitch, volume and pan.</summary>
    public void FinishTick(int t)
    {
        for (int c = 0; c < _tracks.Length; c++)
        {
            Track track = _tracks[c];
            if (track.StartTick == t && track.Pending is { } cell)
            {
                Start(c, cell, t);
            }
            else if (Restarts(track, t))
            {
                Restart(c, t);
            }

            if (track.Sounding)
            {
                Update(c, t);
            }

            if (track.CutTick == t)
            {
                // The plan's cut where the MOD's note has gone silent (docs/adr/0095-voices-stop-with-the-mod-note.md).
                Stop(c);
                track.CutTick = -1;
            }
        }

        if (SharesDacPan)
        {
            SetDacPan(t);
        }

        if (_pans is not null)
        {
            LogPans(_pans, t);
        }
    }

    /// <summary>
    /// Adds to <paramref name="pans"/> the side every sounding note of tick <paramref name="t"/> plays on, next to its song
    /// channel's side and why the two differ (docs/adr/0108-pan-swap-count.md). A recording that has played out is silent.
    /// </summary>
    private void LogPans(PanLog pans, int t)
    {
        foreach (Track track in _tracks)
        {
            if (!track.Sounding || track.Source < 0 || track.Sample < 0 || (track.Kind == VoiceKind.Dac && _tickStarts[t] >= track.DacEndsAt))
            {
                continue;
            }

            ChannelTick ch = _ticks[t].Channels[track.Source];
            int original = PlanVoicing.Side(ch.Pan);
            (int heard, PanCause cause) = track.Kind switch
            {
                VoiceKind.Fm => track.Centred ? (0, PanCause.CentredBass) : (original, PanCause.None),
                VoiceKind.Dac when track.Centred => (0, track.SteadyMiddle ? PanCause.SteadyMiddle : PanCause.PreMix),
                VoiceKind.Dac when SharesDacPan => (SideOf(_tracks[(int)GenesisChannel.Fm6].Pan), PanCause.SharedDac),
                VoiceKind.Dac => (original, PanCause.None),
                _ => (0, PanCause.Psg),
            };

            pans.Add(new PanTick(t, _tickStarts[t], _durations[t], track.Sample, track.Source, original, heard, heard == original ? PanCause.None : cause, ch.Volume));
        }
    }

    /// <summary>The side pan bits of register <c>$B4</c> play on: −1 left only, +1 right only, 0 both (or neither).</summary>
    private static int SideOf(byte pan) => pan switch
    {
        PanLeft => -1,
        PanRight => 1,
        _ => 0,
    };

    /// <summary>When each tick starts, in seconds of the render, and after the last one, its end.</summary>
    private readonly double[] _tickStarts;

    /// <summary>True when every PCM voice's side decides the DAC's one pan together (<see cref="GenesisPlan.DacPanClash"/>).</summary>
    private bool SharesDacPan => _plan.DacPanClash != DacPanClash.FirstVoice && _chips.Driver != PcmDriver.Dmf;

    /// <summary>
    /// Pans the DAC (FM6) for every recording sounding on tick <paramref name="t"/>: on their side when they share one,
    /// otherwise as <see cref="GenesisPlan.DacPanClash"/> says. Nothing sounding leaves it as it was.
    /// </summary>
    private void SetDacPan(int t)
    {
        List<(int Side, double Volume)> playing = [];
        foreach (int c in (int[])[(int)GenesisChannel.Fm6, (int)GenesisChannel.Pcm2, (int)GenesisChannel.Pcm3, (int)GenesisChannel.Pcm4])
        {
            // A recording that has played out no longer pulls the DAC to its side, though its note hasn't ended.
            if (c < _tracks.Length && _tracks[c] is { Sounding: true, Kind: VoiceKind.Dac, Source: >= 0 } track && _tickStarts[t] < track.DacEndsAt)
            {
                ChannelTick ch = _ticks[t].Channels[track.Source];

                // A pre-mix of notes from both sides plays from both speakers (docs/adr/0094-pre-mixed-voice-steals.md).
                // So does a recording the allocator holds in the middle (docs/adr/0109-pan-swap-cost.md).
                playing.Add((track.Centred ? 0 : PlanVoicing.Side(ch.Pan), ch.Volume));
            }
        }

        if (playing.Count == 0)
        {
            return;
        }

        int side = playing.All(p => p.Side == playing[0].Side) ? playing[0].Side
            : _plan.DacPanClash == DacPanClash.Louder ? LouderSide(playing)
            : 0;
        byte pan = side < 0 ? PanLeft : side > 0 ? PanRight : (byte)(PanLeft | PanRight);
        Track dac = _tracks[(int)GenesisChannel.Fm6];
        if (pan != dac.Pan)
        {
            WriteFm((int)GenesisChannel.Fm6, RegPanLfo, (byte)(pan | dac.Sensitivity));
            dac.Pan = pan;
        }
    }

    /// <summary>The side of the loudest recording, or both when the loudest on each side tie.</summary>
    private static int LouderSide(List<(int Side, double Volume)> playing)
    {
        double left = playing.Where(p => p.Side < 0).Select(p => p.Volume).DefaultIfEmpty(0).Max();
        double right = playing.Where(p => p.Side > 0).Select(p => p.Volume).DefaultIfEmpty(0).Max();
        double centre = playing.Where(p => p.Side == 0).Select(p => p.Volume).DefaultIfEmpty(0).Max();
        return centre >= Math.Max(left, right) ? 0 : left > right ? -1 : right > left ? 1 : 0;
    }

    /// <summary>Adds the render's tallies to the conversion notes.</summary>
    public void Report()
    {
        foreach ((string template, int count) in _tallies.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            _notes.Add(template, count);
        }
    }

    /// <summary>A row's first tick: note-offs now, notes on the tick their sample starts.</summary>
    private void ReadRow(int t)
    {
        PlaybackTick tick = _ticks[t];
        IReadOnlyList<PlanRow> rows = _plan.Patterns[tick.Order];
        IReadOnlyList<PlanCell>? cells = tick.Row < rows.Count ? rows[tick.Row].Cells : null;
        for (int c = 0; c < _tracks.Length; c++)
        {
            Track track = _tracks[c];
            track.Pending = null;
            track.StartTick = -1;
            track.RestartRow = false;
            track.CutTick = -1;
            if (cells is null || c >= cells.Count || cells[c] is not { } cell)
            {
                continue;
            }

            // Only the plan's own cut stops the voice. The MOD's ECx only sets the volume to 0, which FM and PSG voices already
            // follow, and a later volume command may bring the note back (docs/adr/0095-voices-stop-with-the-mod-note.md).
            if (cell.Effects.OfType<NoteCut>().FirstOrDefault(e => e.Ends) is { } cut)
            {
                track.CutTick = TickInRow(t, cut.Tick) ?? -1;
            }

            if (cell is { NoteOff: true, Note: null })
            {
                Stop(c);
                continue;
            }

            if (cell is not { Note: not null, Voice: not null, Source: { } source, Sample: not null })
            {
                continue;
            }

            track.RestartRow = track.Source == source && cell.Effects.OfType<Retrigger>().Any();
            int? start = StartTickIn(t, source);
            if (start is { } at)
            {
                track.Pending = cell;
                track.StartTick = at;
            }
            else if (track.Sounding && track.Source == source && cell.Effects.OfType<TonePortamento>().Any())
            {
                // A tone portamento slides the playing note: no new start.
                Glide(c, cell);
            }
            else if (cell.Effects.OfType<NoteDelay>().Any())
            {
                Tally("{Count} notes delayed past their row's end (EDx) were left out, as ProTracker plays nothing");
            }
            else
            {
                track.Pending = cell;
                track.StartTick = t;
            }
        }
    }

    /// <summary>The first tick of the row starting at <paramref name="t"/> on which song channel <paramref name="source"/> starts a sample, or <c>null</c>.</summary>
    private int? StartTickIn(int t, int source)
    {
        PlaybackTick first = _ticks[t];
        for (int j = t; j < _ticks.Count; j++)
        {
            PlaybackTick tick = _ticks[j];
            if (tick.Order != first.Order || tick.Row != first.Row || (j > t && tick.Tick <= _ticks[j - 1].Tick))
            {
                break;
            }

            if (tick.Channels[source].Started is not null)
            {
                return j;
            }
        }

        return null;
    }

    /// <summary>The index of tick <paramref name="tick"/> (0-based) of the row starting at <paramref name="t"/>, or <c>null</c> when the row is shorter.</summary>
    private int? TickInRow(int t, int tick)
    {
        int at = t + tick;
        return at < _ticks.Count && _ticks[at].Order == _ticks[t].Order && _ticks[at].Row == _ticks[t].Row && _ticks[at].Tick == tick ? at : null;
    }

    /// <summary>True when the song channel <paramref name="track"/> follows restarts its sample on tick <paramref name="t"/> by <c>E9x</c>.</summary>
    private bool Restarts(Track track, int t) =>
        track is { RestartRow: true, Sounding: true } && track.StartedAt != t && _ticks[t].Channels[track.Source].Started is not null;

    private void Start(int c, PlanCell cell, int t)
    {
        Track track = _tracks[c];
        PlanVoice voice = cell.Voice!.Value;
        int source = cell.Source!.Value;
        int sample = cell.Sample!.Value;
        track.Pending = null;
        track.StartTick = -1;
        if (track.Sounding && track.Kind != voice.Kind)
        {
            Stop(c);
        }

        track.Source = source;
        track.Sample = sample;
        track.Voice = voice;
        track.StartedAt = t;

        // From here the track follows the row's song channel, so the row's E9x restarts this note: also the first note the
        // channel plays, or one that takes it over from another song channel, which ReadRow couldn't count as following it.
        track.RestartRow = cell.Effects.OfType<Retrigger>().Any();
        ChannelTick ch = _ticks[t].Channels[source];
        switch (voice.Kind)
        {
            case VoiceKind.Fm:
                if (c == (int)GenesisChannel.Fm6 && _chips.Dac is DmfDac dmf)
                {
                    // 17 00: the DMF switches FM6 back from the DAC before an FM note.
                    dmf.Stop();
                    dmf.SetDacMode(false);
                }

                LoadPatch(c, voice.Index);
                track.ReleasedEarly = false;
                track.Centred = _voicing.CentredBass(ch.Pitch, sample, ch.Pan);
                track.Gain = FmGain(track, sample, voice.Index) * cell.Level;
                track.Cents = cell.Cents;
                track.Sounding = true;
                Update(c, t);
                KeyOn(c);
                break;
            case VoiceKind.Dac:
                // A pre-mix of notes from both sides plays from both speakers, on a DMF too (docs/adr/0094-pre-mixed-voice-steals.md).
                // So does a recording the allocator holds in the middle (docs/adr/0109-pan-swap-cost.md).
                track.SteadyMiddle = cell.Centred && _plan.DacSamples[voice.Index].Mix is not { Centred: true };
                track.Centred = cell.Centred || _plan.DacSamples[voice.Index].Mix is { Centred: true };
                track.Sounding = PlayDac(c, _plan.DacSamples[voice.Index], voice.Index, ch, t);
                break;
            case VoiceKind.PsgTone:
                WriteTonePeriod(c, PsgSetting.NearestPeriod(PlanVoicing.Hz(_voicing.HeardSemitones(ch.Pitch, sample, out _))));
                track.Gain = _voicing.ToneLevel(sample).Gain * cell.Level;
                StartNoise(track, ch, t);
                break;
            default:
                int rate = 2 - _voicing.NoiseNote(sample);
                _chips.WritePsg((byte)(PsgNoiseControl | PsgWhiteNoise | rate));
                track.Gain = _voicing.NoiseLevel(sample).Gain;
                StartNoise(track, ch, t);
                break;
        }
    }

    /// <summary>E9x: the note starts again with what it plays now.</summary>
    private void Restart(int c, int t)
    {
        Track track = _tracks[c];
        track.StartedAt = t;
        ChannelTick ch = _ticks[t].Channels[track.Source];
        switch (track.Kind)
        {
            case VoiceKind.Fm:
                if (track.ReleasedEarly)
                {
                    // The patch's own release again, for when this note ends.
                    FmVoice.WriteRelease(_chips.Ports, c, _plan.FmPatches[track.Voice.Index]);
                    track.ReleasedEarly = false;
                }

                KeyOn(c);
                break;
            case VoiceKind.Dac:
                track.Sounding = PlayDac(c, _plan.DacSamples[track.Voice.Index], track.Voice.Index, ch, t);
                break;
            default:
                StartNoise(track, ch, t);
                break;
        }
    }

    /// <summary>A note under a tone portamento: the playing note keeps going, with the cell's voice if it changed.</summary>
    private void Glide(int c, PlanCell cell)
    {
        Track track = _tracks[c];
        PlanVoice voice = cell.Voice!.Value;
        int sample = cell.Sample!.Value;
        if (voice.Kind != track.Kind)
        {
            return;
        }

        track.Sample = sample;
        if (voice.Kind == VoiceKind.Fm)
        {
            if (voice.Index != track.Voice.Index)
            {
                LoadPatch(c, voice.Index);
            }

            track.Gain = FmGain(track, sample, voice.Index);
        }

        track.Voice = voice;
    }

    /// <summary>
    /// The factor everything plays at on XGM and XGM2: 1, or less under a retry's rule (<see cref="GenesisPlan.SongGainDb"/>,
    /// docs/adr/0112-retry-list.md). A DMF's plan never has one.
    /// </summary>
    private double SongGain => Math.Pow(10, _plan.SongGainDb / 20);

    /// <summary>The factor on the MOD volume of <paramref name="sample"/> on FM patch <paramref name="patch"/>, as this note is panned.</summary>
    private double FmGain(Track track, int sample, int patch) =>
        _voicing.FmLevel(sample, patch).Gain * (track.Centred ? PlanVoicing.CentredGain : 1);

    private void Stop(int c)
    {
        Track track = _tracks[c];
        if (!track.Sounding)
        {
            return;
        }

        switch (track.Kind)
        {
            case VoiceKind.Fm:
                KeyOff(c);
                break;
            case VoiceKind.Dac:
                StopDac(c);
                break;
            default:
                WritePsgVolume(c, track, PsgOff);
                break;
        }

        track.Sounding = false;
    }

    /// <summary>Sets a sounding channel's pitch, volume and pan from tick <paramref name="t"/> of the song channel it follows.</summary>
    private void Update(int c, int t)
    {
        Track track = _tracks[c];
        ChannelTick ch = _ticks[t].Channels[track.Source];
        if (c <= (int)GenesisChannel.Fm6 && !(track.Kind == VoiceKind.Dac && SharesDacPan))
        {
            int side = track.Centred ? 0 : PlanVoicing.Side(ch.Pan);
            byte pan = side < 0 ? PanLeft : side > 0 ? PanRight : (byte)(PanLeft | PanRight);
            if (pan != track.Pan)
            {
                WriteFm(c, RegPanLfo, (byte)(pan | track.Sensitivity));
                track.Pan = pan;
            }
        }

        // The DAC has no pitch or volume of its own: both were set when the note started, a fade baked into its copy.
        switch (track.Kind)
        {
            case VoiceKind.Fm:
                UpdateFm(c, track, ch, starting: track.StartedAt == t);
                break;
            case VoiceKind.PsgNoise:
            case VoiceKind.PsgTone:
                UpdateNoise(c, track, ch, _durations[t]);
                break;
        }
    }

    private void UpdateFm(int c, Track track, ChannelTick ch, bool starting)
    {
        if (ch.Pitch.PlaybackRateHz > 0)
        {
            double hz = PlanVoicing.Hz(_voicing.HeardSemitones(ch.Pitch, track.Sample, out _) + (track.Cents / 100));
            int blockFnum = BlockFnum(hz);
            if (blockFnum != track.BlockFnum)
            {
                WriteFm(c, RegBlockFnumHigh, (byte)(blockFnum >> 8));
                WriteFm(c, RegFnumLow, (byte)blockFnum);
                track.BlockFnum = blockFnum;
            }
        }

        double level = ch.Volume * track.Gain * SongGain;
        int target = level <= 0 ? MaxTl : (int)Math.Round(-20 * Math.Log10(level) / TlStepDb, MidpointRounding.AwayFromZero);
        // DefleMask sets a channel's volume at once, so the DMF render does too.
        int steps = starting || track.LevelSteps < 0 || _chips.Driver == PcmDriver.Dmf ? target : Ramp(track.LevelSteps, target);
        track.LevelSteps = steps;
        FmPatch patch = _plan.FmPatches[track.Voice.Index];
        foreach (int op in VoiceLevel.Carriers(patch.Algorithm))
        {
            int tl = Math.Clamp(patch.Operators[op].Tl + steps, 0, MaxTl);
            if (tl != track.CarrierTl[op])
            {
                WriteFm(c, (byte)(RegTl + FmVoice.SlotOffsets[op]), (byte)tl);
                track.CarrierTl[op] = tl;
            }
        }
    }

    /// <summary>
    /// The level a sounding note plays this tick, in TL steps, on its way from <paramref name="heard"/> to <paramref name="target"/>:
    /// at most <see cref="MaxStepPerTick"/> closer, or all the way once both are below <see cref="QuietSteps"/>.
    /// </summary>
    private static int Ramp(int heard, int target)
    {
        if (Math.Min(heard, target) >= QuietSteps || Math.Abs(target - heard) <= MaxStepPerTick)
        {
            return target;
        }

        return target > heard ? heard + MaxStepPerTick : heard - MaxStepPerTick;
    }

    /// <summary>The block (bits 13–11) and F-number (bits 10–0) closest to <paramref name="hz"/>: the lowest block that holds it, for the finest steps.</summary>
    private int BlockFnum(double hz) => FmVoice.BlockFnum(hz, _chips.SampleRateHz);

    private void LoadPatch(int c, int index)
    {
        Track track = _tracks[c];
        FmPatch patch = _plan.FmPatches[index];
        FmVoice.LoadPatch(_chips.Ports, c, patch);
        Array.Fill(track.CarrierTl, -1);

        // The plan has no LFO rate yet, so the LFO stays off (plan M9 picks one per song, docs/adr/0069-fm-patch-fitting.md);
        // the sensitivities are still the patch's, as the DMF stores them.
        byte sensitivity = (byte)((patch.Ams << 4) | patch.Fms);
        if (sensitivity != track.Sensitivity)
        {
            track.Sensitivity = sensitivity;
            WriteFm(c, RegPanLfo, (byte)(track.Pan | sensitivity));
        }
    }

    private void KeyOn(int c) => _chips.Ports.WriteRegister(0, RegKeyOn, (byte)(0xf0 | KeyOnChannel(c)));

    private void KeyOff(int c) => _chips.Ports.WriteRegister(0, RegKeyOn, (byte)KeyOnChannel(c));

    /// <summary>The channel bits of register <c>$28</c>: 0–2 for FM1–3, 4–6 for FM4–6.</summary>
    private static int KeyOnChannel(int c) => c < 3 ? c : c + 1;

    /// <summary>Writes an FM channel's register: <paramref name="register"/> is the channel 1 address, offset by the channel within its part.</summary>
    private void WriteFm(int c, byte register, byte value) => FmVoice.Write(_chips.Ports, c, register, value);

    private void StartNoise(Track track, ChannelTick ch, int t)
    {
        track.NoiseFrame = ch.Started?.StartFrame ?? 0;
        int frames = Math.Max(1, (int)(ch.Pitch.PlaybackRateHz * _durations[t]));
        track.NoiseWindow = frames;
        track.NoisePeak = NoisePeak(track.Sample, frames);
        track.Volume = -1;
        track.Sounding = true;
    }

    /// <summary>
    /// The noise follows its sample's loudness tick by tick, as the DMF's noise instrument does: the tick's stretch of the
    /// sample against its loudest stretch, times the MOD volume, at the nearest of the chip's levels (docs/adr/0067-psg-volume-levels.md). Past a one-shot sample's end it is silent.
    /// </summary>
    private void UpdateNoise(int c, Track track, ChannelTick ch, double seconds)
    {
        Sample sample = _song.Samples[track.Sample];
        double rms = WindowRms(sample, (int)track.NoiseFrame, track.NoiseWindow);
        double level = ch.Volume * track.Gain * SongGain * (track.NoisePeak > 0 ? rms / track.NoisePeak : 0);
        int volume = PsgLevels.Nearest(level);
        WritePsgVolume(c, track, volume);
        track.NoiseFrame += ch.Pitch.PlaybackRateHz * seconds;
    }

    private void WritePsgVolume(int c, Track track, int volume)
    {
        if (volume != track.Volume)
        {
            _chips.WritePsg((byte)(PsgVolumeLatch | ((c - (int)GenesisChannel.Psg1) << 5) | volume));
            track.Volume = volume;
        }
    }

    /// <summary>Sets a PSG tone channel's 10-bit period: the latch byte's low 4 bits, then the high 6.</summary>
    private void WriteTonePeriod(int c, int period)
    {
        int clamped = Math.Clamp(period, 1, PsgSetting.MaxPeriod);
        _chips.WritePsg((byte)(PsgPeriodLatch | ((c - (int)GenesisChannel.Psg1) << 5) | (clamped & 0xf)));
        _chips.WritePsg((byte)(clamped >> 4));
    }

    /// <summary>The PCM channel column <paramref name="c"/> plays on: FM6 is the first, then the extra PCM columns.</summary>
    private static int PcmChannel(int c) => c == (int)GenesisChannel.Fm6 ? 0 : c - (int)GenesisChannel.Pcm2 + 1;

    /// <summary>The loudest stretch of <paramref name="frames"/> frames in the noise sample, as the DMF's macro is scaled to.</summary>
    private double NoisePeak(int index, int frames)
    {
        if (!_noisePeaks.TryGetValue((index, frames), out double peak))
        {
            Sample sample = _song.Samples[index];
            int end = sample.Loop is { } loop ? loop.Start + loop.Length : sample.Length;
            for (int from = 0; from < end; from += frames)
            {
                peak = Math.Max(peak, WindowRms(sample, from, Math.Min(frames, end - from)));
            }

            _noisePeaks[(index, frames)] = peak;
        }

        return peak;
    }

    /// <summary>The RMS of <paramref name="frames"/> frames from <paramref name="from"/>, through the loop; 0 past a one-shot sample's end.</summary>
    private static double WindowRms(Sample sample, int from, int frames)
    {
        double sum = 0;
        for (int i = 0; i < frames; i++)
        {
            int at = from + i;
            if (sample.Loop is { } loop && at >= loop.Start + loop.Length)
            {
                at = loop.Start + ((at - loop.Start) % loop.Length);
            }

            if (at >= sample.Length)
            {
                break;
            }

            sum += (double)sample.Data[at] * sample.Data[at];
        }

        return frames > 0 ? Math.Sqrt(sum / frames) : 0;
    }

    /// <summary>Plays a DAC note: its sample from the frame the tick player starts at, at the volume it starts with and, on XGM and XGM2, with its fade. False when it stays silent.</summary>
    private bool PlayDac(int c, DacSample dac, int index, ChannelTick ch, int t)
    {
        int offset = ch.Started?.StartFrame ?? 0;
        NoteFade? fade = dac.Mix is null ? _fades.GetValueOrDefault((_tracks[c].Source, t)) : null;
        if (Prepare(dac, index, offset, ch.Volume, _tracks[c].SteadyMiddle, fade) is not { } pcm)
        {
            StopDac(c);
            return false;
        }

        switch (_chips.Dac)
        {
            case DmfDac dmf:
                dmf.SetDacMode(true);
                dmf.Play(pcm.Data16!, pcm.RateHz);
                break;
            case XgmPcmDriver xgm:
                xgm.Play(PcmChannel(c), pcm.Data8!, PcmPriority);
                break;
            case Xgm2PcmDriver xgm2:
                // Music plays at the low priority, as SGDK's xgm2tool writes it, so a game's sound effect can take the channel
                // (docs/adr/0099-xgm2-writer-records-the-player.md).
                xgm2.Play(PcmChannel(c), pcm.Data8!, Xgm2PcmDriver.LowPriority, dac.HalfSpeed);
                break;
        }

        if (pcm.Data8 is { } data8)
        {
            _chips.Recorder?.PcmPlay(PcmChannel(c), data8, _chips.Driver == PcmDriver.Xgm2 && dac.HalfSpeed, dac.Sample, pcm.Name);
        }

        // The driver plays the data once through, a loop unrolled into it, after its start delay; buffered XGM2 plays the
        // first fill twice (docs/adr/0091-shrinking-recordings.md, docs/adr/0066-pcm-drivers-checked-against-sgdk.md).
        _tracks[c].DacEndsAt = _tickStarts[t] + ((double)(pcm.Data16?.Length ?? pcm.Data8!.Length) / pcm.RateHz) + DriverLagSeconds();
        return true;
    }

    /// <summary>How much later than its data's length a play ends: the driver's start delay, and buffered XGM2's repeated first fill.</summary>
    private double DriverLagSeconds() => _chips.Dac switch
    {
        XgmPcmDriver { Buffered: true } => XgmPcmDriver.StartLeadBeats / XgmPcmDriver.BeatRateHz(_chips.YmClockHz),
        Xgm2PcmDriver { Buffered: true } xgm2 => (Xgm2PcmDriver.StartDelayBeats + Xgm2PcmDriver.RepeatedBeats - xgm2.Lead) / Xgm2PcmDriver.BeatRateHz(_chips.YmClockHz),
        _ => 0,
    };

    private void StopDac(int c)
    {
        switch (_chips.Dac)
        {
            case DmfDac dmf:
                dmf.Stop();
                break;
            case XgmPcmDriver xgm:
                xgm.Stop(PcmChannel(c), PcmPriority);
                break;
            case Xgm2PcmDriver xgm2:
                xgm2.Stop(PcmChannel(c));
                break;
        }

        if (_chips.Driver != PcmDriver.Dmf)
        {
            _chips.Recorder?.PcmStop(PcmChannel(c));
        }
    }

    /// <summary>
    /// The PCM a DAC note plays, made on first use: the sample played out from <paramref name="offset"/>, at the driver's
    /// rate, at <paramref name="volume"/>; <c>null</c> when it is silent. The DMF keeps DefleMask's rate codes and makes
    /// its copies 3 dB apart, as the DMF writer does; XGM and XGM2 keep 3 dB steps too, at their driver's exact rate. A
    /// recording <paramref name="held"/> in the middle plays <see cref="DacSample.HeldSteps"/> steps quieter, so from both
    /// speakers it is as loud as from one (<see cref="PlanVoicing.CentredGain"/>, docs/adr/0109-pan-swap-cost.md).
    /// On XGM and XGM2, a note whose volume moves has <paramref name="fade"/> baked in (docs/adr/0120-faded-recordings.md).
    /// </summary>
    private Pcm? Prepare(DacSample dac, int index, int offset, double volume, bool held = false, NoteFade? fade = null)
    {
        bool dmf = _chips.Driver == PcmDriver.Dmf;
        if (dac.Mix is { } mix)
        {
            return PrepareMix(dac, mix, index);
        }

        if (!dmf && volume > 0)
        {
            // XGM and XGM2 store a copy per 3 dB step of loudness, or per several steps when the shrinker shares them: the
            // note plays at its copy's, as the capacity check and the shrinker counted it (docs/adr/0091-shrinking-recordings.md,
            // docs/adr/0099-xgm2-writer-records-the-player.md).
            int stored = RecordingShrinker.StoredSteps(DmfPatternTranslator.DacVolumeSteps(volume) + (held ? dac.HeldSteps : 0), dac.GainDb + _plan.SongGainDb, Math.Max(1, dac.LoudnessShareSteps));
            volume = Math.Pow(10, -stored * DmfPatternTranslator.DacVolumeStepDb / 20);
        }
        else
        {
            volume = Math.Min(1, volume * Math.Pow(10, dac.GainDb / 20));
        }

        int steps = volume <= 0 ? int.MaxValue : Math.Max(0, (int)Math.Round(-20 * Math.Log10(volume) / DmfPatternTranslator.DacVolumeStepDb, MidpointRounding.AwayFromZero));
        double key = dmf ? steps : volume;
        if (_pcm.TryGetValue((index, offset, key, fade), out Pcm? pcm))
        {
            return pcm;
        }

        Sample sample = SampleProcessor.CutAfterLoop(_song.Samples[dac.Sample]);
        float[] data = sample.Data;
        if (sample.Loop is not null)
        {
            data = SampleProcessor.Unroll(sample, Math.Max(sample.Length, (int)Math.Ceiling(LoopSeconds * dac.PlaybackRateHz) + offset + 1));
        }

        data = data[Math.Min(offset, data.Length)..];
        if (fade is not null)
        {
            data = fade.Apply(data, dac.PlaybackRateHz);
        }

        string name = string.Create(CultureInfo.InvariantCulture, $"{dac.Sample + 1} \"{sample.Name}\"");
        if (volume <= 0 || data.Length == 0 || (dmf && steps > DmfPatternTranslator.MaxDacVolumeSteps))
        {
            pcm = null;
        }
        else if (dmf)
        {
            int rate = DmfSongBuilder.DacRateHz(dac.PlaybackRateHz);
            data = SampleProcessor.ApplyGain(SampleProcessor.Resample(data, dac.PlaybackRateHz, rate, _resampler), Math.Pow(10, -steps * DmfPatternTranslator.DacVolumeStepDb / 20));

            // The DMF writer makes and notes the same copies; the render doesn't note them again.
            short[] pcm16 = SampleProcessor.ToSigned16(data, name, new ConversionNotes(Serilog.Core.Logger.None));
            pcm = pcm16.Length == 0 ? null : new Pcm(pcm16, null, rate, name);
        }
        else
        {
            double rate = _chips.Driver == PcmDriver.Xgm ? XgmPcmDriver.BeatRateHz(_chips.YmClockHz) : Xgm2PcmDriver.BeatRateHz(_chips.YmClockHz) / (dac.HalfSpeed ? 2 : 1);
            sbyte[] pcm8 = SampleProcessor.ToSigned8(SampleProcessor.ApplyGain(SampleProcessor.Resample(data, dac.PlaybackRateHz, rate, _resampler), volume), name, _notes);
            pcm = pcm8.Length == 0 ? null : new Pcm(null, Stored(pcm8, dac.HalfSpeed), (int)Math.Round(rate), name);
        }

        _pcm[(index, offset, key, fade)] = pcm;
        return pcm;
    }

    /// <summary>
    /// The PCM of a recording the mapper mixed (a pre-mix or its tail, docs/adr/0094-pre-mixed-voice-steals.md): its points
    /// from the start, at the loudness they hold, at the driver's rate; <c>null</c> when it is empty.
    /// </summary>
    private Pcm? PrepareMix(DacSample dac, DacMix mix, int index)
    {
        if (_pcm.TryGetValue((index, 0, 1, null), out Pcm? pcm))
        {
            return pcm;
        }

        if (_chips.Driver == PcmDriver.Dmf)
        {
            int rate = DmfSongBuilder.DacRateHz(dac.PlaybackRateHz);
            short[] pcm16 = SampleProcessor.ToSigned16(SampleProcessor.Resample(mix.Points, dac.PlaybackRateHz, rate, _resampler), mix.Name, new ConversionNotes(Serilog.Core.Logger.None));
            pcm = pcm16.Length == 0 ? null : new Pcm(pcm16, null, rate, mix.Name);
        }
        else
        {
            double rate = _chips.Driver == PcmDriver.Xgm ? XgmPcmDriver.BeatRateHz(_chips.YmClockHz) : Xgm2PcmDriver.BeatRateHz(_chips.YmClockHz);
            float[] points = SampleProcessor.Resample(mix.Points, dac.PlaybackRateHz, rate, _resampler);
            sbyte[] pcm8 = SampleProcessor.ToSigned8(_plan.SongGainDb == 0 ? points : SampleProcessor.ApplyGain(points, SongGain), mix.Name, _notes);
            pcm = pcm8.Length == 0 ? null : new Pcm(null, Stored(pcm8, halfSpeed: false), (int)Math.Round(rate), mix.Name);
        }

        _pcm[(index, 0, 1, null)] = pcm;
        return pcm;
    }

    /// <summary>
    /// A recording as the driver gets it: on XGM2, after the silent head that the driver's repeated first fill plays twice
    /// (docs/adr/0119-xgm2-pcm-lead.md); on XGM, as it is.
    /// </summary>
    private sbyte[] Stored(sbyte[] pcm8, bool halfSpeed) => _chips.Driver == PcmDriver.Xgm2 ? Xgm2PcmDriver.Padded(pcm8, halfSpeed) : pcm8;

    private void Tally(string template) => _tallies[template] = _tallies.GetValueOrDefault(template) + 1;

    /// <summary>A DAC note's data for the driver: 16-bit for the DMF, 8-bit for XGM / XGM2; <paramref name="Name"/> says which sample it is.</summary>
    private sealed record Pcm(short[]? Data16, sbyte[]? Data8, int RateHz, string Name);

    /// <summary>One Genesis channel: the note it plays and what was last written for it.</summary>
    private sealed class Track
    {
        /// <summary>The song channel the note came from, or −1.</summary>
        public int Source { get; set; } = -1;

        /// <summary>When the recording it plays runs out, in seconds of the render.</summary>
        public double DacEndsAt { get; set; }

        /// <summary>The song sample the note plays.</summary>
        public int Sample { get; set; } = -1;

        /// <summary>How far an FM note plays from its pitch (<see cref="PlanCell.Cents"/>).</summary>
        public double Cents { get; set; }

        /// <summary>True when the FM note was let go a tick before the channel's next key-on, at the fast release (<see cref="EarlyReleaseRate"/>).</summary>
        public bool ReleasedEarly { get; set; }

        /// <summary>The voice the note plays with.</summary>
        public PlanVoice Voice { get; set; }

        /// <summary>
        /// True when the note plays centred whatever its channel's pan: an FM bass note (<see cref="PlanVoicing.CentredBass"/>),
        /// a pre-mix of both sides, or a recording held in the middle (<see cref="PlanCell.Centred"/>).
        /// </summary>
        public bool Centred { get; set; }

        /// <summary>True when a recording plays centred because the allocator holds it in the middle (<see cref="PlanCell.Centred"/>), not because it is a pre-mix.</summary>
        public bool SteadyMiddle { get; set; }

        /// <summary>The kind of <see cref="Voice"/>.</summary>
        public VoiceKind Kind => Voice.Kind;

        /// <summary>True while a note plays.</summary>
        public bool Sounding { get; set; }

        /// <summary>The factor on the MOD volume (ADR 0050).</summary>
        public double Gain { get; set; } = 1;

        /// <summary>A note of this row waiting for its tick, or <c>null</c>.</summary>
        public PlanCell? Pending { get; set; }

        /// <summary>The tick <see cref="Pending"/> starts on, or −1.</summary>
        public int StartTick { get; set; } = -1;

        /// <summary>The tick the note last started on.</summary>
        public int StartedAt { get; set; } = -1;

        /// <summary>The tick the note is cut on (a <see cref="NoteCut"/> that <see cref="NoteCut.Ends"/> the note, in this row's cell), or −1.</summary>
        public int CutTick { get; set; } = -1;

        /// <summary>
        /// True on a row whose cell restarts the note (E9x) from the song channel the track follows: set when the row is read
        /// for a note the track already follows, and when the row's note starts.
        /// </summary>
        public bool RestartRow { get; set; }

        /// <summary>The block and F-number last written, or −1.</summary>
        public int BlockFnum { get; set; } = -1;

        /// <summary>The TL last written to each operator as a carrier, or −1.</summary>
        public int[] CarrierTl { get; } = [-1, -1, -1, -1];

        /// <summary>The attenuation the note plays at, in TL steps on top of its patch, or −1 before its first tick.</summary>
        public int LevelSteps { get; set; } = -1;

        /// <summary>The pan bits last written.</summary>
        public byte Pan { get; set; }

        /// <summary>The loaded patch's AMS and FMS bits of register <c>$B4</c>, written with every pan change.</summary>
        public byte Sensitivity { get; set; }

        /// <summary>The PSG volume last written (0 loudest, 15 off), or −1.</summary>
        public int Volume { get; set; } = -1;

        /// <summary>Where in its sample a noise note is, in frames.</summary>
        public double NoiseFrame { get; set; }

        /// <summary>Frames of the sample one tick covered when the noise note started.</summary>
        public int NoiseWindow { get; set; } = 1;

        /// <summary>The RMS of the sample's loudest stretch of <see cref="NoiseWindow"/> frames.</summary>
        public double NoisePeak { get; set; }
    }
}
