using System.Globalization;
using Mod2Xgm.Audio;
using Mod2Xgm.Dmf;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;
using Mod2Xgm.Render.Pcm;

namespace Mod2Xgm.Render;

/// <summary>
/// Turns a plan and its playback into register writes, tick by tick (docs/adr/0059-genesis-renderer.md). Each Genesis
/// channel follows the song channel its last note came from (<see cref="PlanCell.Source"/>): a note starts on the tick
/// the tick player starts its sample (so <c>EDx</c> delays it and <c>E9x</c> restarts it), and until the next note or
/// note-off the channel takes that song channel's pitch, volume and pan from every tick. What the hardware can't change
/// stays as the note started: a DAC note's pitch (its sample's one rate, ADR 0040) and volume, a noise note's rate.
/// </summary>
internal sealed class GenesisPlanPlayer
{
    /// <summary>Attenuation of one TL step, in dB (docs/genesis-audio.md).</summary>
    private const double TlStepDb = 0.75;

    private const int MaxTl = 127;

    /// <summary>Attenuation of one PSG volume step, in dB; 15 is off.</summary>
    private const double PsgStepDb = 2;

    private const int PsgOff = 15;

    /// <summary>F-numbers are 11 bits.</summary>
    private const int MaxFnum = 2047;

    private const int MaxBlock = 7;

    /// <summary>A looped DAC sample is played out this long at most, as the DMF's copies are; a longer note goes quiet.</summary>
    private const double LoopSeconds = 10;

    /// <summary>The priority the music's PCM plays at, the highest, so a new note always takes over (the XGM writers, M10–M11, set the real one).</summary>
    private const int PcmPriority = 15;

    /// <summary>The PCM channel music notes play on.</summary>
    private const int PcmChannel = 0;

    // YM3438 registers (docs/genesis-audio.md).
    private const byte RegKeyOn = 0x28;
    private const byte RegLfo = 0x22;
    private const byte RegCh3Mode = 0x27;
    private const byte RegDetuneMult = 0x30;
    private const byte RegTl = 0x40;
    private const byte RegRsAr = 0x50;
    private const byte RegAmDr = 0x60;
    private const byte RegD2R = 0x70;
    private const byte RegSlRr = 0x80;
    private const byte RegSsgEg = 0x90;
    private const byte RegFnumLow = 0xa0;
    private const byte RegBlockFnumHigh = 0xa4;
    private const byte RegFeedbackAlgorithm = 0xb0;
    private const byte RegPanLfo = 0xb4;
    private const byte PanLeft = 0x80;
    private const byte PanRight = 0x40;

    /// <summary>The PSG noise channel's control byte (noise register) and volume byte, before their low 4 bits.</summary>
    private const byte PsgNoiseControl = 0xe0;

    private const byte PsgNoiseVolume = 0xf0;

    /// <summary>The noise register's white-noise bit.</summary>
    private const byte PsgWhiteNoise = 0x04;

    /// <summary>Register offsets of OP1–OP4 (Yamaha order) within a channel's operator registers: OP1, OP3, OP2, OP4 in address order.</summary>
    private static readonly int[] SlotOffsets = [0, 8, 4, 12];

    private readonly GenesisPlan _plan;
    private readonly Song _song;
    private readonly IReadOnlyList<PlaybackTick> _ticks;
    private readonly IReadOnlyList<double> _durations;
    private readonly GenesisChips _chips;
    private readonly ResamplingMode _resampler;
    private readonly ConversionNotes _notes;
    private readonly PlanVoicing _voicing;
    private readonly Track[] _tracks;
    private readonly Dictionary<(int Dac, int Offset, double Volume), Pcm?> _pcm = [];
    private readonly Dictionary<(int Sample, int Frames), double> _noisePeaks = [];
    private readonly Dictionary<string, int> _tallies = [];

    /// <summary>Prepares the chips for a render: LFO and channel 3's special mode off, every channel on both sides.</summary>
    /// <param name="plan">Which channel plays each note, with which voice.</param>
    /// <param name="song">The plan's song: its samples.</param>
    /// <param name="playback">The song played tick by tick.</param>
    /// <param name="durations">Each tick's length in seconds.</param>
    /// <param name="chips">The hardware to write to.</param>
    /// <param name="resampler">How DAC samples are brought to the driver's rate.</param>
    /// <param name="notes">Collects what the render can't play as the plan says.</param>
    public GenesisPlanPlayer(GenesisPlan plan, Song song, Playback playback, IReadOnlyList<double> durations, GenesisChips chips, ResamplingMode resampler, ConversionNotes notes)
    {
        _plan = plan;
        _song = song;
        _ticks = playback.Ticks;
        _durations = durations;
        _chips = chips;
        _resampler = resampler;
        _notes = notes;
        _voicing = new PlanVoicing(plan, song);
        _tracks = [.. Enumerable.Range(0, GenesisPlan.ChannelCount).Select(_ => new Track())];
        if (_ticks.Count > 0 && _ticks[0].Channels.Count != song.Channels.Count)
        {
            throw new ArgumentException($"The playback has {_ticks[0].Channels.Count} channels; the song has {song.Channels.Count}.", nameof(playback));
        }

        chips.Fm.WriteRegister(0, RegLfo, 0);
        chips.Fm.WriteRegister(0, RegCh3Mode, 0);
        for (int c = 0; c <= (int)GenesisChannel.Fm6; c++)
        {
            WriteFm(c, RegPanLfo, PanLeft | PanRight);
            _tracks[c].Pan = PanLeft | PanRight;
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

        return released;
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
        }
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
            if (cells?[c] is not { } cell)
            {
                continue;
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
                track.Gain = _voicing.FmLevel(sample, voice.Index).Gain;
                track.Sounding = true;
                Update(c, t);
                KeyOn(c);
                break;
            case VoiceKind.Dac:
                track.Sounding = PlayDac(_plan.DacSamples[voice.Index], voice.Index, ch);
                break;
            default:
                int rate = 2 - _voicing.NoiseNote(sample);
                _chips.Psg.Write((byte)(PsgNoiseControl | PsgWhiteNoise | rate));
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
                KeyOn(c);
                break;
            case VoiceKind.Dac:
                track.Sounding = PlayDac(_plan.DacSamples[track.Voice.Index], track.Voice.Index, ch);
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

            track.Gain = _voicing.FmLevel(sample, voice.Index).Gain;
        }

        track.Voice = voice;
    }

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
                StopDac();
                break;
            default:
                WriteNoiseVolume(track, PsgOff);
                break;
        }

        track.Sounding = false;
    }

    /// <summary>Sets a sounding channel's pitch, volume and pan from tick <paramref name="t"/> of the song channel it follows.</summary>
    private void Update(int c, int t)
    {
        Track track = _tracks[c];
        ChannelTick ch = _ticks[t].Channels[track.Source];
        if (c <= (int)GenesisChannel.Fm6)
        {
            int side = PlanVoicing.Side(ch.Pan);
            byte pan = side < 0 ? PanLeft : side > 0 ? PanRight : (byte)(PanLeft | PanRight);
            if (pan != track.Pan)
            {
                WriteFm(c, RegPanLfo, pan);
                track.Pan = pan;
            }
        }

        // The DAC has no pitch or volume of its own: both were set when the note started.
        switch (track.Kind)
        {
            case VoiceKind.Fm:
                UpdateFm(c, track, ch);
                break;
            case VoiceKind.PsgNoise:
                UpdateNoise(track, ch, _durations[t]);
                break;
        }
    }

    private void UpdateFm(int c, Track track, ChannelTick ch)
    {
        if (ch.Pitch.PlaybackRateHz > 0)
        {
            double hz = PlanVoicing.Hz(_voicing.HeardSemitones(ch.Pitch, track.Sample, out _));
            int blockFnum = BlockFnum(hz);
            if (blockFnum != track.BlockFnum)
            {
                WriteFm(c, RegBlockFnumHigh, (byte)(blockFnum >> 8));
                WriteFm(c, RegFnumLow, (byte)blockFnum);
                track.BlockFnum = blockFnum;
            }
        }

        double level = ch.Volume * track.Gain;
        int steps = level <= 0 ? MaxTl : (int)Math.Round(-20 * Math.Log10(level) / TlStepDb, MidpointRounding.AwayFromZero);
        FmPatch patch = _plan.FmPatches[track.Voice.Index];
        foreach (int op in VoiceLevel.Carriers(patch.Algorithm))
        {
            int tl = Math.Clamp(patch.Operators[op].Tl + steps, 0, MaxTl);
            if (tl != track.CarrierTl[op])
            {
                WriteFm(c, (byte)(RegTl + SlotOffsets[op]), (byte)tl);
                track.CarrierTl[op] = tl;
            }
        }
    }

    /// <summary>The block (bits 13–11) and F-number (bits 10–0) closest to <paramref name="hz"/>: the lowest block that holds it, for the finest steps.</summary>
    private int BlockFnum(double hz)
    {
        // f = fnum × 2^(block − 1) × rate / 2^20 (docs/genesis-audio.md).
        double fnumAtBlock0 = hz * (1 << 21) / _chips.SampleRateHz;
        for (int block = 0; block <= MaxBlock; block++)
        {
            int fnum = (int)Math.Round(fnumAtBlock0 / (1 << block));
            if (fnum <= MaxFnum)
            {
                return (block << 11) | fnum;
            }
        }

        return (MaxBlock << 11) | MaxFnum;
    }

    private void LoadPatch(int c, int index)
    {
        Track track = _tracks[c];
        FmPatch patch = _plan.FmPatches[index];
        for (int op = 0; op < 4; op++)
        {
            FmOperator o = patch.Operators[op];
            int slot = SlotOffsets[op];
            int detune = o.Detune >= 0 ? o.Detune : 4 - o.Detune;
            WriteFm(c, (byte)(RegDetuneMult + slot), (byte)((detune << 4) | o.Mult));
            WriteFm(c, (byte)(RegTl + slot), (byte)o.Tl);
            WriteFm(c, (byte)(RegRsAr + slot), (byte)((o.Rs << 6) | o.Ar));
            WriteFm(c, (byte)(RegAmDr + slot), (byte)((o.Am ? 0x80 : 0) | o.Dr));
            WriteFm(c, (byte)(RegD2R + slot), (byte)o.D2R);
            WriteFm(c, (byte)(RegSlRr + slot), (byte)((o.Sl << 4) | o.Rr));
            WriteFm(c, (byte)(RegSsgEg + slot), 0);
            track.CarrierTl[op] = -1;
        }

        WriteFm(c, RegFeedbackAlgorithm, (byte)((patch.Feedback << 3) | patch.Algorithm));
    }

    private void KeyOn(int c) => _chips.Fm.WriteRegister(0, RegKeyOn, (byte)(0xf0 | KeyOnChannel(c)));

    private void KeyOff(int c) => _chips.Fm.WriteRegister(0, RegKeyOn, (byte)KeyOnChannel(c));

    /// <summary>The channel bits of register <c>$28</c>: 0–2 for FM1–3, 4–6 for FM4–6.</summary>
    private static int KeyOnChannel(int c) => c < 3 ? c : c + 1;

    /// <summary>Writes an FM channel's register: <paramref name="register"/> is the channel 1 address, offset by the channel within its part.</summary>
    private void WriteFm(int c, byte register, byte value) => _chips.Fm.WriteRegister(c / 3, (byte)(register + (c % 3)), value);

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
    /// sample against its loudest stretch, in 2 dB steps on top of the MOD volume. Past a one-shot sample's end it is silent.
    /// </summary>
    private void UpdateNoise(Track track, ChannelTick ch, double seconds)
    {
        Sample sample = _song.Samples[track.Sample];
        double rms = WindowRms(sample, (int)track.NoiseFrame, track.NoiseWindow);
        double level = ch.Volume * track.Gain * (track.NoisePeak > 0 ? rms / track.NoisePeak : 0);
        int volume = level <= 0 ? PsgOff : Math.Clamp((int)Math.Round(-20 * Math.Log10(level) / PsgStepDb, MidpointRounding.AwayFromZero), 0, PsgOff);
        WriteNoiseVolume(track, volume);
        track.NoiseFrame += ch.Pitch.PlaybackRateHz * seconds;
    }

    private void WriteNoiseVolume(Track track, int volume)
    {
        if (volume != track.Volume)
        {
            _chips.Psg.Write((byte)(PsgNoiseVolume | volume));
            track.Volume = volume;
        }
    }

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

    /// <summary>Plays a DAC note: its sample from the frame the tick player starts at, at the volume it starts with. False when it stays silent.</summary>
    private bool PlayDac(DacSample dac, int index, ChannelTick ch)
    {
        int offset = ch.Started?.StartFrame ?? 0;
        if (Prepare(dac, index, offset, ch.Volume) is not { } pcm)
        {
            StopDac();
            return false;
        }

        switch (_chips.Dac)
        {
            case DmfDac dmf:
                dmf.SetDacMode(true);
                dmf.Play(pcm.Data16!, pcm.RateHz);
                break;
            case XgmPcmDriver xgm:
                xgm.Play(PcmChannel, pcm.Data8!, PcmPriority);
                break;
            case Xgm2PcmDriver xgm2:
                xgm2.Play(PcmChannel, pcm.Data8!, PcmPriority, halfSpeed: false);
                break;
        }

        return true;
    }

    private void StopDac()
    {
        switch (_chips.Dac)
        {
            case DmfDac dmf:
                dmf.Stop();
                break;
            case XgmPcmDriver xgm:
                xgm.Stop(PcmChannel, PcmPriority);
                break;
            case Xgm2PcmDriver xgm2:
                xgm2.Stop(PcmChannel);
                break;
        }
    }

    /// <summary>
    /// The PCM a DAC note plays, made on first use: the sample played out from <paramref name="offset"/>, at the driver's
    /// rate, at <paramref name="volume"/>; <c>null</c> when it is silent. The DMF keeps DefleMask's rate codes and makes
    /// its copies 3 dB apart, as the DMF writer does; XGM and XGM2 play exactly.
    /// </summary>
    private Pcm? Prepare(DacSample dac, int index, int offset, double volume)
    {
        bool dmf = _chips.Driver == PcmDriver.Dmf;
        int steps = volume <= 0 ? int.MaxValue : Math.Max(0, (int)Math.Round(-20 * Math.Log10(volume) / DmfPatternTranslator.DacVolumeStepDb, MidpointRounding.AwayFromZero));
        double key = dmf ? steps : volume;
        if (_pcm.TryGetValue((index, offset, key), out Pcm? pcm))
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
            pcm = pcm16.Length == 0 ? null : new Pcm(pcm16, null, rate);
        }
        else
        {
            double rate = _chips.Driver == PcmDriver.Xgm ? XgmPcmDriver.BeatRateHz(_chips.YmClockHz) : Xgm2PcmDriver.BeatRateHz(_chips.YmClockHz);
            sbyte[] pcm8 = SampleProcessor.ToSigned8(SampleProcessor.ApplyGain(SampleProcessor.Resample(data, dac.PlaybackRateHz, rate, _resampler), volume), name, _notes);
            pcm = pcm8.Length == 0 ? null : new Pcm(null, pcm8, (int)Math.Round(rate));
        }

        _pcm[(index, offset, key)] = pcm;
        return pcm;
    }

    private void Tally(string template) => _tallies[template] = _tallies.GetValueOrDefault(template) + 1;

    /// <summary>A DAC note's data for the driver: 16-bit for the DMF, 8-bit for XGM / XGM2.</summary>
    private sealed record Pcm(short[]? Data16, sbyte[]? Data8, int RateHz);

    /// <summary>One Genesis channel: the note it plays and what was last written for it.</summary>
    private sealed class Track
    {
        /// <summary>The song channel the note came from, or −1.</summary>
        public int Source { get; set; } = -1;

        /// <summary>The song sample the note plays.</summary>
        public int Sample { get; set; } = -1;

        /// <summary>The voice the note plays with.</summary>
        public PlanVoice Voice { get; set; }

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

        /// <summary>True on a row whose cell restarts the note (E9x) from the same song channel.</summary>
        public bool RestartRow { get; set; }

        /// <summary>The block and F-number last written, or −1.</summary>
        public int BlockFnum { get; set; } = -1;

        /// <summary>The TL last written to each operator as a carrier, or −1.</summary>
        public int[] CarrierTl { get; } = [-1, -1, -1, -1];

        /// <summary>The pan bits last written.</summary>
        public byte Pan { get; set; }

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
