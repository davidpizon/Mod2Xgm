using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Mod2Xgm.Dmf;

namespace Mod2Xgm.Tests.Dmf;

/// <summary>
/// Builds Genesis DMF file bytes in code (docs/formats/dmf.md), so <see cref="DmfReader"/> can be tested before the
/// DMF writer exists (plan T4.2). Every field can be set, including to values the reader must reject.
/// Defaults: version 24, Genesis (10 channels), PAL, speed 6, 4 rows per pattern, 1 matrix row, 1 effect column, all
/// cells empty, no instruments, wavetables or samples.
/// </summary>
internal sealed class DmfBuilder
{
    private readonly Dictionary<(int Channel, int MatrixRow, int Row), DmfCell> _cells = [];
    private readonly Dictionary<(int Channel, int MatrixRow), int> _orders = [];
    private readonly Dictionary<(int Channel, int MatrixRow), string> _patternNames = [];
    private readonly Dictionary<int, int> _effectColumns = [];
    private readonly List<byte[]> _instruments = [];
    private readonly List<int[]> _wavetables = [];
    private readonly List<DmfSample> _samples = [];

    /// <summary>The 16-byte format string.</summary>
    public string Magic { get; init; } = DmfReader.Magic;

    /// <summary>File version.</summary>
    public int Version { get; init; } = 24;

    /// <summary>System byte.</summary>
    public int System { get; init; } = (int)DmfSystem.Genesis;

    /// <summary>Song name.</summary>
    public string Name { get; init; } = "test song";

    /// <summary>Song author.</summary>
    public string Author { get; init; } = "Mod2Xgm";

    /// <summary>First pattern highlight.</summary>
    public int HighlightA { get; init; } = 4;

    /// <summary>Second pattern highlight.</summary>
    public int HighlightB { get; init; } = 16;

    /// <summary>Time base.</summary>
    public int TimeBase { get; init; }

    /// <summary>Ticks per row on even rows.</summary>
    public int TickTime1 { get; init; } = 6;

    /// <summary>Ticks per row on odd rows.</summary>
    public int TickTime2 { get; init; } = 6;

    /// <summary>Frames mode byte: 0 = PAL, 1 = NTSC.</summary>
    public int FramesMode { get; init; }

    /// <summary>Using custom Hz byte.</summary>
    public int UsingCustomHz { get; init; }

    /// <summary>The 3 custom Hz bytes.</summary>
    public byte[] CustomHzBytes { get; init; } = "50\0"u8.ToArray();

    /// <summary>Rows per pattern (4-byte field).</summary>
    public int RowsPerPattern { get; init; } = 4;

    /// <summary>Rows in the pattern matrix.</summary>
    public int MatrixRows { get; init; } = 1;

    /// <summary>Bytes appended after the samples.</summary>
    public byte[] TrailingBytes { get; init; } = [];

    /// <summary>Channels the system byte implies (10 unless it is ext. CH3).</summary>
    private int ChannelCount => System == (int)DmfSystem.GenesisExtendedChannel3 ? 13 : 10;

    /// <summary>Sets one channel's pattern index for a matrix row (default: the matrix row's number).</summary>
    public DmfBuilder WithOrder(int channel, int matrixRow, int pattern)
    {
        _orders[(channel, matrixRow)] = pattern;
        return this;
    }

    /// <summary>Sets a pattern name (written in version 25 and later; default empty).</summary>
    public DmfBuilder WithPatternName(int channel, int matrixRow, string name)
    {
        _patternNames[(channel, matrixRow)] = name;
        return this;
    }

    /// <summary>Sets a channel's effect-column count (default 1).</summary>
    public DmfBuilder WithEffectColumns(int channel, int count)
    {
        _effectColumns[channel] = count;
        return this;
    }

    /// <summary>Sets one cell; effect columns the cell doesn't give are written empty.</summary>
    public DmfBuilder WithCell(int channel, int matrixRow, int row, int note = 0, int octave = 0, int volume = -1, int instrument = -1, params DmfEffect[] effects)
    {
        _cells[(channel, matrixRow, row)] = new DmfCell(note, octave, volume, effects, instrument);
        return this;
    }

    /// <summary>Adds an FM instrument (mode 1).</summary>
    public DmfBuilder WithFmInstrument(string name, DmfFmInstrument fm)
    {
        using MemoryStream stream = new();
        WriteString(stream, name);
        stream.WriteByte(1);
        Write(stream, fm.Algorithm, fm.Feedback, fm.Fms, fm.Ams);
        foreach (DmfOperator op in fm.Operators)
        {
            Write(stream, op.Am, op.Ar, op.Dr, op.Mult, op.Rr, op.Sl, op.Tl, op.Dt2, op.Rs, op.Dt, op.D2R, op.SsgMode);
        }

        _instruments.Add(stream.ToArray());
        return this;
    }

    /// <summary>Adds a STD instrument (mode 0).</summary>
    public DmfBuilder WithStdInstrument(string name, DmfStdInstrument std)
    {
        using MemoryStream stream = new();
        WriteString(stream, name);
        stream.WriteByte(0);
        WriteMacro(stream, std.Volume);
        WriteMacro(stream, std.Arpeggio);
        stream.WriteByte((byte)std.ArpeggioMode);
        WriteMacro(stream, std.DutyNoise);
        WriteMacro(stream, std.Wavetable);
        _instruments.Add(stream.ToArray());
        return this;
    }

    /// <summary>Adds an instrument from raw bytes (name, mode and body), for bytes the typed helpers can't express.</summary>
    public DmfBuilder WithRawInstrument(byte[] bytes)
    {
        _instruments.Add(bytes);
        return this;
    }

    /// <summary>Adds a wavetable.</summary>
    public DmfBuilder WithWavetable(params int[] values)
    {
        _wavetables.Add(values);
        return this;
    }

    /// <summary>Adds a sample; in version 27 and later its cut points default to the whole sample.</summary>
    public DmfBuilder WithSample(DmfSample sample)
    {
        _samples.Add(sample);
        return this;
    }

    /// <summary>A 4-operator FM patch whose bytes count up from <paramref name="first"/>, so operator order shows.</summary>
    public static DmfFmInstrument CountingFm(int first) =>
        new(first, first + 1, first + 2, first + 3, [.. Enumerable.Range(0, 4).Select(op => Operator(first + 4 + (op * 12)))]);

    /// <summary>An operator whose 12 bytes are <paramref name="first"/>, <paramref name="first"/> + 1, ….</summary>
    public static DmfOperator Operator(int first) =>
        new(first, first + 1, first + 2, first + 3, first + 4, first + 5, first + 6, first + 7, first + 8, first + 9, first + 10, first + 11);

    /// <summary>The file as stored: zlib-compressed.</summary>
    public byte[] Build() => Compress(BuildDecompressed());

    /// <summary>The file's contents before compression.</summary>
    public byte[] BuildDecompressed()
    {
        using MemoryStream stream = new();
        stream.Write(Encoding.Latin1.GetBytes(Magic));
        Write(stream, Version, System);
        WriteString(stream, Name);
        WriteString(stream, Author);
        Write(stream, HighlightA, HighlightB, TimeBase, TickTime1, TickTime2, FramesMode, UsingCustomHz);
        stream.Write(CustomHzBytes);
        WriteInt32(stream, RowsPerPattern);
        stream.WriteByte((byte)MatrixRows);

        for (int channel = 0; channel < ChannelCount; channel++)
        {
            for (int row = 0; row < MatrixRows; row++)
            {
                stream.WriteByte((byte)_orders.GetValueOrDefault((channel, row), row));
                if (Version >= 25)
                {
                    WriteString(stream, _patternNames.GetValueOrDefault((channel, row), string.Empty));
                }
            }
        }

        stream.WriteByte((byte)_instruments.Count);
        foreach (byte[] instrument in _instruments)
        {
            stream.Write(instrument);
        }

        stream.WriteByte((byte)_wavetables.Count);
        foreach (int[] wavetable in _wavetables)
        {
            WriteInt32(stream, wavetable.Length);
            foreach (int value in wavetable)
            {
                WriteInt32(stream, value);
            }
        }

        for (int channel = 0; channel < ChannelCount; channel++)
        {
            int columns = _effectColumns.GetValueOrDefault(channel, 1);
            stream.WriteByte((byte)columns);
            for (int matrixRow = 0; matrixRow < MatrixRows; matrixRow++)
            {
                for (int row = 0; row < RowsPerPattern; row++)
                {
                    WriteCell(stream, _cells.GetValueOrDefault((channel, matrixRow, row)), columns);
                }
            }
        }

        stream.WriteByte((byte)_samples.Count);
        foreach (DmfSample sample in _samples)
        {
            WriteInt32(stream, sample.Data.Count);
            WriteString(stream, sample.Name);
            Write(stream, sample.RateCode, sample.Pitch, sample.Amp, sample.Bits);
            if (Version >= 27)
            {
                WriteInt32(stream, sample.CutStart ?? 0);
                WriteInt32(stream, sample.CutEnd ?? sample.Data.Count);
            }

            foreach (short value in sample.Data)
            {
                WriteInt16(stream, value);
            }
        }

        stream.Write(TrailingBytes);
        return stream.ToArray();
    }

    /// <summary>zlib-compresses <paramref name="data"/>, as DMF files are stored.</summary>
    public static byte[] Compress(byte[] data)
    {
        using MemoryStream stream = new();
        using (ZLibStream zlib = new(stream, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return stream.ToArray();
    }

    /// <summary>A cell, or an empty one; effect columns beyond the cell's effects are written empty.</summary>
    private static void WriteCell(Stream stream, DmfCell? cell, int columns)
    {
        WriteInt16(stream, cell?.Note ?? 0);
        WriteInt16(stream, cell?.Octave ?? 0);
        WriteInt16(stream, cell?.Volume ?? -1);
        for (int i = 0; i < columns; i++)
        {
            DmfEffect effect = cell is not null && i < cell.Effects.Count ? cell.Effects[i] : DmfEffect.Empty;
            WriteInt16(stream, effect.Code);
            WriteInt16(stream, effect.Value);
        }

        WriteInt16(stream, cell?.Instrument ?? -1);
    }

    /// <summary>Size byte, 4-byte values, and the loop byte only when there are values.</summary>
    private static void WriteMacro(Stream stream, DmfMacro macro)
    {
        stream.WriteByte((byte)macro.Values.Count);
        foreach (int value in macro.Values)
        {
            WriteInt32(stream, value);
        }

        if (macro.Values.Count > 0)
        {
            stream.WriteByte((byte)(sbyte)macro.Loop);
        }
    }

    /// <summary>Each value as one byte.</summary>
    private static void Write(Stream stream, params int[] bytes)
    {
        foreach (int value in bytes)
        {
            stream.WriteByte((byte)value);
        }
    }

    /// <summary>1-byte length, then Latin-1 characters.</summary>
    private static void WriteString(Stream stream, string text)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(text);
        stream.WriteByte((byte)bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteInt16(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, (short)value);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
