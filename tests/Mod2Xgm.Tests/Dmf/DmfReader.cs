using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Mod2Xgm.Tests.Dmf;

/// <summary>
/// Test-only reader for Genesis DMF files (plan T4.2, docs/formats/dmf.md): checks what the DMF writer (plan T5.2)
/// wrote, and opens files saved by DefleMask (file versions 24–27) for the probe files (plan T4.3).
/// <para>It is <b>strict</b>, because its job is catching writer bugs: anything the format or DefleMask doesn't
/// allow (a value out of range, a missing byte, an unknown version or system) throws
/// <see cref="InvalidDataException"/> naming the field and its byte offset in the decompressed data. Bytes left after
/// the last sample are counted (<see cref="DmfFile.TrailingBytes"/>), not rejected.</para>
/// </summary>
internal static class DmfReader
{
    /// <summary>The 16 bytes every DMF starts with.</summary>
    public const string Magic = ".DelekDefleMask.";

    /// <summary>Oldest file version read: the one the writer makes (DefleMask 1.0).</summary>
    public const int MinVersion = 24;

    /// <summary>Newest file version read (DefleMask 1.1.7 and later).</summary>
    public const int MaxVersion = 27;

    /// <summary>First version with pattern names in the matrix (DefleMask 1.1).</summary>
    private const int PatternNamesVersion = 25;

    /// <summary>First version with sample cut points (DefleMask 1.1.7).</summary>
    private const int SampleCutVersion = 27;

    /// <summary>Note value for "note off".</summary>
    private const int NoteOff = 100;

    /// <summary>Reads a DMF file: zlib-compressed bytes as stored on disk.</summary>
    public static DmfFile Read(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return ReadDecompressed(Decompress(file));
    }

    /// <summary>Undoes the zlib compression around a DMF file.</summary>
    public static byte[] Decompress(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        try
        {
            using ZLibStream zlib = new(new MemoryStream(file), CompressionMode.Decompress);
            using MemoryStream data = new();
            zlib.CopyTo(data);
            return data.ToArray();
        }
        catch (InvalidDataException error)
        {
            throw new InvalidDataException($"Not zlib-compressed data: {error.Message}", error);
        }
    }

    /// <summary>Reads the decompressed contents of a DMF file.</summary>
    public static DmfFile ReadDecompressed(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Cursor cursor = new(data);

        string magic = Encoding.Latin1.GetString(cursor.Bytes(Magic.Length, "format string"));
        Require(magic == Magic, cursor, 0, $"format string is \"{magic}\", not \"{Magic}\"");

        int version = cursor.Byte("file version");
        Require(version is >= MinVersion and <= MaxVersion, cursor, 1, $"file version {version} isn't {MinVersion}–{MaxVersion}");

        int systemByte = cursor.Byte("system");
        Require(systemByte is (int)DmfSystem.Genesis or (int)DmfSystem.GenesisExtendedChannel3, cursor, cursor.Offset - 1, $"system 0x{systemByte:X2} isn't Genesis (0x02 or 0x42)");
        DmfSystem system = (DmfSystem)systemByte;

        string name = cursor.String("song name");
        string author = cursor.String("song author");
        int highlightA = cursor.Byte("highlight A");
        int highlightB = cursor.Byte("highlight B");
        DmfTiming timing = ReadTiming(cursor);

        int rowsPerPattern = cursor.Int32("rows per pattern");
        Require(rowsPerPattern is >= 1 and <= 256, cursor, cursor.Offset - 4, $"rows per pattern is {rowsPerPattern}, not 1–256");
        int matrixRows = cursor.Byte("matrix rows");
        Require(matrixRows is >= 1 and <= 127, cursor, cursor.Offset - 1, $"matrix rows is {matrixRows}, not 1–127");

        int channelCount = DmfSystems.ChannelCount(system);
        (int[] Orders, string[]? Names)[] matrix = new (int[], string[]?)[channelCount];
        for (int channel = 0; channel < channelCount; channel++)
        {
            int[] orders = new int[matrixRows];
            string[]? names = version >= PatternNamesVersion ? new string[matrixRows] : null;
            for (int row = 0; row < matrixRows; row++)
            {
                orders[row] = cursor.Byte("pattern index");
                Require(orders[row] <= 0x7F, cursor, cursor.Offset - 1, $"channel {channel + 1} matrix row {row} names pattern {orders[row]}, above 127");
                if (names is not null)
                {
                    names[row] = cursor.String("pattern name");
                }
            }

            matrix[channel] = (orders, names);
        }

        DmfInstrument[] instruments = new DmfInstrument[cursor.Byte("instrument count")];
        for (int i = 0; i < instruments.Length; i++)
        {
            instruments[i] = ReadInstrument(cursor, i);
        }

        IReadOnlyList<int>[] wavetables = new IReadOnlyList<int>[cursor.Byte("wavetable count")];
        for (int i = 0; i < wavetables.Length; i++)
        {
            int size = cursor.Int32("wavetable size");
            Require(size >= 0, cursor, cursor.Offset - 4, $"wavetable {i} size is {size}");
            Require(size <= cursor.Remaining / 4, cursor, cursor.Offset, $"the file ends inside wavetable {i} ({size} × 4 bytes needed, {cursor.Remaining} left)");
            wavetables[i] = [.. Enumerable.Range(0, size).Select(_ => cursor.Int32("wavetable value"))];
        }

        DmfChannel[] channels = new DmfChannel[channelCount];
        for (int channel = 0; channel < channelCount; channel++)
        {
            int effectColumns = cursor.Byte("effect columns");
            Require(effectColumns is >= 1 and <= 4, cursor, cursor.Offset - 1, $"channel {channel + 1} has {effectColumns} effect columns, not 1–4");
            IReadOnlyList<DmfCell>[] patterns = new IReadOnlyList<DmfCell>[matrixRows];
            for (int matrixRow = 0; matrixRow < matrixRows; matrixRow++)
            {
                DmfCell[] rows = new DmfCell[rowsPerPattern];
                for (int row = 0; row < rowsPerPattern; row++)
                {
                    rows[row] = ReadCell(cursor, system, channel, effectColumns, instruments.Length, $"channel {channel + 1} matrix row {matrixRow} row {row}");
                }

                patterns[matrixRow] = rows;
            }

            channels[channel] = new DmfChannel(matrix[channel].Orders, matrix[channel].Names, effectColumns, patterns);
        }

        DmfSample[] samples = new DmfSample[cursor.Byte("sample count")];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = ReadSample(cursor, version, i);
        }

        return new DmfFile(version, system, name, author, highlightA, highlightB, timing, rowsPerPattern, channels, instruments, wavetables, samples, cursor.Remaining);
    }

    private static DmfTiming ReadTiming(Cursor cursor)
    {
        int timeBase = cursor.Byte("time base");
        int tickTime1 = cursor.Byte("tick time 1");
        Require(tickTime1 >= 1, cursor, cursor.Offset - 1, $"tick time 1 is 0");
        int tickTime2 = cursor.Byte("tick time 2");
        Require(tickTime2 >= 1, cursor, cursor.Offset - 1, $"tick time 2 is 0");
        int framesMode = cursor.Byte("frames mode");
        Require(framesMode is 0 or 1, cursor, cursor.Offset - 1, $"frames mode is {framesMode}, not 0 (PAL) or 1 (NTSC)");
        int usingCustomHz = cursor.Byte("using custom Hz");
        Require(usingCustomHz is 0 or 1, cursor, cursor.Offset - 1, $"using custom Hz is {usingCustomHz}, not 0 or 1");

        int hzAt = cursor.Offset;
        byte[] hzBytes = cursor.Bytes(3, "custom Hz value");
        int? customHz = ParseHz(hzBytes);
        Require(usingCustomHz == 0 || customHz is >= 1, cursor, hzAt, $"custom Hz bytes {Convert.ToHexString(hzBytes)} don't spell a rate (ASCII digits, then 0 padding)");

        return new DmfTiming(timeBase, tickTime1, tickTime2, framesMode == 1, usingCustomHz == 1, hzBytes, customHz);
    }

    /// <summary>The value 3 custom Hz bytes spell: ASCII digits, left-aligned, padded with 0; <c>null</c> otherwise.</summary>
    private static int? ParseHz(byte[] bytes)
    {
        int digits = Array.IndexOf(bytes, (byte)0);
        if (digits < 0)
        {
            digits = bytes.Length;
        }

        bool valid = digits > 0
            && bytes.Take(digits).All(b => b is >= (byte)'0' and <= (byte)'9')
            && bytes.Skip(digits).All(b => b == 0);
        return valid ? int.Parse(Encoding.ASCII.GetString(bytes, 0, digits), NumberStyles.None, CultureInfo.InvariantCulture) : null;
    }

    private static DmfInstrument ReadInstrument(Cursor cursor, int index)
    {
        string name = cursor.String("instrument name");
        int mode = cursor.Byte("instrument mode");
        Require(mode is 0 or 1, cursor, cursor.Offset - 1, $"instrument {index} mode is {mode}, not 0 (STD) or 1 (FM)");
        if (mode == 1)
        {
            int algorithm = cursor.Byte("ALG");
            int feedback = cursor.Byte("FB");
            int fms = cursor.Byte("FMS");
            int ams = cursor.Byte("AMS");
            DmfOperator[] operators = new DmfOperator[4];
            for (int op = 0; op < operators.Length; op++)
            {
                byte[] b = cursor.Bytes(12, "FM operator");
                operators[op] = new DmfOperator(b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7], b[8], b[9], b[10], b[11]);
            }

            return new DmfInstrument(name, new DmfFmInstrument(algorithm, feedback, fms, ams, operators), null);
        }

        DmfMacro volume = ReadMacro(cursor, "volume macro");
        DmfMacro arpeggio = ReadMacro(cursor, "arpeggio macro");
        int arpeggioMode = cursor.Byte("arpeggio mode");
        Require(arpeggioMode is 0 or 1, cursor, cursor.Offset - 1, $"instrument {index} arpeggio mode is {arpeggioMode}, not 0 or 1");
        DmfMacro dutyNoise = ReadMacro(cursor, "duty/noise macro");
        DmfMacro wavetable = ReadMacro(cursor, "wavetable macro");
        return new DmfInstrument(name, null, new DmfStdInstrument(volume, arpeggio, arpeggioMode, dutyNoise, wavetable));
    }

    /// <summary>Size byte (0–127), the 4-byte values, and a loop byte only when the size isn't 0.</summary>
    private static DmfMacro ReadMacro(Cursor cursor, string field)
    {
        int size = cursor.Byte(field + " size");
        Require(size <= 127, cursor, cursor.Offset - 1, $"{field} size is {size}, above 127");
        int[] values = [.. Enumerable.Range(0, size).Select(_ => cursor.Int32(field + " value"))];
        if (size == 0)
        {
            return new DmfMacro(values, -1);
        }

        int loop = (sbyte)cursor.Byte(field + " loop");
        Require(loop >= -1 && loop < size, cursor, cursor.Offset - 1, $"{field} loops at {loop}, outside its {size} values");
        return new DmfMacro(values, loop);
    }

    private static DmfCell ReadCell(Cursor cursor, DmfSystem system, int channel, int effectColumns, int instrumentCount, string where)
    {
        int at = cursor.Offset;
        int note = cursor.Int16("note");
        int octave = cursor.Int16("octave");
        Require(note is (>= 0 and <= 12) or NoteOff, cursor, at, $"{where}: note {note} isn't 0–12 or 100 (off)");
        Require(note != 0 || octave == 0, cursor, at, $"{where}: empty note with octave {octave}");

        at = cursor.Offset;
        int volume = cursor.Int16("volume");
        int maxVolume = DmfSystems.IsPsg(system, channel) ? 0xF : 0x7F;
        Require(volume >= -1 && volume <= maxVolume, cursor, at, $"{where}: volume {volume} isn't −1 or 0–{maxVolume:X}");

        DmfEffect[] effects = new DmfEffect[effectColumns];
        for (int i = 0; i < effects.Length; i++)
        {
            at = cursor.Offset;
            effects[i] = new DmfEffect(cursor.Int16("effect code"), cursor.Int16("effect value"));
            Require(effects[i].Code is >= -1 and <= 0xFF && effects[i].Value is >= -1 and <= 0xFF, cursor, at, $"{where}: effect {effects[i].Code},{effects[i].Value} isn't −1 or 0–FF");
        }

        at = cursor.Offset;
        int instrument = cursor.Int16("instrument");
        Require(instrument >= -1 && instrument < instrumentCount, cursor, at, instrumentCount == 0 ? (FormattableString)$"{where}: instrument {instrument}, but there are no instruments" : $"{where}: instrument {instrument} isn't −1 or 0–{instrumentCount - 1}");
        return new DmfCell(note, octave, volume, effects, instrument);
    }

    private static DmfSample ReadSample(Cursor cursor, int version, int index)
    {
        int size = cursor.Int32("sample size");
        Require(size >= 0, cursor, cursor.Offset - 4, $"sample {index} size is {size}");
        string name = cursor.String("sample name");
        int rate = cursor.Byte("sample rate");
        Require(rate <= 5, cursor, cursor.Offset - 1, $"sample {index} rate code is {rate}, not 0–5");
        int pitch = cursor.Byte("sample pitch");
        Require(pitch <= 10, cursor, cursor.Offset - 1, $"sample {index} pitch is {pitch}, not 0–10");
        int amp = cursor.Byte("sample amp");
        int bits = cursor.Byte("sample bits");
        Require(bits is 8 or 16, cursor, cursor.Offset - 1, $"sample {index} has {bits} bits, not 8 or 16");

        int? cutStart = null;
        int? cutEnd = null;
        if (version >= SampleCutVersion)
        {
            int at = cursor.Offset;
            cutStart = cursor.Int32("sample cut start");
            cutEnd = cursor.Int32("sample cut end");
            Require(cutStart >= 0 && cutEnd >= cutStart, cursor, at, $"sample {index} cut {cutStart}–{cutEnd} is backwards or negative");
        }

        Require(size <= cursor.Remaining / 2, cursor, cursor.Offset, $"the file ends inside sample {index}'s data ({size} × 2 bytes needed, {cursor.Remaining} left)");
        short[] data = [.. Enumerable.Range(0, size).Select(_ => (short)cursor.Int16("sample data"))];
        return new DmfSample(name, rate, pitch, amp, bits, cutStart, cutEnd, data);
    }

    private static void Require(bool condition, Cursor cursor, int offset, FormattableString message)
    {
        if (!condition)
        {
            throw cursor.Error(offset, FormattableString.Invariant(message));
        }
    }

    /// <summary>Reads little-endian fields from the decompressed data, failing with the field and offset when it runs out.</summary>
    private sealed class Cursor(byte[] data)
    {
        /// <summary>Offset of the next byte.</summary>
        public int Offset { get; private set; }

        /// <summary>Bytes not read yet.</summary>
        public int Remaining => data.Length - Offset;

        /// <summary>The next <paramref name="count"/> bytes of <paramref name="field"/>.</summary>
        public byte[] Bytes(int count, string field)
        {
            if (count > Remaining)
            {
                throw Error(Offset, $"the file ends inside the {field} ({count} bytes needed, {Remaining} left)");
            }

            byte[] bytes = data[Offset..(Offset + count)];
            Offset += count;
            return bytes;
        }

        /// <summary>A 1-byte field.</summary>
        public int Byte(string field) => Bytes(1, field)[0];

        /// <summary>A signed 16-bit little-endian field.</summary>
        public int Int16(string field) => BinaryPrimitives.ReadInt16LittleEndian(Bytes(2, field));

        /// <summary>A signed 32-bit little-endian field.</summary>
        public int Int32(string field) => BinaryPrimitives.ReadInt32LittleEndian(Bytes(4, field));

        /// <summary>1-byte length, then that many Latin-1 characters.</summary>
        public string String(string field) => Encoding.Latin1.GetString(Bytes(Byte(field + " length"), field));

        /// <summary>The exception for a bad field at <paramref name="offset"/>.</summary>
        public InvalidDataException Error(int offset, string message) =>
            new(FormattableString.Invariant($"DMF: {message} (at byte 0x{offset:X} of the decompressed data)."));
    }
}
