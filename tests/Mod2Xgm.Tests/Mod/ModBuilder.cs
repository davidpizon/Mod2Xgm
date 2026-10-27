using System.Buffers.Binary;
using System.Text;

namespace Mod2Xgm.Tests.Mod;

/// <summary>
/// Builds MOD file bytes in code (docs/formats/mod.md), so tests never need copyrighted MOD files.
/// Defaults: 31-sample ProTracker layout, tag "M.K.", one order (pattern 0), all samples empty.
/// Set <see cref="Soundtracker"/> for the 15-sample layout (no tag, patterns at offset 600).
/// </summary>
internal sealed class ModBuilder
{
    private readonly SampleSpec?[] _samples = new SampleSpec?[31];
    private readonly Dictionary<int, byte[]> _patterns = [];
    private int[] _orders = [0];

    public string Title { get; set; } = "test song";
    public string Tag { get; set; } = "M.K.";

    /// <summary>Writes the 15-sample Soundtracker layout: 15 sample headers and no tag (<see cref="Tag"/> is ignored).</summary>
    public bool Soundtracker { get; init; }

    /// <summary>Bytes appended after the sample data.</summary>
    public byte[] TrailingBytes { get; init; } = [];
    public int RestartPosition { get; set; } = 127;

    /// <summary>Overrides the stored song length (default: number of orders).</summary>
    public int? SongLength { get; set; }

    /// <summary>Entries stored after the played orders (default: zeros).</summary>
    public int[] UnusedOrderEntries { get; set; } = [];

    /// <summary>Raw bytes for a sample name (to test non-ASCII or padding); overrides the string name.</summary>
    public ModBuilder WithSample(
        int number,
        string name,
        int length,
        int fineTune = 0,
        int volume = 64,
        int loopStart = 0,
        int loopLength = 0,
        Func<int, sbyte>? data = null,
        byte[]? rawName = null,
        int? loopStartWords = null,
        byte? fineTuneByte = null)
    {
        _samples[number - 1] = new SampleSpec(
            name,
            rawName,
            length,
            fineTuneByte ?? (byte)(fineTune & 0x0F),
            volume,
            loopStartWords ?? (loopStart / 2),
            loopLength,
            data ?? (i => (sbyte)(i % 64)));
        return this;
    }

    public ModBuilder WithOrders(params int[] orders)
    {
        _orders = orders;
        return this;
    }

    public ModBuilder WithCell(int pattern, int row, int channel, int sample, int period, int effect = 0, int parameter = 0)
    {
        byte[] bytes = Pattern(pattern);
        int at = ((row * 4) + channel) * 4;
        bytes[at] = (byte)((sample & 0xF0) | ((period >> 8) & 0x0F));
        bytes[at + 1] = (byte)(period & 0xFF);
        bytes[at + 2] = (byte)(((sample & 0x0F) << 4) | (effect & 0x0F));
        bytes[at + 3] = (byte)parameter;
        return this;
    }

    public byte[] Build()
    {
        using MemoryStream stream = new();
        Write(stream, Text(Title, 20));

        int sampleCount = Soundtracker ? 15 : 31;
        if (_samples.Skip(sampleCount).Any(spec => spec is not null))
        {
            throw new InvalidOperationException("A 15-sample module has samples 1-15 only.");
        }

        foreach (SampleSpec? spec in _samples.Take(sampleCount))
        {
            byte[] header = new byte[30];
            if (spec is not null)
            {
                byte[] name = spec.RawName ?? Text(spec.Name, 22);
                name.AsSpan(0, Math.Min(22, name.Length)).CopyTo(header);
                BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(22), (ushort)(spec.Length / 2));
                header[24] = spec.FineTuneByte;
                header[25] = (byte)spec.Volume;
                BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(26), (ushort)spec.LoopStartWords);
                BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(28), (ushort)(spec.LoopLength / 2));
            }

            Write(stream, header);
        }

        int[] table = new int[128];
        _orders.CopyTo(table, 0);
        UnusedOrderEntries.CopyTo(table, _orders.Length);
        stream.WriteByte((byte)(SongLength ?? _orders.Length));
        stream.WriteByte((byte)RestartPosition);
        Write(stream, [.. table.Select(o => (byte)o)]);
        if (!Soundtracker)
        {
            Write(stream, Encoding.Latin1.GetBytes(Tag));
        }

        int patternCount = table.Max() + 1;
        for (int p = 0; p < patternCount; p++)
        {
            Write(stream, _patterns.TryGetValue(p, out byte[]? bytes) ? bytes : new byte[1024]);
        }

        foreach (SampleSpec? spec in _samples.Take(sampleCount))
        {
            if (spec is not null)
            {
                Write(stream, [.. Enumerable.Range(0, spec.Length).Select(i => unchecked((byte)spec.Data(i)))]);
            }
        }

        Write(stream, TrailingBytes);
        return stream.ToArray();
    }

    /// <summary>A minimal valid MOD: one empty pattern and one short sample.</summary>
    public static byte[] Minimal() => new ModBuilder().WithSample(1, "kick", 64).Build();

    private byte[] Pattern(int index)
    {
        if (!_patterns.TryGetValue(index, out byte[]? bytes))
        {
            bytes = new byte[1024];
            _patterns[index] = bytes;
        }

        return bytes;
    }

    private static byte[] Text(string text, int length)
    {
        byte[] bytes = new byte[length];
        byte[] encoded = Encoding.Latin1.GetBytes(text);
        encoded.AsSpan(0, Math.Min(length, encoded.Length)).CopyTo(bytes);
        return bytes;
    }

    private static void Write(Stream stream, byte[] bytes) => stream.Write(bytes, 0, bytes.Length);

    private sealed record SampleSpec(
        string Name,
        byte[]? RawName,
        int Length,
        byte FineTuneByte,
        int Volume,
        int LoopStartWords,
        int LoopLength,
        Func<int, sbyte> Data);
}
