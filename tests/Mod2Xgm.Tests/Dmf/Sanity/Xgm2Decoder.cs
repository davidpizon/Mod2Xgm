using System.Buffers.Binary;
using Mod2Xgm.Cli;
using Mod2Xgm.Render;
using Mod2Xgm.Xgm;

namespace Mod2Xgm.Tests.Dmf.Sanity;

/// <summary>
/// Reads an XGM2 file back into the chip writes SGDK's driver makes, frame by frame (docs/formats/xgm.md#xgm2-layout-from-xgm2txt-version-byte-0x10),
/// for the DMF sanity check (docs/testing.md#dmf-sanity-check). It follows the spec, not the writer: waits, the frame
/// splitter (which ends the driver's frame without counting it) and the loop command are timed as the driver plays them.
/// The delta, pan and frequency commands are read against what the driver stored, as <c>drv_xgm2_ym_fct.i80</c> keeps it
/// (<see cref="DriverState"/>): its own model, not the writer's (docs/adr/0139-xgm2-frame-budget.md). Only the commands
/// <see cref="Xgm2Writer"/> uses are read; any other throws, so a writer change can't go unnoticed.
/// </summary>
internal static class Xgm2Decoder
{
    private const int SidOffset = 0x0C;
    private const int SidEntries = 124;
    private const int DataOffset = SidOffset + (SidEntries * 2);
    private const int Alignment = 256;
    private const int NoLoop = 0xFFFFFF;
    private const int FlagPal = 0x01;

    /// <summary>The 7 slot registers a <c>$2x</c> load instrument carries, 4 slots each, then <c>$B0</c> and <c>$B4</c>.</summary>
    private const int SlotRegisters = 7;

    /// <summary>
    /// Decodes <paramref name="file"/>; <paramref name="samples"/> (the writer's report) names the song sample behind each
    /// id. PCM plays carry their stored bytes (signed, as the driver gets them) and rate.
    /// </summary>
    public static ChipLog Decode(byte[] file, IReadOnlyList<Xgm2Sample> samples)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(samples);
        if (file.Length < DataOffset || file[0] != 'X' || file[1] != 'G' || file[2] != 'M' || file[3] != '2')
        {
            throw new InvalidDataException("Not an XGM2 file.");
        }

        bool pal = (file[5] & FlagPal) != 0;
        int sdatLength = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6)) * Alignment;
        int fmLength = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(8)) * Alignment;
        int psgLength = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(10)) * Alignment;
        byte[] sdat = file[DataOffset..(DataOffset + sdatLength)];
        ReadOnlySpan<byte> fm = file.AsSpan(DataOffset + sdatLength, fmLength);
        ReadOnlySpan<byte> psg = file.AsSpan(DataOffset + sdatLength + fmLength, psgLength);
        sbyte[][] data = [.. Enumerable.Range(0, samples.Count).Select(i => SampleBytes(file, sdat, i))];

        List<List<ChipWrite>> frames = [];
        int? fmLoop = DecodeFm(fm, frames, samples, data);
        int? psgLoop = DecodePsg(psg, frames);
        if (fmLoop != psgLoop)
        {
            throw new InvalidDataException($"The FM stream loops to frame {fmLoop?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} and the PSG stream to {psgLoop?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}.");
        }

        return new ChipLog([.. frames], fmLoop, GenesisChips.FrameRateHz(pal ? Region.Pal : Region.Ntsc));
    }

    /// <summary>Sample id <paramref name="index"/> + 1's bytes: from its SID entry to the next, signed as the driver gets them (ADR 0127).</summary>
    private static sbyte[] SampleBytes(byte[] file, byte[] sdat, int index)
    {
        int start = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(SidOffset + (index * 2))) * Alignment;
        int end = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(SidOffset + ((index + 1) * 2))) * Alignment;
        return [.. sdat[start..end].Select(b => unchecked((sbyte)(b ^ 0x80)))];
    }

    /// <summary>Decodes the FM stream into <paramref name="frames"/>; returns the frame the loop goes back to, or <c>null</c>.</summary>
    private static int? DecodeFm(ReadOnlySpan<byte> fm, List<List<ChipWrite>> frames, IReadOnlyList<Xgm2Sample> samples, sbyte[][] data)
    {
        Dictionary<int, int> frameAt = [];
        DriverState driver = new();
        int frame = 0;
        int at = 0;
        while (true)
        {
            frameAt.TryAdd(at, frame);
            byte command = fm[at];
            int high = command >> 4;
            int low = command & 0x0F;
            switch (high)
            {
                case 0x0:
                    frame += low == 0x0F ? fm[at + 1] + 16 : low + 1;
                    at += low == 0x0F ? 2 : 1;
                    break;
                case 0x1:
                    int id = fm[at + 1];
                    int channel = low & 0x03;
                    if (id == 0)
                    {
                        Add(frames, frame, new ChipWrite(ChipTarget.PcmStop, Channel: channel));
                    }
                    else
                    {
                        Xgm2Sample sample = samples[id - 1];
                        Add(frames, frame, new ChipWrite(ChipTarget.PcmPlay, Channel: channel, Sample: sample.Sample, RateHz: sample.RateHz, Data: data[id - 1], HalfSpeed: (low & 0x04) != 0));
                    }

                    at += 2;
                    break;
                case 0x2:
                    int part = (low >> 2) & 1;
                    int ch = low & 0x03;
                    int d = at + 1;
                    for (int r = 0; r < SlotRegisters; r++)
                    {
                        for (int s = 0; s < 4; s++)
                        {
                            Add(frames, frame, ChipWrite.Fm(part, 0x30 + (r << 4) + (s << 2) + ch, fm[d++]));
                        }
                    }

                    Add(frames, frame, ChipWrite.Fm(part, 0xB0 + ch, fm[d++]));
                    Add(frames, frame, ChipWrite.Fm(part, 0xB4 + ch, fm[d]));
                    for (int s = 0; s < 4; s++)
                    {
                        driver.Tl[(part * 16) + (s * 4) + ch] = fm[at + 1 + 4 + s];
                    }

                    driver.Pan[(part * 3) + ch] = fm[d];
                    at += 1 + (SlotRegisters * 4) + 2;
                    break;
                case 0x3 or 0x8:
                    // Frequency set (+ end of frame): 14 bits, high byte first, bit 14 a key off before, bit 15 a key on after.
                    SetFrequency(frames, frame, driver, low, fm[at + 1], fm[at + 2], keys: true);
                    at += 3;
                    frame += high == 0x8 ? 1 : 0;
                    break;
                case 0x4:
                    Add(frames, frame, ChipWrite.Fm(0, 0x28, (low & 0x07) | ((low & 0x08) != 0 ? 0xF0 : 0x00)));
                    at++;
                    break;
                case 0x5:
                    // Key sequence: bit 3 clear is off then on, set is on then off.
                    bool onFirst = (low & 0x08) != 0;
                    Add(frames, frame, ChipWrite.Fm(0, 0x28, (low & 0x07) | (onFirst ? 0xF0 : 0x00)));
                    Add(frames, frame, ChipWrite.Fm(0, 0x28, (low & 0x07) | (onFirst ? 0x00 : 0xF0)));
                    at++;
                    break;
                case 0x6 or 0x7:
                    // Pan: the driver keeps the stored AMS/PMS bits and puts the pan in bits 7-6.
                    int panPart = high == 0x7 ? 1 : 0;
                    int panChannel = low & 0x03;
                    int stored = driver.Pan[(panPart * 3) + panChannel];
                    if (stored < 0)
                    {
                        throw new InvalidDataException($"XGM2 pan command ${command:X2} at {at} before the driver stored a pan for its channel.");
                    }

                    int pan = (stored & 0x3F) | (((low >> 2) & 0x03) << 6);
                    driver.Pan[(panPart * 3) + panChannel] = pan;
                    Add(frames, frame, ChipWrite.Fm(panPart, 0xB4 + panChannel, pan));
                    at++;
                    break;
                case 0x9:
                    byte level = fm[at + 1];
                    driver.Tl[((level & 1) * 16) + low] = level >> 1;
                    Add(frames, frame, ChipWrite.Fm(level & 1, 0x40 + low, level >> 1));
                    at += 2;
                    break;
                case 0xA or 0xB:
                    // Frequency delta (+ end of frame): b0 subtracts, b7-b1 the delta less 1, on the driver's 16 bits.
                    byte step = fm[at + 1];
                    ref int freq = ref driver.Frequency(low);
                    if (freq < 0)
                    {
                        throw new InvalidDataException($"XGM2 frequency delta ${command:X2} at {at} before the driver stored a frequency.");
                    }

                    int delta = (step >> 1) + 1;
                    int next = (freq + ((step & 1) != 0 ? -delta : delta)) & 0xFFFF;
                    SetFrequency(frames, frame, driver, low, next >> 8, next & 0xFF, keys: false);
                    at += 2;
                    frame += high == 0xB ? 1 : 0;
                    break;
                case 0xD:
                    // TL delta + end of frame: b0 the part, b1 subtracts, b7-b2 the delta less 1.
                    byte change = fm[at + 1];
                    int tlPart = change & 1;
                    int tl = driver.Tl[(tlPart * 16) + low];
                    if (tl < 0)
                    {
                        throw new InvalidDataException($"XGM2 TL delta ${command:X2} at {at} before the driver stored a level.");
                    }

                    int tlDelta = (change >> 2) + 1;
                    tl = (tl + ((change & 2) != 0 ? -tlDelta : tlDelta)) & 0xFF;
                    driver.Tl[(tlPart * 16) + low] = tl;
                    Add(frames, frame, ChipWrite.Fm(tlPart, 0x40 + low, tl & 0x7F));
                    at += 2;
                    frame++;
                    break;
                case 0xE:
                    int writes = (low & 0x07) + 1;
                    int rawPart = (low >> 3) & 1;
                    for (int w = 0; w < writes; w++)
                    {
                        int register = fm[at + 1 + (w * 2)];
                        int value = fm[at + 2 + (w * 2)];
                        if (register is >= 0xB4 and <= 0xB6)
                        {
                            driver.Pan[(rawPart * 3) + (register & 0x03)] = value;
                        }

                        Add(frames, frame, ChipWrite.Fm(rawPart, register, value));
                    }

                    at += 1 + (writes * 2);
                    break;
                case 0xF:
                    switch (low)
                    {
                        case 0x0:
                            // The frame splitter ends the driver's frame without counting it (docs/formats/xgm.md).
                            frame++;
                            at++;
                            break;
                        case 0x8:
                            Add(frames, frame, ChipWrite.Fm(0, 0x28, fm[at + 1]));
                            at += 2;
                            break;
                        case 0x9:
                            Add(frames, frame, ChipWrite.Fm(0, 0x22, fm[at + 1]));
                            at += 2;
                            break;
                        case 0xA or 0xB:
                            Add(frames, frame, ChipWrite.Fm(0, 0x27, low == 0xA ? 0x40 : 0x00));
                            at++;
                            break;
                        case 0xC or 0xD:
                            Add(frames, frame, ChipWrite.Fm(0, ChipLog.RegDacEnable, low == 0xC ? 0x80 : 0x00));
                            at++;
                            break;
                        case 0xF:
                            // The loop command ends the frame it is in.
                            int target = fm[at + 1] | (fm[at + 2] << 8) | (fm[at + 3] << 16);
                            Add(frames, frame, null);
                            return target == NoLoop ? null : frameAt[target];
                        default:
                            throw Unsupported("FM", command, at);
                    }

                    break;
                default:
                    throw Unsupported("FM", command, at);
            }
        }
    }

    /// <summary>
    /// A frequency set as the driver makes it: a key off first if asked (bit 14), the high byte then the low byte (to
    /// <c>$AC</c>/<c>$A8</c> for a CH3 slot, bit 3 of <paramref name="x"/>), then a key on if asked (bit 15). The chip
    /// ignores bits 7-6 of the high byte, where the driver leaves the key flags, so they are written as 0.
    /// </summary>
    private static void SetFrequency(List<List<ChipWrite>> frames, int frame, DriverState driver, int x, int high, int low, bool keys)
    {
        driver.Frequency(x) = (high << 8) | low;
        bool slot = (x & 0x08) != 0;
        int part = slot ? 0 : (x >> 2) & 1;
        int index = x & 0x03;
        int key = (part * 4) + index;
        if (keys && !slot && (high & 0x40) != 0)
        {
            Add(frames, frame, ChipWrite.Fm(0, 0x28, key));
        }

        Add(frames, frame, ChipWrite.Fm(part, (slot ? 0xAC : 0xA4) + index, high & 0x3F));
        Add(frames, frame, ChipWrite.Fm(part, (slot ? 0xA8 : 0xA0) + index, low));
        if (keys && !slot && (high & 0x80) != 0)
        {
            Add(frames, frame, ChipWrite.Fm(0, 0x28, 0xF0 | key));
        }
    }

    /// <summary>Decodes the PSG stream into <paramref name="frames"/> as PSG port bytes; returns the loop frame, or <c>null</c>.</summary>
    private static int? DecodePsg(ReadOnlySpan<byte> psg, List<List<ChipWrite>> frames)
    {
        Dictionary<int, int> frameAt = [];
        int frame = 0;
        int at = 0;
        while (true)
        {
            frameAt.TryAdd(at, frame);
            byte command = psg[at];
            switch (command)
            {
                case <= 0x0D:
                    frame += command + 1;
                    at++;
                    break;
                case 0x0E:
                    frame += psg[at + 1] + 15;
                    at += 2;
                    break;
                case 0x0F:
                    int target = psg[at + 1] | (psg[at + 2] << 8) | (psg[at + 3] << 16);
                    Add(frames, frame, null);
                    return target == NoLoop ? null : frameAt[target];
                case >= 0x20 and <= 0x3F:
                    int channel = (command >> 2) & 0x03;
                    int period = ((command & 0x03) << 8) | psg[at + 1];
                    if (channel == 3)
                    {
                        Add(frames, frame, ChipWrite.Psg(0xE0 | (period & 0x0F)));
                    }
                    else
                    {
                        Add(frames, frame, ChipWrite.Psg(0x80 | (channel << 5) | (period & 0x0F)));
                        Add(frames, frame, ChipWrite.Psg((period >> 4) & 0x3F));
                    }

                    at += 2;
                    if (command >= 0x30)
                    {
                        frame++;
                    }

                    break;
                case >= 0x80 and <= 0xBF:
                    Add(frames, frame, ChipWrite.Psg(0x90 | (((command >> 4) & 0x03) << 5) | (command & 0x0F)));
                    at++;
                    break;
                default:
                    throw Unsupported("PSG", command, at);
            }
        }
    }

    /// <summary>Adds <paramref name="write"/> to <paramref name="frame"/>, making the frames up to it; <c>null</c> only makes them.</summary>
    private static void Add(List<List<ChipWrite>> frames, int frame, ChipWrite? write)
    {
        while (frames.Count <= frame)
        {
            frames.Add([]);
        }

        if (write is { } w)
        {
            frames[frame].Add(w);
        }
    }

    /// <summary>What SGDK's XGM2 driver stores for the delta and pan commands (FM_STATE in <c>drv_xgm2_ym_fct.i80</c>); −1 where nothing was stored yet.</summary>
    private sealed class DriverState
    {
        private readonly int[] _frequency = [.. Enumerable.Repeat(-1, 6)];
        private readonly int[] _slotFrequency = [-1, -1, -1];

        /// <summary>Each slot's total level, by part × 16 + the register's low nibble.</summary>
        public int[] Tl { get; } = [.. Enumerable.Repeat(-1, 32)];

        /// <summary>Each channel's <c>$B4</c> byte, by part × 3 + channel.</summary>
        public int[] Pan { get; } = [.. Enumerable.Repeat(-1, 6)];

        /// <summary>The 16 bits stored for the channel or CH3 slot a frequency command's <paramref name="x"/> names.</summary>
        public ref int Frequency(int x) => ref (x & 0x08) != 0 ? ref _slotFrequency[x & 0x03] : ref _frequency[((x >> 2) & 1) * 3 + (x & 0x03)];
    }

    private static NotSupportedException Unsupported(string stream, byte command, int at) =>
        new($"XGM2 {stream} command ${command:X2} at {at} isn't one the writer uses; teach the decoder (docs/formats/xgm.md).");
}
