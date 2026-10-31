namespace Mod2Xgm.Render.Pcm;

/// <summary>
/// What SGDK's XGM and XGM2 Z80 drivers share (docs/adr/0058-pcm-driver-models.md): a few channels of 8-bit signed PCM
/// mixed in software, one byte per channel per beat, added in channel order with each sum stuck at +127 / −128 instead
/// of wrapping, and sent to the DAC. Every sample plays to the end of its 256-byte padding. A play replaces the one on
/// its channel when its priority is the same or higher; the channel's priority goes back to 0 when its sound ends.
/// <para>The beats are even and a sound starts on the first beat after its play, which the real drivers' buffering
/// delays a little; the DAC enable follows each driver's own rule, checked once per mixing block.</para>
/// </summary>
internal abstract class SoftwarePcmDriver : IDacSource
{
    /// <summary>The highest priority a play can have (both drivers: 4 bits).</summary>
    protected const int MaxPriority = 15;

    /// <summary>Sample addresses and sizes are multiples of 256 bytes (docs/formats/xgm.md), so a sample plays to its padding's end.</summary>
    private const int SampleAlignment = 256;

    /// <summary><c>$2B</c> with bit 7 set: the DAC replaces FM channel 6.</summary>
    protected const byte DacOn = 0x80;

    /// <summary><c>$2B</c> clear: the music never turns the DAC on itself (docs/formats/xgm.md#writer-rules-xgm).</summary>
    protected const byte DacOff = 0x00;

    /// <summary>The DAC byte for 0: the drivers add <c>$80</c> to the signed mix.</summary>
    private const byte DacZero = 0x80;

    private readonly Voice[] _voices;
    private readonly int _blockBeats;
    private int _beatInBlock;
    private bool _soundInBlock;
    private byte _data = DacZero;
    private byte _enable = DacOff;

    /// <summary>Creates the driver with <paramref name="channels"/> silent channels and the DAC off.</summary>
    /// <param name="channels">PCM channels: 4 (XGM) or 3 (XGM2).</param>
    /// <param name="blockBeats">Beats per mixing block, after each of which the driver sets <c>$2B</c>.</param>
    protected SoftwarePcmDriver(int channels, int blockBeats)
    {
        _voices = new Voice[channels];
        for (int i = 0; i < channels; i++)
        {
            _voices[i] = new Voice();
        }

        _blockBeats = blockBeats;
    }

    /// <summary>The number of PCM channels.</summary>
    public int Channels => _voices.Length;

    /// <summary>True while channel <paramref name="channel"/> is playing a sound (its padding included).</summary>
    public bool IsPlaying(int channel) => VoiceAt(channel).Data is not null;

    /// <summary>The priority of the sound on channel <paramref name="channel"/>: 0 when it is silent.</summary>
    public int PriorityOf(int channel) => VoiceAt(channel).Priority;

    /// <inheritdoc/>
    public void Generate(Span<byte> data, Span<byte> enable)
    {
        if (data.Length != enable.Length)
        {
            throw new ArgumentException("Both registers need the same length.", nameof(enable));
        }

        for (int i = 0; i < data.Length; i++)
        {
            if (NextSampleIsBeat())
            {
                Beat();
            }

            data[i] = _data;
            enable[i] = _enable;
        }
    }

    /// <summary>Advances one YM3438 sample; true when the driver sends a byte to the DAC on it.</summary>
    protected abstract bool NextSampleIsBeat();

    /// <summary>
    /// The driver's <c>$2B</c> rule at the end of a mixing block, given whether a channel is playing now and whether one
    /// played during the block; the driver keeps its own counter.
    /// </summary>
    protected abstract byte DacEnableAfterBlock(bool playingNow, bool soundInBlock);

    /// <summary>
    /// What a play does to the DAC enable before its first byte, standing in for the driver turning the DAC on while it
    /// mixes ahead (the drivers' buffering is not modeled).
    /// </summary>
    protected virtual void OnPlay()
    {
    }

    /// <summary>Starts <paramref name="sample"/> on <paramref name="channel"/> if <paramref name="priority"/> is at least the playing one's; true if it did.</summary>
    /// <exception cref="ArgumentException"><paramref name="sample"/> is empty.</exception>
    protected bool Start(int channel, sbyte[] sample, int priority, bool halfSpeed)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.Length == 0)
        {
            throw new ArgumentException("A sample needs at least one byte.", nameof(sample));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(priority);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(priority, MaxPriority);
        Voice voice = VoiceAt(channel);
        if (priority < voice.Priority)
        {
            return false;
        }

        voice.Data = sample;
        voice.Length = (sample.Length + SampleAlignment - 1) / SampleAlignment * SampleAlignment;
        voice.Position = 0;
        voice.HalfSpeed = halfSpeed;
        voice.SecondHalf = false;
        voice.Priority = priority;
        _soundInBlock = true;
        _enable = DacOn;
        OnPlay();
        return true;
    }

    /// <summary>Silences <paramref name="channel"/> and sets its priority back to 0.</summary>
    protected void Silence(int channel) => VoiceAt(channel).Clear();

    private Voice VoiceAt(int channel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(channel, _voices.Length);
        return _voices[channel];
    }

    /// <summary>Mixes one byte of every channel, in channel order, and moves to the next block when this one is full.</summary>
    private void Beat()
    {
        int mix = 0;
        foreach (Voice voice in _voices)
        {
            if (voice.Data is not null)
            {
                _soundInBlock = true;
                mix = Math.Clamp(mix + voice.Next(), sbyte.MinValue, sbyte.MaxValue);
            }
        }

        _data = (byte)(mix + DacZero);
        if (++_beatInBlock < _blockBeats)
        {
            return;
        }

        _beatInBlock = 0;
        bool playingNow = Array.Exists(_voices, v => v.Data is not null);
        _enable = DacEnableAfterBlock(playingNow, _soundInBlock);
        _soundInBlock = false;
    }

    /// <summary>One PCM channel: the sound it plays, where it is, and its priority.</summary>
    private sealed class Voice
    {
        /// <summary>The sample's bytes, or null when the channel is silent.</summary>
        public sbyte[]? Data { get; set; }

        /// <summary>The sample's length rounded up to <see cref="SampleAlignment"/>: the padding plays as silence.</summary>
        public int Length { get; set; }

        /// <summary>The next byte to play.</summary>
        public int Position { get; set; }

        /// <summary>XGM2's half speed: every byte plays on two beats.</summary>
        public bool HalfSpeed { get; set; }

        /// <summary>At half speed, true when the current byte has played once already.</summary>
        public bool SecondHalf { get; set; }

        /// <summary>The sound's priority, 0 when silent.</summary>
        public int Priority { get; set; }

        /// <summary>This beat's byte; moves on and clears the channel after the last one.</summary>
        public int Next()
        {
            if (Data is not { } data)
            {
                return 0;
            }

            int value = Position < data.Length ? data[Position] : 0;
            if (HalfSpeed && !SecondHalf)
            {
                SecondHalf = true;
                return value;
            }

            SecondHalf = false;
            if (++Position == Length)
            {
                Clear();
            }

            return value;
        }

        /// <summary>Silences the channel: no sound, priority 0.</summary>
        public void Clear()
        {
            Data = null;
            Priority = 0;
        }
    }
}
