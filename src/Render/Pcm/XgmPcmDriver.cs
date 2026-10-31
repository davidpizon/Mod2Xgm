using Mod2Xgm.Render.Fm;

namespace Mod2Xgm.Render.Pcm;

/// <summary>
/// The PCM half of SGDK's XGM (v1) Z80 driver, <c>src/snd/drv_xgm.s80</c> (docs/adr/0058-pcm-driver-models.md): four
/// channels of 8-bit signed PCM mixed on the DAC in place of FM channel 6, one byte every 254 Z80 cycles (about
/// 14 093 Hz on NTSC, 13 964 Hz on PAL), with priorities 0–15.
/// <para>Each add that clips costs the driver 11 Z80 cycles, which delays the beats after it: with four loud channels
/// clipping all the time the beat falls to about 13.5 kHz, 3.8 % slow.</para>
/// <para><c>$2B</c>: after every block of 256 beats the driver turns the DAC on while a channel is playing, keeps it on
/// for three more blocks after the last one stops, then turns it off.</para>
/// <para>Buffered (docs/adr/0066-pcm-drivers-checked-against-sgdk.md): SGDK's xgmtool sends every PCM command 3 frames
/// ahead of its frame, and the driver mixes it into the first 256-beat block it has not mixed yet, which is heard from
/// the first block that starts at least <see cref="StartLeadBeats"/> beats after the song frame. The model gets the
/// command on its song frame, so it starts the sound on that block.</para>
/// </summary>
internal sealed class XgmPcmDriver : SoftwarePcmDriver
{
    /// <summary>XGM's PCM channels.</summary>
    private const int ChannelCount = 4;

    /// <summary>Master clocks per YM3438 sample: the chip runs at the master clock ÷ 7 and makes a sample every 144 of its clocks.</summary>
    private const int MasterClocksPerSample = 7 * 144;

    /// <summary>Master clocks per beat: the Z80 runs at the master clock ÷ 15 and sends a byte every 254 cycles (<c>drv_xgm.s80</c>).</summary>
    private const int MasterClocksPerBeat = 15 * 254;

    /// <summary>Beats per mixing block: one 256-byte write buffer (<c>drv_xgm.s80</c>, <c>next_write_buf</c>).</summary>
    private const int BlockBeats = 256;

    /// <summary>Blocks the DAC stays on after the last sound (<c>YM_2B_CNT</c>).</summary>
    private const int HoldBlocks = 3;

    /// <summary>
    /// Beats from a PCM command's song frame to the earliest block it is heard from: measured on SGDK's driver at 247–280
    /// YM3438 samples (docs/adr/0066-pcm-drivers-checked-against-sgdk.md), 70 beats is 265 samples.
    /// </summary>
    public const int StartLeadBeats = 70;

    /// <summary>
    /// Master clocks a clipped add costs: the driver's overflow fix (<c>LD A, C</c> / <c>ADC $FF</c>) is 11 Z80 cycles
    /// more, and the beats after it come that much later (<c>drv_xgm.s80</c>, <c>readAndMix2</c>; measured,
    /// docs/adr/0066-pcm-drivers-checked-against-sgdk.md).
    /// </summary>
    private const int MasterClocksPerClip = 15 * 11;

    private int _phase = MasterClocksPerBeat - MasterClocksPerSample;
    private int _hold;

    /// <summary>Creates the driver with every channel silent and the DAC off.</summary>
    /// <param name="buffered">True (the default) to start plays and stops when SGDK's driver is heard doing them; false to start them on the next beat.</param>
    public XgmPcmDriver(bool buffered = true)
        : base(ChannelCount, BlockBeats, buffered)
    {
    }

    /// <summary>Beats per second for a YM3438 at <paramref name="ymClockHz"/> (<see cref="Ym3438.NtscClockHz"/> or <see cref="Ym3438.PalClockHz"/>).</summary>
    public static double BeatRateHz(double ymClockHz) => ymClockHz * MasterClocksPerSample / MasterClocksPerBeat / 144;

    /// <summary>
    /// The music's <c>$5X id</c> command: plays <paramref name="sample"/> (8-bit signed, any length; padded to 256 bytes) on
    /// <paramref name="channel"/> 0–3 if <paramref name="priority"/> 0–15 is at least the playing sound's. True if it did.
    /// </summary>
    /// <remarks>Buffered, the priority is checked when the play is heard, and the result is always true.</remarks>
    public bool Play(int channel, sbyte[] sample, int priority)
    {
        CheckPlay(channel, sample, priority);
        return Send(() => Start(channel, sample, priority, halfSpeed: false), isPlay: true);
    }

    /// <summary>The music's <c>$5X 00</c>: stops <paramref name="channel"/> if <paramref name="priority"/> is at least the playing sound's. True if it did.</summary>
    /// <remarks>Buffered, the priority is checked when the stop is heard, and the result is always true.</remarks>
    public bool Stop(int channel, int priority)
    {
        CheckPriority(priority);
        CheckChannel(channel);
        return Send(
            () =>
            {
                if (priority < PriorityOf(channel))
                {
                    return false;
                }

                Silence(channel);
                return true;
            },
            isPlay: false);
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
    protected override long StartBeat(long now, int blockBeats)
    {
        long earliest = now + StartLeadBeats;
        return (earliest + blockBeats - 1) / blockBeats * blockBeats;
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

    /// <inheritdoc/>
    /// <remarks>Each clipped add pushes the next beats 11 Z80 cycles later, so a loud, clipping mix plays a little slow.</remarks>
    protected override void OnClip(int clips) => _phase -= clips * MasterClocksPerClip;
}
