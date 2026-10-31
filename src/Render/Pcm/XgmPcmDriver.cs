using Mod2Xgm.Render.Fm;

namespace Mod2Xgm.Render.Pcm;

/// <summary>
/// The PCM half of SGDK's XGM (v1) Z80 driver, <c>src/snd/drv_xgm.s80</c> (docs/adr/0058-pcm-driver-models.md): four
/// channels of 8-bit signed PCM mixed on the DAC in place of FM channel 6, one byte every 254 Z80 cycles (about
/// 14 093 Hz on NTSC, 13 964 Hz on PAL), with priorities 0–15.
/// <para><c>$2B</c>: after every block of 256 beats the driver turns the DAC on while a channel is playing, keeps it on
/// for three more blocks after the last one stops, then turns it off.</para>
/// </summary>
internal sealed class XgmPcmDriver : SoftwarePcmDriver
{
    /// <summary>XGM's PCM channels.</summary>
    public const int ChannelCount = 4;

    /// <summary>Master clocks per YM3438 sample: the chip runs at the master clock ÷ 7 and makes a sample every 144 of its clocks.</summary>
    private const int MasterClocksPerSample = 7 * 144;

    /// <summary>Master clocks per beat: the Z80 runs at the master clock ÷ 15 and sends a byte every 254 cycles (<c>drv_xgm.s80</c>).</summary>
    private const int MasterClocksPerBeat = 15 * 254;

    /// <summary>Beats per mixing block: one 256-byte write buffer (<c>drv_xgm.s80</c>, <c>next_write_buf</c>).</summary>
    private const int BlockBeats = 256;

    /// <summary>Blocks the DAC stays on after the last sound (<c>YM_2B_CNT</c>).</summary>
    private const int HoldBlocks = 3;

    private int _phase = MasterClocksPerBeat - MasterClocksPerSample;
    private int _hold;

    /// <summary>Creates the driver with every channel silent and the DAC off.</summary>
    public XgmPcmDriver()
        : base(ChannelCount, BlockBeats)
    {
    }

    /// <summary>Beats per second for a YM3438 at <paramref name="ymClockHz"/> (<see cref="Ym3438.NtscClockHz"/> or <see cref="Ym3438.PalClockHz"/>).</summary>
    public static double BeatRateHz(double ymClockHz) => ymClockHz * MasterClocksPerSample / MasterClocksPerBeat / 144;

    /// <summary>
    /// The music's <c>$5X id</c> command: plays <paramref name="sample"/> (8-bit signed, any length; padded to 256 bytes) on
    /// <paramref name="channel"/> 0–3 if <paramref name="priority"/> 0–15 is at least the playing sound's. True if it did.
    /// </summary>
    public bool Play(int channel, sbyte[] sample, int priority) => Start(channel, sample, priority, halfSpeed: false);

    /// <summary>The music's <c>$5X 00</c>: stops <paramref name="channel"/> if <paramref name="priority"/> is at least the playing sound's. True if it did.</summary>
    public bool Stop(int channel, int priority)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(priority);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(priority, MaxPriority);
        if (priority < PriorityOf(channel))
        {
            return false;
        }

        Silence(channel);
        return true;
    }

    /// <inheritdoc/>
    protected override bool NextSampleIsBeat()
    {
        _phase += MasterClocksPerSample;
        if (_phase < MasterClocksPerBeat)
        {
            return false;
        }

        _phase -= MasterClocksPerBeat;
        return true;
    }

    /// <inheritdoc/>
    protected override byte DacEnableAfterBlock(bool playingNow, bool soundInBlock)
    {
        if (playingNow)
        {
            _hold = HoldBlocks;
            return DacOn;
        }

        if (_hold > 0)
        {
            _hold--;
            return DacOn;
        }

        return DacOff;
    }

    /// <inheritdoc/>
    protected override void OnPlay() => _hold = HoldBlocks;
}
