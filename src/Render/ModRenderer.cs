using Mod2Xgm.Model;

namespace Mod2Xgm.Render;

/// <summary>How <see cref="ModRenderer"/> renders.</summary>
/// <param name="SampleRate">Output samples per second.</param>
/// <param name="StereoSeparation">
/// How far apart the channels' pans are: 0 = mono, 1 = as the playback says (hard left/right on an Amiga). The default,
/// <see cref="ModRenderer.OpenMptSeparation"/>, is what OpenMPT gives a MOD.
/// </param>
internal sealed record ModRenderOptions(int SampleRate = 44_100, double StereoSeparation = ModRenderer.OpenMptSeparation)
{
    /// <summary>The defaults: 44.1 kHz, OpenMPT's stereo, the playback's own tick lengths.</summary>
    public static ModRenderOptions Default { get; } = new();

    /// <summary>
    /// The length of each tick in seconds, in place of the playback's own (one per tick), or <c>null</c> to keep them.
    /// The accuracy harness passes OpenMPT's tick lengths so the render keeps time with the reference.
    /// </summary>
    public IReadOnlyList<double>? TickDurations { get; init; }
}

/// <summary>
/// Plays a song from its <see cref="Playback"/> (the tick player's output) the way libopenmpt plays it with its default
/// settings (plan T7.1, docs/adr/0053-mod-renderer.md): the in-app reference the optimizer listens to. Each channel
/// plays its sample parts at the playback rate through an 8-tap windowed sinc (<see cref="SincTable"/>), with OpenMPT's
/// level, MOD panning and volume ramps, a short fade-out of the old note when a new one starts, and OpenMPT's click
/// removal where a sample ends. The Amiga's low-pass ("LED") filter is not modelled, as libopenmpt doesn't by default.
/// </summary>
internal static class ModRenderer
{
    /// <summary>Output level of one channel at full volume, before panning: libopenmpt's, measured on a full-scale sample.</summary>
    public const double ChannelGain = 0.5;

    /// <summary>OpenMPT pans MOD channels to 25 % / 75 % (pan ±0.5), not hard left/right.</summary>
    public const double OpenMptSeparation = 0.5;

    /// <summary>A rising volume reaches its new value over this long (OpenMPT's default ramp up, 16 samples at 44.1 kHz).</summary>
    public const double RampUpSeconds = 363e-6;

    /// <summary>A falling volume, or a note cut by a new one, takes this long (OpenMPT's default ramp down, 42 samples at 44.1 kHz).</summary>
    public const double RampDownSeconds = 952e-6;

    /// <summary>
    /// Click removal: where a one-shot sample ends, OpenMPT keeps its last value as an offset that shrinks by this share
    /// every output sample, instead of jumping to 0 (its mixer's offset decay, a shift by 8 bits).
    /// </summary>
    public const double OffsetDecay = 1.0 / 256;

    /// <summary>Renders <paramref name="playback"/> once through, from its first tick to the end of its last.</summary>
    /// <param name="song">The song the playback's sample parts point into.</param>
    /// <param name="playback">The song played tick by tick.</param>
    /// <param name="options">How to render; <c>null</c> for <see cref="ModRenderOptions.Default"/>.</param>
    /// <exception cref="ArgumentException">The options don't fit the playback, or a tick plays a sample the song doesn't have.</exception>
    public static StereoAudio Render(Song song, Playback playback, ModRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(playback);
        ModRenderOptions settings = options ?? ModRenderOptions.Default;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.SampleRate, nameof(options));
        if (settings.StereoSeparation is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), settings.StereoSeparation, "Stereo separation must be from 0 to 1.");
        }

        IReadOnlyList<PlaybackTick> ticks = playback.Ticks;
        IReadOnlyList<double> durations = settings.TickDurations ?? [.. ticks.Select(t => t.DurationSeconds)];
        if (durations.Count != ticks.Count)
        {
            throw new ArgumentException($"{durations.Count} tick lengths for {ticks.Count} ticks.", nameof(options));
        }

        int[] starts = TickStarts(durations, settings.SampleRate);
        float[] left = new float[starts[^1]];
        float[] right = new float[starts[^1]];
        Mixer mixer = new(song, settings);
        ChannelVoices[] channels = [.. Enumerable.Range(0, ticks.Count == 0 ? 0 : ticks[0].Channels.Count).Select(_ => new ChannelVoices())];
        for (int t = 0; t < ticks.Count; t++)
        {
            for (int c = 0; c < channels.Length; c++)
            {
                channels[c].Update(ticks[t].Channels[c], mixer);
                channels[c].Mix(left, right, starts[t], starts[t + 1]);
            }
        }

        mixer.AddOffsets(left, right);
        return new StereoAudio(left, right, settings.SampleRate);
    }

    /// <summary>The output sample each tick starts on, and the length of the render last.</summary>
    private static int[] TickStarts(IReadOnlyList<double> durations, int sampleRate)
    {
        int[] starts = new int[durations.Count + 1];
        double seconds = 0;
        for (int i = 0; i < durations.Count; i++)
        {
            if (durations[i] < 0 || !double.IsFinite(durations[i]))
            {
                throw new ArgumentException($"Tick {i} lasts {durations[i]} s.", nameof(durations));
            }

            seconds += durations[i];
            starts[i + 1] = checked((int)Math.Round(seconds * sampleRate));
        }

        return starts;
    }

    /// <summary>What every voice of one render shares: the song's samples, the rate, the stereo width and the ramp lengths.</summary>
    private sealed class Mixer(Song song, ModRenderOptions options)
    {
        /// <summary>The song whose samples are played.</summary>
        public Song Song { get; } = song;

        /// <summary>Output samples per second.</summary>
        public int SampleRate { get; } = options.SampleRate;

        /// <summary>0 = mono … 1 = the playback's pans.</summary>
        public double Separation { get; } = options.StereoSeparation;

        /// <summary>Output samples a rising volume ramps over.</summary>
        public int RampUp { get; } = Math.Max(1, (int)Math.Round(RampUpSeconds * options.SampleRate));

        /// <summary>Output samples a falling volume ramps over.</summary>
        public int RampDown { get; } = Math.Max(1, (int)Math.Round(RampDownSeconds * options.SampleRate));

        private readonly List<(int At, double Left, double Right)> _offsets = [];

        /// <summary>Keeps a sample's last value from output sample <paramref name="at"/> on, fading (<see cref="OffsetDecay"/>).</summary>
        public void HoldOffset(int at, double left, double right) => _offsets.Add((at, left, right));

        /// <summary>Adds the held offsets to the render, each fading from where it was held.</summary>
        public void AddOffsets(float[] left, float[] right)
        {
            _offsets.Sort((a, b) => a.At.CompareTo(b.At));
            double offsetLeft = 0;
            double offsetRight = 0;
            int next = 0;
            for (int n = 0; n < left.Length; n++)
            {
                for (; next < _offsets.Count && _offsets[next].At <= n; next++)
                {
                    offsetLeft += _offsets[next].Left;
                    offsetRight += _offsets[next].Right;
                }

                offsetLeft -= offsetLeft * OffsetDecay;
                offsetRight -= offsetRight * OffsetDecay;
                left[n] += (float)offsetLeft;
                right[n] += (float)offsetRight;
            }
        }
    }

    /// <summary>One song channel: the note playing, and old notes still fading out after a new one started.</summary>
    private sealed class ChannelVoices
    {
        private readonly List<Voice> _fading = [];
        private Voice? _playing;

        /// <summary>Takes the channel's state for the coming tick: a new note, pitch, volume, pan and the part to play next.</summary>
        public void Update(ChannelTick tick, Mixer mixer)
        {
            if (tick.Started is { } started)
            {
                if (_playing is not null)
                {
                    _playing.FadeOut(mixer.RampDown);
                    _fading.Add(_playing);
                }

                _playing = new Voice(mixer, started);
            }

            if (_playing is null)
            {
                return;
            }

            _playing.Next = tick.Next;
            _playing.Increment = tick.Pitch.PlaybackRateHz / mixer.SampleRate;
            double volume = tick.Playing ? tick.Volume * ChannelGain : 0;
            double pan = Math.Clamp(tick.Pan, -1, 1) * mixer.Separation;
            _playing.SetVolume(volume, volume * (1 - pan) / 2, volume * (1 + pan) / 2, mixer);
        }

        /// <summary>Adds the channel's sound for output samples <paramref name="start"/> up to <paramref name="end"/>.</summary>
        public void Mix(float[] left, float[] right, int start, int end)
        {
            _playing?.Mix(left, right, start, end);
            foreach (Voice voice in _fading)
            {
                voice.Mix(left, right, start, end);
            }

            _fading.RemoveAll(v => v.Silent);
        }
    }

    /// <summary>
    /// One note playing: it reads its sample parts as Paula does (the part that started, then the next part each time
    /// the playing one ends), interpolates between source samples, and ramps its volume.
    /// </summary>
    private sealed class Voice
    {
        private readonly Mixer _mixer;
        private readonly float[] _window = new float[SincTable.Taps];
        private SampleBlock _block;
        private int _read;
        private bool _ended;
        private int _silentPushes;
        private float _lastSource;
        private bool _offsetHeld;
        private int _head;
        private double _fraction;
        private double _volume;
        private double _left;
        private double _right;
        private double _targetLeft;
        private double _targetRight;
        private double _stepLeft;
        private double _stepRight;
        private int _ramp;
        private bool _fadingOut;

        /// <summary>Starts playing <paramref name="started"/>, silent until its volume is set.</summary>
        public Voice(Mixer mixer, SampleBlock started)
        {
            _mixer = mixer;
            _block = started;
            _ended = IsEnd(started);
            Next = new SampleBlock(null, 0, 0);
            for (int i = 0; i < SincTable.TapsBefore; i++)
            {
                Push(0);
            }

            for (int i = SincTable.TapsBefore; i < SincTable.Taps; i++)
            {
                Push(Pull());
            }
        }

        /// <summary>The part played after the current one ends; it may change while the current one plays.</summary>
        public SampleBlock Next { get; set; }

        /// <summary>Source samples per output sample.</summary>
        public double Increment { get; set; }

        /// <summary>
        /// True while the voice makes no sound: faded out, or past the end of its sample with nothing to play next. A
        /// fade-out is final; a sample that ended starts again if <see cref="Next"/> becomes a real part (a ProTracker sample swap).
        /// </summary>
        public bool Silent => (_fadingOut && _ramp == 0) || (PastEnd && IsEnd(Next));

        /// <summary>True once the current position is past the last source sample (the window looks 4 samples ahead).</summary>
        private bool PastEnd => _ended && _silentPushes > SincTable.Taps - SincTable.TapsBefore - 1;

        /// <summary>Ramps to a new <paramref name="volume"/> with these left and right gains: quickly when it rises, slower when it falls.</summary>
        public void SetVolume(double volume, double left, double right, Mixer mixer)
        {
            if (left == _targetLeft && right == _targetRight)
            {
                return;
            }

            int length = volume > _volume ? mixer.RampUp : mixer.RampDown;
            _volume = volume;
            _targetLeft = left;
            _targetRight = right;
            _stepLeft = (left - _left) / length;
            _stepRight = (right - _right) / length;
            _ramp = length;
        }

        /// <summary>Fades the voice out over <paramref name="length"/> output samples, after which it is <see cref="Silent"/>.</summary>
        public void FadeOut(int length)
        {
            _volume = 0;
            _targetLeft = 0;
            _targetRight = 0;
            _stepLeft = -_left / length;
            _stepRight = -_right / length;
            _ramp = length;
            _fadingOut = true;
        }

        /// <summary>Adds the voice to output samples <paramref name="start"/> up to <paramref name="end"/>.</summary>
        public void Mix(float[] left, float[] right, int start, int end)
        {
            SincTable table = SincTable.For(Increment);
            for (int n = start; n < end; n++)
            {
                if (Silent)
                {
                    // OpenMPT stops the note here and holds its last value (click removal); once, as a fade-out ends at 0 anyway.
                    if (PastEnd && !_offsetHeld)
                    {
                        _mixer.HoldOffset(n, _lastSource * _left, _lastSource * _right);
                        _offsetHeld = true;
                    }

                    return;
                }

                if (_ramp > 0)
                {
                    _ramp--;
                    _left = _ramp == 0 ? _targetLeft : _left + _stepLeft;
                    _right = _ramp == 0 ? _targetRight : _right + _stepRight;
                }

                double sample = Interpolate(table);
                left[n] += (float)(sample * _left);
                right[n] += (float)(sample * _right);
                Advance();
            }
        }

        /// <summary>The source sound at the current position, from the eight source samples around it.</summary>
        private double Interpolate(SincTable table)
        {
            ReadOnlySpan<float> filter = table.Filter(_fraction);
            double sum = 0;
            for (int tap = 0; tap < SincTable.Taps; tap++)
            {
                sum += _window[(_head + tap) % SincTable.Taps] * filter[tap];
            }

            return sum;
        }

        /// <summary>Moves on by <see cref="Increment"/> source samples.</summary>
        private void Advance()
        {
            _fraction += Increment;
            while (_fraction >= 1)
            {
                _fraction -= 1;
                Push(Pull());
            }
        }

        /// <summary>Adds a source sample to the end of the window; the oldest drops out.</summary>
        private void Push(float sample)
        {
            _silentPushes = _ended ? _silentPushes + 1 : 0;
            _window[_head] = sample;
            _head = (_head + 1) % SincTable.Taps;
        }

        /// <summary>The next source sample: from the playing part, then from <see cref="Next"/> each time a part ends.</summary>
        private float Pull()
        {
            if (_ended || _read >= _block.Frames)
            {
                _ended = IsEnd(Next);
                if (_ended)
                {
                    return 0;
                }

                _block = Next;
                _read = 0;
                _offsetHeld = false;
            }

            float[] data = _mixer.Song.Samples[_block.Sample ?? 0].Data;
            int frame = _block.StartFrame + _read++;
            _lastSource = frame >= 0 && frame < data.Length ? data[frame] : 0;
            return _lastSource;
        }

        /// <summary>
        /// True for a part that means silence: no sample, or the 2-frame stretch at a sample's start that ends a one-shot
        /// sample on an Amiga (docs/architecture.md#layers); OpenMPT stops the note there too.
        /// </summary>
        private static bool IsEnd(SampleBlock block) => block.Sample is null || block.Frames <= 0 || (block.StartFrame == 0 && block.Frames <= 2);
    }
}
