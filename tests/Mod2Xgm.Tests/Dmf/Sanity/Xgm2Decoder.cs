using System.Buffers.Binary;
using Mod2Xgm.Cli;
using Mod2Xgm.Render;
using Mod2Xgm.Xgm;

namespace Mod2Xgm.Tests.Dmf.Sanity;

/// <summary>
/// Reads an XGM2 file back into the chip writes SGDK's driver makes, frame by frame (docs/formats/xgm.md#xgm2-layout-from-xgm2txt-version-byte-0x10),
/// for the DMF sanity check (docs/testing.md#dmf-sanity-check). It follows the spec, not the writer: waits, the frame
/// splitter (which ends the driver's frame without counting it) and the loop command are timed as the driver plays them.
/// Only the commands <see cref="Xgm2Writer"/> uses are read; any other throws, so a writer change can't go unnoticed.
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
                    at += 1 + (SlotRegisters * 4) + 2;
                    break;
                case 0x9:
                    byte level = fm[at + 1];
                    Add(frames, frame, ChipWrite.Fm(level & 1, 0x40 + low, level >> 1));
                    at += 2;
                    break;
                case 0xE:
                    int writes = (low & 0x07) + 1;
                    int rawPart = (low >> 3) & 1;
                    for (int w = 0; w < writes; w++)
                    {
                        Add(frames, frame, ChipWrite.Fm(rawPart, fm[at + 1 + (w * 2)], fm[at + 2 + (w * 2)]));
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

    private static NotSupportedException Unsupported(string stream, byte command, int at) =>
        new($"XGM2 {stream} command ${command:X2} at {at} isn't one the writer uses; teach the decoder (docs/formats/xgm.md).");
}
