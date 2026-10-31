namespace Mod2Xgm.Render.Pcm;

/// <summary>
/// What SGDK's XGM and XGM2 Z80 drivers share (docs/adr/0058-pcm-driver-models.md): a few channels of 8-bit signed PCM
/// mixed in software, one byte per channel per beat, added in channel order with each sum stuck at +127 / −128 instead
/// of wrapping, and sent to the DAC. Every sample plays to the end of its 256-byte padding. A play replaces the one on
/// its channel when its priority is the same or higher; the channel's priority goes back to 0 when its sound ends.
/// <para>The beats are even. The real drivers mix ahead into a buffer, so a play or a stop is heard a while after its
/// frame; buffered (the default), the model holds each one until the beat its driver's buffer puts it on (each driver's
/// <see cref="StartBeat"/>, measured on SGDK's drivers: docs/adr/0066-pcm-drivers-checked-against-sgdk.md), and does it
/// there, priority check included. Unbuffered, a play starts on the next beat (the mixer alone, for its tests). The DAC
/// enable follows each driver's own rule, checked once per mixing block.</para>
/// </summary>
internal abstract class SoftwarePcmDriver : IDacSource
{
    /// <summary>The highest priority a play can have (both drivers: 4 bits).</summary>
    private const int MaxPriority = 15;

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
    private readonly List<(long Beat, Action Apply)> _pending = [];
    private long _beat;
    private int _beatInBlock;
    private byte _data = DacZero;
    private byte _enable = DacOff;

    /// <summary>Creates the driver with <paramref name="channels"/> silent channels and the DAC off.</summary>
    /// <param name="channels">PCM channels: 4 (XGM) or 3 (XGM2).</param>
    /// <param name="blockBeats">Beats per mixing block, after each of which the driver sets <c>$2B</c>.</param>
    /// <param name="buffered">True to delay plays and stops as the driver's buffer does; false to start them on the next beat.</param>
    protected SoftwarePcmDriver(int channels, int blockBeats, bool buffered)
    {
        Buffered = buffered;
        _voices = new Voice[channels];
        for (int i = 0; i < channels; i++)
        {
            _voices[i] = new Voice();
        }

        _blockBeats = blockBeats;
    }

    /// <summary>True when plays and stops wait for the beat the driver's buffer puts them on (<see cref="StartBeat"/>).</summary>
    public bool Buffered { get; }

    /// <summary>The beats sent so far (one byte to the DAC each).</summary>
    public long BeatsSent => _beat;

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
    /// Buffered: the beat a play or a stop sent on beat <paramref name="now"/> (the next beat to be sent) is heard from,
    /// as the driver's buffer delays it (docs/adr/0066-pcm-drivers-checked-against-sgdk.md).
    /// </summary>
    /// <param name="now">The next beat's index, counted from 0 at the driver's start.</param>
    /// <param name="blockBeats">Beats per mixing block; block <c>n</c> starts on beat <c>n × blockBeats</c>.</param>
    protected abstract long StartBeat(long now, int blockBeats);

    /// <summary>
    /// Buffered: the beat the driver turns the DAC on for a play sent on beat <paramref name="now"/> that is heard from
    /// <paramref name="start"/>; at or before <paramref name="now"/> means at once.
    /// </summary>
    protected virtual long DacOnBeat(long now, long start) => now;

    /// <summary>
    /// Sends a play or a stop: does <paramref name="command"/> at once when unbuffered, or holds it until its
    /// <see cref="StartBeat"/>, turning the DAC on at its <see cref="DacOnBeat"/> if it is a play.
    /// </summary>
    /// <returns>Unbuffered, what <paramref name="command"/> returns; buffered, true (the driver decides when it gets there).</returns>
    protected bool Send(Func<bool> command, bool isPlay)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Buffered)
        {
            return command();
        }

        long start = StartBeat(_beat, _blockBeats);
        if (isPlay)
        {
            long dacOn = DacOnBeat(_beat, start);
            if (dacOn <= _beat)
            {
                TurnDacOn();
            }
            else
            {
                _pending.Add((dacOn, TurnDacOn));
            }
        }

        _pending.Add((start, () => command()));
        return true;
    }

    /// <summary>Turns the DAC on for a play, as the driver does when it mixes the sound.</summary>
    private void TurnDacOn()
    {
        _enable = DacOn;
        OnPlay();
    }

    /// <summary>
    /// The driver's <c>$2B</c> rule at the end of a mixing block, given whether a channel is playing now; the driver keeps
    /// its own counter.
    /// </summary>
    protected abstract byte DacEnableAfterBlock(bool playingNow);

    /// <summary>
    /// What a play does to the DAC enable before its first byte, standing in for the driver turning the DAC on while it
    /// mixes ahead (the drivers' buffering is not modeled).
    /// </summary>
    protected virtual void OnPlay()
    {
    }

    /// <summary>Called after a beat whose mix stuck at +127 / −128 <paramref name="clips"/> times (once per channel add that overflowed).</summary>
    protected virtual void OnClip(int clips)
    {
    }

    /// <summary>Starts <paramref name="sample"/> on <paramref name="channel"/> if <paramref name="priority"/> is at least the playing one's; true if it did.</summary>
    /// <param name="channel">The PCM channel.</param>
    /// <param name="sample">The sound: 8-bit signed, at least one byte.</param>
    /// <param name="priority">0–15.</param>
    /// <param name="halfSpeed">True to play every byte on two beats (XGM2).</param>
    /// <exception cref="ArgumentException"><paramref name="sample"/> is empty.</exception>
    /// <param name="repeatHead">Source bytes the sound plays from its start a second time once it has played them (XGM2's driver: <see cref="Xgm2PcmDriver"/>); 0 for none.</param>
    protected bool Start(int channel, sbyte[] sample, int priority, bool halfSpeed, int repeatHead = 0)
    {
        sample = CheckPlay(channel, sample, priority);
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
        voice.RepeatHead = Math.Min(repeatHead, voice.Length);
        voice.Priority = priority;
        TurnDacOn();
        return true;
    }

    /// <summary>Throws if a play's channel, sample or priority is out of range, so a buffered play fails when it is sent.</summary>
    /// <exception cref="ArgumentException"><paramref name="sample"/> is empty.</exception>
    /// <returns><paramref name="sample"/>, checked.</returns>
    protected sbyte[] CheckPlay(int channel, sbyte[] sample, int priority)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.Length == 0)
        {
            throw new ArgumentException("A sample needs at least one byte.", nameof(sample));
        }

        CheckPriority(priority);
        _ = VoiceAt(channel);
        return sample;
    }

    /// <summary>Throws if <paramref name="priority"/> is not 0–15.</summary>
    protected static void CheckPriority(int priority)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(priority);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(priority, MaxPriority);
    }

    /// <summary>Throws if <paramref name="channel"/> is not one of the driver's.</summary>
    protected void CheckChannel(int channel) => _ = VoiceAt(channel);

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
        if (_pending.Count > 0)
        {
            // In the order sent: a play and a stop on the same beat keep their order (a stable sort).
            List<(long Beat, Action Apply)> due = [.. _pending.Where(p => p.Beat <= _beat).OrderBy(p => p.Beat)];
            _pending.RemoveAll(p => p.Beat <= _beat);
            foreach ((_, Action apply) in due)
            {
                apply();
            }
        }

        _beat++;
        int mix = 0;
        int clips = 0;
        foreach (Voice voice in _voices)
        {
            if (voice.Data is not null)
            {
                        int sum = mix + voice.Next();
                if (sum is > sbyte.MaxValue or < sbyte.MinValue)
                {
                    clips++;
                }

                mix = Math.Clamp(sum, sbyte.MinValue, sbyte.MaxValue);
            }
        }

        _data = (byte)(mix + DacZero);
        if (clips > 0)
        {
            OnClip(clips);
        }
        if (++_beatInBlock < _blockBeats)
        {
            return;
        }

        _beatInBlock = 0;
        bool playingNow = Array.Exists(_voices, v => v.Data is not null);
        _enable = DacEnableAfterBlock(playingNow);
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

        /// <summary>Source bytes played again from the start once reached, then 0: XGM2's first fill played twice.</summary>
        public int RepeatHead { get; set; }

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
            if (++Position == RepeatHead)
            {
                Position = 0;
                RepeatHead = 0;
            }
            else if (Position == Length)
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
