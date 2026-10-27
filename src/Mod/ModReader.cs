using System.Buffers.Binary;
using System.Text;

namespace Mod2Xgm.Mod;

/// <summary>
/// Reads MOD files (docs/formats/mod.md). All multi-byte values are big-endian. Malformed or unsupported files throw
/// <see cref="ConversionException"/> with <see cref="ExitCode.InvalidInput"/> and the byte offset; recoverable damage
/// (truncated sample data, loops past the sample end) is repaired and reported as a conversion note.
/// </summary>
internal static class ModReader
{
    public const int TitleLength = 20;
    public const int SampleHeaderSize = 30;
    public const int SampleNameLength = 22;
    public const int PatternSize = ModPattern.Rows * 4 * 4; // 64 rows × 4 channels × 4 bytes
    public const int MaxOrders = 128;

    // 31-sample (ProTracker) layout.
    private const int SampleCount31 = 31;
    private const int SongLengthOffset31 = TitleLength + (SampleCount31 * SampleHeaderSize); // 950
    private const int TagOffset = SongLengthOffset31 + 2 + MaxOrders;                       // 1080
    private const int PatternOffset31 = TagOffset + 4;                                      // 1084

    private static readonly string[] FourChannelTags = ["M.K.", "M!K!", "FLT4", "4CHN"];

    public static ModModule Read(ReadOnlySpan<byte> data, ConversionNotes notes)
    {
        if (data.Length < PatternOffset31)
        {
            throw Invalid(data.Length, $"the file is only {data.Length} bytes; a 31-sample MOD header needs {PatternOffset31}");
        }

        string tag = Latin1(data.Slice(TagOffset, 4));
        if (Array.IndexOf(FourChannelTags, tag) < 0)
        {
            throw new ConversionException(ExitCode.InvalidInput, UnsupportedTagMessage(tag));
        }

        return Read31(data, tag, notes);
    }

    private static ModModule Read31(ReadOnlySpan<byte> data, string tag, ConversionNotes notes)
    {
        const int channels = 4;

        int songLength = data[SongLengthOffset31];
        if (songLength is < 1 or > MaxOrders)
        {
            throw Invalid(SongLengthOffset31, $"song length {songLength} is outside 1–{MaxOrders}");
        }

        int restart = data[SongLengthOffset31 + 1];
        int[] orderTable = data.Slice(SongLengthOffset31 + 2, MaxOrders).ToArray().Select(b => (int)b).ToArray();

        var headers = new (int Number, string Name, int Length, int FineTune, int Volume, int LoopStart, int LoopLength)[SampleCount31];
        for (int i = 0; i < SampleCount31; i++)
        {
            headers[i] = ReadSampleHeader(data, TitleLength + (i * SampleHeaderSize), i + 1);
        }

        int patternCount = PatternCount(data.Length, orderTable, songLength, headers.Sum(h => (long)h.Length));
        int sampleDataOffset = PatternOffset31 + (patternCount * PatternSize);
        if (data.Length < sampleDataOffset)
        {
            throw Invalid(data.Length, $"the file ends inside the pattern data ({patternCount} patterns need {sampleDataOffset} bytes)");
        }

        List<ModPattern> patterns = [];
        for (int p = 0; p < patternCount; p++)
        {
            patterns.Add(ReadPattern(data.Slice(PatternOffset31 + (p * PatternSize), PatternSize), channels));
        }

        List<ModSample> samples = [];
        int offset = sampleDataOffset;
        foreach (var header in headers)
        {
            int available = Math.Clamp(data.Length - offset, 0, header.Length);
            if (available < header.Length)
            {
                notes.Add(
                    "Sample {Sample} {Name}: the file ends early; kept {Kept} of {Length} bytes",
                    header.Number,
                    header.Name,
                    available,
                    header.Length);
            }

            sbyte[] pcm = new sbyte[available];
            data.Slice(offset, available).CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pcm.AsSpan()));
            offset += available;

            (int loopStart, int loopLength) = ClampLoop(header.Number, header.Name, header.LoopStart, header.LoopLength, available, notes);
            samples.Add(new ModSample
            {
                Number = header.Number,
                Name = header.Name,
                FineTune = header.FineTune,
                Volume = header.Volume,
                LoopStart = loopStart,
                LoopLength = loopLength,
                Data = pcm,
            });
        }

        return new ModModule
        {
            Title = Name(data[..TitleLength]),
            Format = ModFormat.ProTracker31,
            Tag = tag,
            ChannelCount = channels,
            Samples = samples,
            SongLength = songLength,
            RestartPosition = restart,
            OrderTable = orderTable,
            Patterns = patterns,
            FileSize = data.Length,
        };
    }

    /// <summary>
    /// ProTracker stores every pattern up to the highest entry in the whole 128-entry order table, but some trackers
    /// leave garbage in the unused entries. Choose the count (all entries vs. played orders) whose expected file size
    /// — header + patterns + sample data — is closest to the real size; ties go to all entries.
    /// </summary>
    internal static int PatternCount(long fileLength, IReadOnlyList<int> orderTable, int songLength, long sampleBytes)
    {
        int all = orderTable.Max() + 1;
        int played = orderTable.Take(songLength).Max() + 1;
        if (all == played)
        {
            return all;
        }

        long Distance(int patterns) => Math.Abs(fileLength - (PatternOffset31 + ((long)patterns * PatternSize) + sampleBytes));
        return Distance(all) <= Distance(played) ? all : played;
    }

    private static (int Number, string Name, int Length, int FineTune, int Volume, int LoopStart, int LoopLength) ReadSampleHeader(
        ReadOnlySpan<byte> data,
        int offset,
        int number)
    {
        ReadOnlySpan<byte> header = data.Slice(offset, SampleHeaderSize);
        int fineTuneNibble = header[24] & 0x0F;
        return (
            number,
            Name(header[..SampleNameLength]),
            BinaryPrimitives.ReadUInt16BigEndian(header[22..]) * 2,
            fineTuneNibble > 7 ? fineTuneNibble - 16 : fineTuneNibble,
            header[25],
            BinaryPrimitives.ReadUInt16BigEndian(header[26..]) * 2,
            BinaryPrimitives.ReadUInt16BigEndian(header[28..]) * 2);
    }

    private static ModPattern ReadPattern(ReadOnlySpan<byte> bytes, int channels)
    {
        ModCell[] cells = new ModCell[ModPattern.Rows * channels];
        for (int i = 0; i < cells.Length; i++)
        {
            ReadOnlySpan<byte> c = bytes.Slice(i * 4, 4);
            cells[i] = new ModCell(
                Sample: (c[0] & 0xF0) | (c[2] >> 4),
                Period: ((c[0] & 0x0F) << 8) | c[1],
                Effect: c[2] & 0x0F,
                EffectParameter: c[3]);
        }

        return new ModPattern(channels, cells);
    }

    private static (int Start, int Length) ClampLoop(int number, string name, int start, int length, int sampleLength, ConversionNotes notes)
    {
        if (length <= 2)
        {
            return (0, 0);
        }

        if (start >= sampleLength)
        {
            notes.Add("Sample {Sample} {Name}: loop starts past the sample end; loop removed", number, name);
            return (0, 0);
        }

        if (start + length > sampleLength)
        {
            notes.Add("Sample {Sample} {Name}: loop runs past the sample end; shortened", number, name);
            return (start, sampleLength - start);
        }

        return (start, length);
    }

    private static string UnsupportedTagMessage(string tag)
    {
        int channels = tag switch
        {
            "6CHN" => 6,
            "8CHN" or "FLT8" or "CD81" or "OKTA" or "OCTA" => 8,
            _ when tag.Length == 4 && tag.EndsWith("CH", StringComparison.Ordinal) && int.TryParse(tag.AsSpan(0, 2), out int n) => n,
            _ => 0,
        };

        return channels > 0
            ? $"Unsupported MOD: {channels}-channel module (tag '{tag}'). Mod2Xgm converts 4-channel MODs."
            : $"Not a supported MOD file: no 4-channel ProTracker tag at byte offset {TagOffset} (found '{Printable(tag)}').";
    }

    private static ConversionException Invalid(int offset, string reason) =>
        new(ExitCode.InvalidInput, $"Not a valid MOD file: {reason} (byte offset {offset}).");

    /// <summary>Latin-1 text up to the first NUL, trailing spaces removed (leading spaces are meaningful).</summary>
    private static string Name(ReadOnlySpan<byte> bytes)
    {
        int nul = bytes.IndexOf((byte)0);
        return Latin1(nul < 0 ? bytes : bytes[..nul]).TrimEnd(' ');
    }

    private static string Latin1(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);

    private static string Printable(string text) =>
        string.Concat(text.Select(c => char.IsControl(c) ? '·' : c));
}
