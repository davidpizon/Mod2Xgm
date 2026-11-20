using System.Buffers.Binary;
using Mod2Xgm.Audio;
using Mod2Xgm.Cli;
using Mod2Xgm.Render;
using Mod2Xgm.Render.Fm;
using Mod2Xgm.Render.Pcm;
using Mod2Xgm.Xgm;
using Serilog.Core;

namespace Mod2Xgm.Tests.Dmf.Sanity;

/// <summary>What one <see cref="ChipWrite"/> is.</summary>
internal enum ChipTarget
{
    /// <summary>A YM2612 register write.</summary>
    Fm,

    /// <summary>A byte to the SN76489's port.</summary>
    Psg,

    /// <summary>A PCM sample starting on a channel.</summary>
    PcmPlay,

    /// <summary>A PCM channel stopped.</summary>
    PcmStop,
}

/// <summary>
/// One thing a song tells the Genesis's sound chips, in a form every source of the DMF sanity check shares
/// (docs/testing.md#dmf-sanity-check): DefleMask's VGM export, the test-only DMF player and the decoded XGM2 file.
/// </summary>
/// <param name="Target">What it is.</param>
/// <param name="Part">The YM2612 part (0 or 1) of a register write.</param>
/// <param name="Register">The YM2612 register.</param>
/// <param name="Value">The YM2612 value, or the PSG byte.</param>
/// <param name="Channel">The PCM channel of a play or stop.</param>
/// <param name="Sample">The song's sample a play starts (the DMF sample number, which is also DefleMask's VGM data block number).</param>
/// <param name="RateHz">The rate a play plays at, in hertz.</param>
/// <param name="Data">A play's 8-bit signed data as the player plays it.</param>
/// <param name="HalfSpeed">True when an XGM2 play plays at half speed.</param>
internal readonly record struct ChipWrite(
    ChipTarget Target,
    int Part = 0,
    int Register = 0,
    int Value = 0,
    int Channel = 0,
    int Sample = -1,
    double RateHz = 0,
    sbyte[]? Data = null,
    bool HalfSpeed = false)
{
    /// <summary>A YM2612 register write.</summary>
    public static ChipWrite Fm(int part, int register, int value) => new(ChipTarget.Fm, part, register, value);

    /// <summary>A PSG byte.</summary>
    public static ChipWrite Psg(int value) => new(ChipTarget.Psg, Value: value);
}

/// <summary>
/// A song as frames of chip writes: one frame per tick of the song (DefleMask's tick, or one XGM2 frame), each with its
/// writes in order.
/// </summary>
/// <param name="Frames">Every frame of one pass through the song.</param>
/// <param name="LoopFrame">The frame the song goes back to after the last one, or <c>null</c> when it ends.</param>
/// <param name="FrameRateHz">Frames a second the source plays at.</param>
internal sealed record ChipLog(IReadOnlyList<IReadOnlyList<ChipWrite>> Frames, int? LoopFrame, double FrameRateHz)
{
    /// <summary>The YM2612 DAC data register, which the PCM players own.</summary>
    public const int RegDacData = 0x2A;

    /// <summary>The YM2612 DAC enable register.</summary>
    public const int RegDacEnable = 0x2B;

    /// <summary>VGM stream command: set the stream's frequency (docs: VGM 1.71 spec, command 0x92).</summary>
    private const int VgmStreamFrequency = 0x92;

    /// <summary>VGM stream command: stop the stream.</summary>
    private const int VgmStreamStop = 0x94;

    /// <summary>VGM stream command: start the stream from a data block ("fast call").</summary>
    private const int VgmStreamStart = 0x95;

    /// <summary>
    /// DefleMask's VGM export as frames: each tick's writes in one frame. A tick is the time unit every wait in the file is a
    /// multiple of (735 samples at 60 Hz; 787 at DefleMask's 56 Hz), so the ticks stay exact where 44 100 / rate isn't whole.
    /// DAC streams become PCM plays (the block played, at the stream's frequency) and stops.
    /// </summary>
    public static ChipLog FromVgm(VgmLog vgm)
    {
        ArgumentNullException.ThrowIfNull(vgm);
        int unit = TickUnit(vgm);
        List<List<ChipWrite>> frames = [];
        double rate = 0;
        foreach (VgmEvent e in vgm.Events)
        {
            int frame = e.Time / unit;
            while (frames.Count <= frame)
            {
                frames.Add([]);
            }

            switch (e.Chip)
            {
                case VgmChip.Ym2612:
                    frames[frame].Add(ChipWrite.Fm(e.Port, e.Register, e.Value));
                    break;
                case VgmChip.Sn76489:
                    frames[frame].Add(ChipWrite.Psg(e.Register));
                    break;
                default:
                    byte[] data = e.Data ?? [];
                    switch (e.Port)
                    {
                        case VgmStreamFrequency:
                            rate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(1));
                            break;
                        case VgmStreamStart:
                            int block = data[1] | (data[2] << 8);
                            sbyte[] pcm = [.. vgm.DataBlocks[block].Select(b => unchecked((sbyte)(b ^ 0x80)))];
                            frames[frame].Add(new ChipWrite(ChipTarget.PcmPlay, Sample: block, RateHz: rate, Data: pcm));
                            break;
                        case VgmStreamStop:
                            frames[frame].Add(new ChipWrite(ChipTarget.PcmStop));
                            break;
                    }

                    break;
            }
        }

        int total = (int)Math.Ceiling((double)vgm.TotalSamples / unit);
        while (frames.Count < total)
        {
            frames.Add([]);
        }

        int? loop = vgm.LoopSamples > 0 ? (vgm.TotalSamples - vgm.LoopSamples) / unit : null;
        return new ChipLog(frames, loop, VgmLog.SampleRate / unit);
    }

    /// <summary>The XGM2 writer's input as frames, with each play's song sample, rate and data.</summary>
    public static ChipLog FromStream(GenesisStream stream, double frameRateHz)
    {
        ArgumentNullException.ThrowIfNull(stream);
        List<IReadOnlyList<ChipWrite>> frames = [];
        foreach (IReadOnlyList<ChipEvent> events in stream.Frames)
        {
            frames.Add([.. events.Select(e => e.Kind switch
            {
                ChipEventKind.FmWrite => ChipWrite.Fm(e.Part, e.Register, e.Value),
                ChipEventKind.PsgWrite => ChipWrite.Psg(e.Value),
                ChipEventKind.PcmPlay => new ChipWrite(ChipTarget.PcmPlay, Channel: e.Channel, Sample: e.Sample, RateHz: e.HalfSpeed ? stream.PcmRateHz / 2 : stream.PcmRateHz, Data: e.Data, HalfSpeed: e.HalfSpeed),
                _ => new ChipWrite(ChipTarget.PcmStop, Channel: e.Channel),
            })]);
        }

        return new ChipLog(frames, stream.LoopFrame, frameRateHz);
    }

    /// <summary>
    /// What the project's XGM2 path would hand the writer for these frames (the control path of the sanity check): every
    /// write but the DAC registers, which SGDK's driver owns, and each play's data brought to the driver's rate and padded as
    /// the plan player pads it; every PCM command then goes ahead by the driver's lead, as <see cref="GenesisStream.RecordXgm2"/> does.
    /// </summary>
    public GenesisStream ToXgm2Stream(bool pal)
    {
        double ymClock = pal ? Ym3438.PalClockHz : Ym3438.NtscClockHz;
        double driverRate = Xgm2PcmDriver.BeatRateHz(ymClock);
        double frameRate = GenesisChips.FrameRateHz(pal ? Region.Pal : Region.Ntsc);
        Dictionary<(int Sample, double Rate), sbyte[]> copies = [];
        List<IReadOnlyList<ChipEvent>> frames = [];
        foreach (IReadOnlyList<ChipWrite> frame in Frames)
        {
            List<ChipEvent> events = [];
            foreach (ChipWrite w in frame)
            {
                switch (w.Target)
                {
                    case ChipTarget.Fm when w.Register is not (RegDacData or RegDacEnable):
                        events.Add(ChipEvent.Fm(w.Part, (byte)w.Register, (byte)w.Value));
                        break;
                    case ChipTarget.Psg:
                        events.Add(ChipEvent.Psg((byte)w.Value));
                        break;
                    case ChipTarget.PcmPlay:
                        bool half = w.RateHz <= driverRate / 2;
                        if (!copies.TryGetValue((w.Sample, w.RateHz), out sbyte[]? copy))
                        {
                            copy = DriverCopy(w.Data ?? [], w.RateHz, half ? driverRate / 2 : driverRate, half);
                            copies[(w.Sample, w.RateHz)] = copy;
                        }

                        events.Add(ChipEvent.Play(w.Channel, copy, half, w.Sample, $"sample {w.Sample}"));
                        break;
                    case ChipTarget.PcmStop:
                        events.Add(ChipEvent.Stop(w.Channel));
                        break;
                }
            }

            frames.Add(events);
        }

        ConversionNotes notes = new(Logger.None);
        IReadOnlyList<IReadOnlyList<ChipEvent>> ahead = GenesisStream.PcmAhead(frames, Xgm2PcmDriver.LeadFrames(ymClock, frameRate), LoopFrame, frameRate, notes);
        return new GenesisStream(ahead, LoopFrame, pal, driverRate);
    }

    /// <summary>
    /// 8-bit signed data at <paramref name="fromHz"/> as the XGM2 driver plays it: resampled to <paramref name="toHz"/> the
    /// way the converter resamples, then given the silent head the plan player gives every XGM2 copy.
    /// </summary>
    public static sbyte[] DriverCopy(sbyte[] data, double fromHz, double toHz, bool halfSpeed)
    {
        ArgumentNullException.ThrowIfNull(data);
        float[] points = [.. data.Select(b => b / 128f)];
        float[] resampled = SampleProcessor.Resample(points, fromHz, toHz, ResamplingMode.Smooth);
        sbyte[] pcm = SampleProcessor.ToSigned8(resampled, string.Empty, new ConversionNotes(Logger.None));
        return Xgm2PcmDriver.Padded(pcm, halfSpeed);
    }

    /// <summary>The VGM's tick: the largest whole number of samples every wait between writes is a multiple of.</summary>
    private static int TickUnit(VgmLog vgm)
    {
        int unit = 0;
        int last = 0;
        foreach (VgmEvent e in vgm.Events)
        {
            if (e.Time != last)
            {
                unit = Gcd(unit, e.Time - last);
                last = e.Time;
            }
        }

        // A song with one tick of writes (or none) falls back on the header's rate, and so does one whose writes are all
        // whole multiples of it (a short probe that writes only once a row).
        int nominal = vgm.RateHz > 0 ? (int)Math.Round(VgmLog.SampleRate / vgm.RateHz) : 0;
        if (unit <= 0 || vgm.Events.Count <= 1)
        {
            return Math.Max(1, nominal > 0 ? nominal : (int)VgmLog.SampleRate / 60);
        }

        return nominal > 0 && unit % nominal == 0 ? nominal : unit;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

}
