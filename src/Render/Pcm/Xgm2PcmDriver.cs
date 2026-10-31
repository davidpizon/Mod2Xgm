using Mod2Xgm.Render.Fm;

namespace Mod2Xgm.Render.Pcm;

/// <summary>
/// The PCM half of SGDK's XGM2 Z80 driver, <c>src/snd/xgm2/drv_xgm2.s80</c> (docs/adr/0058-pcm-driver-models.md): three
/// channels of 8-bit signed PCM mixed on the DAC in place of FM channel 6. YM Timer A paces it at one byte every 4
/// YM3438 samples (about 13 317 Hz on NTSC, 13 195 Hz on PAL); at half speed a channel plays each byte on two beats
/// (6.65 kHz).
/// <para><c>$2B</c>: after every block of 64 beats the driver turns the DAC on if a channel played during it, keeps it on
/// for three more blocks after that, then turns it off.</para>
/// </summary>
internal sealed class Xgm2PcmDriver : SoftwarePcmDriver
{
    /// <summary>XGM2's PCM channels.</summary>
    public const int ChannelCount = 3;

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

    private int _sampleInBeat;
    private int _hold;

    /// <summary>Creates the driver with every channel silent and the DAC off.</summary>
    public Xgm2PcmDriver()
        : base(ChannelCount, BlockBeats)
    {
    }

    /// <summary>Full-speed beats per second for a YM3438 at <paramref name="ymClockHz"/>; half speed is half that.</summary>
    public static double BeatRateHz(double ymClockHz) => Ym3438.SampleRateHz(ymClockHz) / SamplesPerBeat;

    /// <summary>
    /// The music's <c>$1x id</c> command: plays <paramref name="sample"/> (8-bit signed, any length; padded to 256 bytes) on
    /// <paramref name="channel"/> 0–2, at half speed if <paramref name="halfSpeed"/>, if <paramref name="priority"/> 0–15
    /// (music: <see cref="LowPriority"/> or <see cref="HighPriority"/>) is at least the playing sound's. True if it did.
    /// </summary>
    public bool Play(int channel, sbyte[] sample, int priority, bool halfSpeed) => Start(channel, sample, priority, halfSpeed);

    /// <summary>The music's <c>$1x 00</c>: stops <paramref name="channel"/>, whatever its priority.</summary>
    public void Stop(int channel) => Silence(channel);

    /// <inheritdoc/>
    protected override bool NextSampleIsBeat()
    {
        bool beat = _sampleInBeat == 0;
        _sampleInBeat = (_sampleInBeat + 1) % SamplesPerBeat;
        return beat;
    }

    /// <inheritdoc/>
    protected override byte DacEnableAfterBlock(bool playingNow, bool soundInBlock)
    {
        if (soundInBlock)
        {
            _hold = HoldCount;
        }

        if (_hold > 0)
        {
            _hold--;
            return DacOn;
        }

        return DacOff;
    }
}
