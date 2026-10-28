using System.Buffers.Binary;

namespace Mod2Xgm.Tests.Dmf;

/// <summary>
/// The chip writes in a VGM file, with their times (plan T4.3): just enough of the VGM format (v1.50+) to check what
/// DefleMask's VGM export of a probe plays — YM2612 and SN76489 writes, waits, data blocks and DAC streams.
/// </summary>
/// <param name="Ym2612ClockHz">YM2612 clock from the header.</param>
/// <param name="Sn76489ClockHz">SN76489 clock from the header.</param>
/// <param name="TotalSamples">Length in 44.1 kHz samples.</param>
/// <param name="Events">Every write and stream command, in order.</param>
/// <param name="DataBlockSizes">Size of each data block (DAC samples), in order.</param>
internal sealed record VgmLog(int Ym2612ClockHz, int Sn76489ClockHz, int TotalSamples, IReadOnlyList<VgmEvent> Events, IReadOnlyList<int> DataBlockSizes)
{
    /// <summary>VGM time base: samples per second.</summary>
    public const double SampleRate = 44100;

    /// <summary>Reads a VGM file.</summary>
    public static VgmLog Read(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length < 0x40 || file[0] != 'V' || file[1] != 'g' || file[2] != 'm' || file[3] != ' ')
        {
            throw new InvalidDataException("Not a VGM file.");
        }

        int version = I32(file, 0x08);
        int start = version >= 0x150 ? 0x34 + I32(file, 0x34) : 0x40;
        List<VgmEvent> events = [];
        List<int> blocks = [];
        int time = 0;
        for (int at = start; at < file.Length;)
        {
            byte command = file[at];
            switch (command)
            {
                case 0x50:
                    events.Add(new VgmEvent(time, VgmChip.Sn76489, 0, file[at + 1], 0));
                    at += 2;
                    break;
                case 0x52 or 0x53:
                    events.Add(new VgmEvent(time, VgmChip.Ym2612, command - 0x52, file[at + 1], file[at + 2]));
                    at += 3;
                    break;
                case 0x61:
                    time += BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 1));
                    at += 3;
                    break;
                case 0x62:
                    time += 735;
                    at++;
                    break;
                case 0x63:
                    time += 882;
                    at++;
                    break;
                case >= 0x70 and <= 0x7F:
                    time += (command & 0x0F) + 1;
                    at++;
                    break;
                case >= 0x80 and <= 0x8F:
                    time += command & 0x0F;
                    at++;
                    break;
                case 0x66:
                    return new VgmLog(I32(file, 0x2C), I32(file, 0x0C), I32(file, 0x18), events, blocks);
                case 0x67:
                    int size = I32(file, at + 3);
                    blocks.Add(size);
                    at += 7 + size;
                    break;
                case >= 0x90 and <= 0x95:
                    int length = command switch { 0x92 => 6, 0x93 => 11, 0x94 => 2, _ => 5 };
                    events.Add(new VgmEvent(time, VgmChip.Stream, command, 0, 0, file[(at + 1)..(at + length)]));
                    at += length;
                    break;
                case 0xE0:
                    at += 5;
                    break;
                default:
                    throw new InvalidDataException($"VGM command 0x{command:X2} at 0x{at:X} isn't handled.");
            }
        }

        throw new InvalidDataException("VGM data has no end command.");
    }

    /// <summary>The YM2612 writes to <paramref name="register"/> on <paramref name="port"/>.</summary>
    public IEnumerable<VgmEvent> Ym(int register, int port = 0) =>
        Events.Where(e => e.Chip == VgmChip.Ym2612 && e.Port == port && e.Register == register);

    /// <summary>The DAC stream commands with code <paramref name="command"/> (0x90–0x95).</summary>
    public IEnumerable<VgmEvent> Stream(int command) => Events.Where(e => e.Chip == VgmChip.Stream && e.Port == command);

    /// <summary>
    /// The FM frequency written by the high/low register pair (A4/A0 for a channel, AD/A9 etc. for CH3 operators) at
    /// <paramref name="time"/>: F-number × clock / 144 / 2^(21 − block).
    /// </summary>
    public double FmHz(int highRegister, int lowRegister, int time)
    {
        int high = Ym(highRegister).Last(e => e.Time <= time).Value;
        int low = Ym(lowRegister).Last(e => e.Time <= time).Value;
        int block = (high >> 3) & 7;
        int fnumber = ((high & 7) << 8) | low;
        return fnumber * (Ym2612ClockHz / 144.0) / (1 << (21 - block));
    }

    private static int I32(byte[] file, int at) => BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at));
}

/// <summary>What a <see cref="VgmEvent"/> writes to.</summary>
internal enum VgmChip
{
    /// <summary>The FM chip (commands 0x52/0x53).</summary>
    Ym2612,

    /// <summary>The PSG (command 0x50).</summary>
    Sn76489,

    /// <summary>A DAC stream command (0x90–0x95).</summary>
    Stream,
}

/// <summary>One chip write or stream command.</summary>
/// <param name="Time">When, in 44.1 kHz samples from the start.</param>
/// <param name="Chip">Which chip, or a stream command.</param>
/// <param name="Port">YM2612 port (0 or 1); for a stream command, the command byte.</param>
/// <param name="Register">YM2612 register; for the PSG, the byte written.</param>
/// <param name="Value">YM2612 value written.</param>
/// <param name="Data">A stream command's bytes after the command byte.</param>
internal sealed record VgmEvent(int Time, VgmChip Chip, int Port, int Register, int Value, byte[]? Data = null)
{
    /// <summary>Seconds from the start.</summary>
    public double Seconds => Time / VgmLog.SampleRate;
}
