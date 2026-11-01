using Mod2Xgm.Render.Fm;

namespace Mod2Xgm.Render.Pcm;

/// <summary>
/// The PCM half of SGDK's XGM2 Z80 driver, <c>src/snd/xgm2/drv_xgm2.s80</c> (docs/adr/0058-pcm-driver-models.md): three
/// channels of 8-bit signed PCM mixed on the DAC in place of FM channel 6. YM Timer A paces it at one byte every 4
/// YM3438 samples (about 13 317 Hz on NTSC, 13 195 Hz on PAL); at half speed a channel plays each byte on two beats
/// (6.65 kHz).
/// <para><c>$2B</c>: on <see cref="DacLeadBeats"/> before a sound is heard, off at the end of the first 64-beat block in
/// which nothing plays (measured; the driver decides it for the block it mixes, ahead of what is heard).</para>
/// <para>Buffered (docs/adr/0066-pcm-drivers-checked-against-sgdk.md): the driver mixes ahead into its buffer, so a play
/// or a stop is heard <see cref="StartDelayBeats"/> beats after its frame (SGDK's xgm2tool does not move PCM commands
/// ahead), and the DAC goes on <see cref="DacLeadBeats"/> beats before the sound.</para>
/// </summary>
internal sealed class Xgm2PcmDriver : SoftwarePcmDriver
{
    /// <summary>XGM2's PCM channels.</summary>
    private const int ChannelCount = 3;

    /// <summary>The priority of a music play with bit 3 of <c>$1x</c> clear (<c>drv_xgm2_ym_fct.i80</c>, <c>FM_playPCM</c>).</summary>
    public const int LowPriority = 7;

    /// <summary>The priority of a music play with bit 3 of <c>$1x</c> set.</summary>
    public const int HighPriority = 15;

    /// <summary>YM3438 samples per beat: Timer A at 1020 overflows every 1024 − 1020 samples (<c>drv_xgm2.s80</c>).</summary>
    private const int SamplesPerBeat = 4;

    /// <summary>Beats per mixing block: the driver fills its ring buffer 64 bytes at a time.</summary>
    private const int BlockBeats = 64;

    /// <summary>The DAC counter's reset value; it counts down once in the same block (<c>DAC_ENABLED_CNT</c>).</summary>
    private const int HoldCount = 4;

    /// <summary>
    /// Beats from a PCM command's frame to its first byte heard: measured on SGDK's driver at 1 682–1 865 YM3438 samples,
    /// as full the buffer happens to be (docs/adr/0066-pcm-drivers-checked-against-sgdk.md); 443 beats is the mean.
    /// </summary>
    public const int StartDelayBeats = 443;

    /// <summary>Beats the driver turns the DAC on before a play's first byte is heard: measured 0.73–0.78 frames, about 173 beats.</summary>
    public const int DacLeadBeats = 173;

    /// <summary>Beats of a sound's start the driver plays twice: one 64-byte buffer fill (64 source bytes, or 32 at half speed).</summary>
    public const int RepeatedBeats = 64;

    private int _sampleInBeat;
    private int _hold;

    /// <summary>Creates the driver with every channel silent and the DAC off.</summary>
    /// <param name="buffered">True (the default) to start plays and stops when SGDK's driver is heard doing them; false to start them on the next beat.</param>
    public Xgm2PcmDriver(bool buffered = true)
        : base(ChannelCount, BlockBeats, buffered)
    {
    }

    /// <summary>Full-speed beats per second for a YM3438 at <paramref name="ymClockHz"/>; half speed is half that.</summary>
    public static double BeatRateHz(double ymClockHz) => Ym3438.SampleRateHz(ymClockHz) / SamplesPerBeat;

    /// <summary>
    /// The music's <c>$1x id</c> command: plays <paramref name="sample"/> (8-bit signed, any length; padded to 256 bytes) on
    /// <paramref name="channel"/> 0–2, at half speed if <paramref name="halfSpeed"/>, if <paramref name="priority"/> 0–15
    /// (music: <see cref="LowPriority"/> or <see cref="HighPriority"/>) is at least the playing sound's. True if it did.
    /// </summary>
    /// <remarks>Buffered, the priority is checked when the play is heard, and the result is always true.</remarks>
    /// <remarks>
    /// Buffered, the first <see cref="RepeatedBeats"/> beats of the sound play twice, as SGDK's driver plays them: its
    /// first fill after a play sees the play's "parameters changed" flag and drops its own advance
    /// (<c>updatePCM</c> in <c>drv_xgm2_pcm_mac.i80</c>; measured on every play, docs/adr/0066-pcm-drivers-checked-against-sgdk.md).
    /// </remarks>
    public bool Play(int channel, sbyte[] sample, int priority, bool halfSpeed)
    {
        sbyte[] data = CheckPlay(channel, sample, priority);
        int repeat = Buffered ? (halfSpeed ? RepeatedBeats / 2 : RepeatedBeats) : 0;
        return Send(() => Start(channel, data, priority, halfSpeed, repeat), isPlay: true);
    }

    /// <summary>The music's <c>$1x 00</c>: stops <paramref name="channel"/>, whatever its priority.</summary>
    public void Stop(int channel)
    {
        CheckChannel(channel);
        Send(
            () =>
            {
                Silence(channel);
                return true;
            },
            isPlay: false);
    }

    /// <inheritdoc/>
    protected override bool NextSampleIsBeat()
    {
        bool beat = _sampleInBeat == 0;
        _sampleInBeat = (_sampleInBeat + 1) % SamplesPerBeat;
        return beat;
    }

    /// <inheritdoc/>
    /// <remarks>The driver resets its DAC counter whenever it mixes a sound, so the DAC stays on until the sound is heard.</remarks>
    protected override void OnPlay() => _hold = HoldCount;

    /// <inheritdoc/>
    protected override long StartBeat(long now, int blockBeats) => now + StartDelayBeats;

    /// <inheritdoc/>
    protected override long DacOnBeat(long now, long start) => start - DacLeadBeats;

    /// <inheritdoc/>
    /// <remarks>
    /// Measured on SGDK's driver (docs/adr/0066-pcm-drivers-checked-against-sgdk.md): the DAC goes off at the end of the
    /// first block in which nothing plays, 29–77 beats after the last sound (its padding included) or a stop; the count
    /// <c>DAC_ENABLED_CNT</c> keeps it on only while a play is still on its way through the buffer.
    /// </remarks>
    protected override byte DacEnableAfterBlock(bool playingNow)
    {
        if (playingNow)
        {
            _hold = 0;
            return DacOn;
        }

        if (_hold > 0)
        {
            _hold--;
            return DacOn;
        }

        return DacOff;
    }
}
