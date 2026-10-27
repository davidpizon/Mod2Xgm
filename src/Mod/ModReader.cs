using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Mod2Xgm.Mod;

/// <summary>
/// Reads MOD files (docs/formats/mod.md). All multi-byte values are big-endian. Files that can't be read throw
/// <see cref="ConversionException"/> with <see cref="ExitCode.InvalidInput"/>, the reason and the byte offset.
/// Everything else is read as stored: recoverable damage (truncated sample data, loops past the sample end) is
/// repaired, and every assumption or sign of a damaged or unusual file is reported as a conversion note.
/// </summary>
internal static class ModReader
{
    public const int TitleLength = 20;
    public const int SampleHeaderSize = 30;
    public const int SampleNameLength = 22;
    public const int PatternSize = ModPattern.Rows * 4 * 4; // 64 rows × 4 channels × 4 bytes
    public const int MaxOrders = 128;

    // docs/formats/mod.md#sample-header: volumes run 0–64; trackers cap samples at 64 KB although the header
    // could store up to 128 KB.
    public const int MaxVolume = 64;
    public const int MaxSampleBytes = 65536;

    // docs/formats/mod.md#pitch: the lowest and highest periods any finetune produces (B-3 at +7, C-1 at −8).
    private const int MinPeriod = 108;
    private const int MaxPeriod = 907;

    private const int Channels = 4;
    private const int TagOffset = 1080; // docs/formats/mod.md#layout-31-sample

    private static readonly string[] FourChannelTags = ["M.K.", "M!K!", "FLT4", "4CHN"];

    // 31-sample, 4-channel variants written by other trackers; not supported yet (docs/formats/mod.md#variant-detection).
    private static readonly Dictionary<string, string> OtherFourChannelTags = new(StringComparer.Ordinal)
    {
        ["N.T."] = "NoiseTracker",
        ["M&K!"] = "His Master's Noise",
        ["FEST"] = "His Master's Noise",
        ["LARD"] = "an unknown tracker",
        ["NSMS"] = "an unknown tracker",
    };

    internal static readonly Layout ProTracker = new(ModFormat.ProTracker31, SampleCount: 31, SongLengthOffset: 950, PatternOffset: 1084);
    internal static readonly Layout Soundtracker = new(ModFormat.Soundtracker15, SampleCount: 15, SongLengthOffset: 470, PatternOffset: 600);

    public static ModModule Read(ReadOnlySpan<byte> data, ConversionNotes notes)
    {
        string? tag = data.Length >= TagOffset + 4 ? Latin1(data.Slice(TagOffset, 4)) : null;
        if (tag is not null)
        {
            if (Array.IndexOf(FourChannelTags, tag) >= 0)
            {
                return ReadModule(data, ProTracker, tag, notes);
            }

            if (UnsupportedTagMessage(tag) is { } unsupported)
            {
                throw new ConversionException(ExitCode.InvalidInput, unsupported);
            }
        }

        // No tag: the only 4-channel layout left is the original 15-sample Soundtracker. Any file can look like one,
        // so the hard checks are only what's needed to read it; everything doubtful becomes a conversion note.
        if (SoundtrackerProblem(data) is { } problem)
        {
            string found = tag is null
                ? $"the file is too short for a ProTracker tag at byte offset {TagOffset}"
                : $"no 4-channel ProTracker tag at byte offset {TagOffset} (found '{Printable(tag)}')";
            throw new ConversionException(
                ExitCode.InvalidInput,
                $"Not a supported MOD file: {found}, and it doesn't read as a 15-sample Soundtracker module either: {problem}.");
        }

        // A readable 15-sample file holds at least one pattern, so it always reaches the tag offset.
        notes.Add(
            "No ProTracker tag at byte offset {Offset} (found '{Found}'); reading the file as a 15-sample Soundtracker module",
            TagOffset,
            Printable(tag ?? string.Empty));
        return ReadModule(data, Soundtracker, string.Empty, notes);
    }

    /// <summary>The reason <paramref name="data"/> can't be read as a 15-sample module, or null if it can.</summary>
    internal static string? SoundtrackerProblem(ReadOnlySpan<byte> data)
    {
        Layout layout = Soundtracker;
        if (data.Length < layout.PatternOffset)
        {
            return $"the file is only {data.Length} bytes; a 15-sample header needs {layout.PatternOffset} (byte offset {data.Length})";
        }

        int songLength = data[layout.SongLengthOffset];
        if (songLength is < 1 or > MaxOrders)
        {
            return $"song length {songLength} is outside 1–{MaxOrders} (byte offset {layout.SongLengthOffset})";
        }

        for (int i = 0; i < songLength; i++)
        {
            int pattern = data[layout.OrderTableOffset + i];
            if (pattern >= MaxOrders)
            {
                return $"order {i} plays pattern {pattern}; there can be at most {MaxOrders} patterns (byte offset {layout.OrderTableOffset + i})";
            }
        }

        int[] orderTable = OrderTable(data, layout);
        int patternCount = PatternCount(data.Length, orderTable, songLength, SampleBytes(data, layout), layout.PatternOffset);
        int patternEnd = layout.PatternOffset + (patternCount * PatternSize);
        return data.Length < patternEnd
            ? $"the file ends inside the pattern data ({patternCount} patterns need {patternEnd} bytes) (byte offset {data.Length})"
            : null;
    }

    private static ModModule ReadModule(ReadOnlySpan<byte> data, Layout layout, string tag, ConversionNotes notes)
    {
        int songLength = data[layout.SongLengthOffset];
        if (songLength is < 1 or > MaxOrders)
        {
            throw Invalid(layout.SongLengthOffset, $"song length {songLength} is outside 1–{MaxOrders}");
        }

        int restart = data[layout.SongLengthOffset + 1];
        int[] orderTable = OrderTable(data, layout);

        if (HasUnprintable(data[..TitleLength]))
        {
            notes.Add("The song title contains unprintable bytes; the header may be damaged");
        }

        SampleHeader[] headers = new SampleHeader[layout.SampleCount];
        for (int i = 0; i < layout.SampleCount; i++)
        {
            headers[i] = ReadSampleHeader(data, TitleLength + (i * SampleHeaderSize), i + 1);
            NoteHeaderHints(headers[i], layout, notes);
        }

        long sampleBytes = headers.Sum(h => (long)h.Length);
        int patternCount = PatternCount(data.Length, orderTable, songLength, sampleBytes, layout.PatternOffset);
        if (patternCount < orderTable.Max() + 1)
        {
            notes.Add(
                "Order-table entries after the song's end point at patterns the file doesn't hold (up to pattern {Pattern}); ignored",
                orderTable.Max());
        }

        int sampleDataOffset = layout.PatternOffset + (patternCount * PatternSize);
        if (data.Length < sampleDataOffset)
        {
            throw Invalid(data.Length, $"the file ends inside the pattern data ({patternCount} patterns need {sampleDataOffset} bytes)");
        }

        List<ModPattern> patterns = [];
        for (int p = 0; p < patternCount; p++)
        {
            patterns.Add(ReadPattern(data.Slice(layout.PatternOffset + (p * PatternSize), PatternSize)));
        }

        NotePatternHints(patterns, layout, notes);

        List<ModSample> samples = [];
        int offset = sampleDataOffset;
        int ambiguousLoops = 0;
        foreach (SampleHeader header in headers)
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

            int loopStart = LoopStart(header, layout, notes, ref ambiguousLoops);
            (loopStart, int loopLength) = ClampLoop(header.Number, header.Name, loopStart, header.LoopLength, available, notes);
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

        if (ambiguousLoops > 0)
        {
            notes.Add(
                "{Count} looping samples have a loop start that fits whether it's stored in words or in bytes; read as words (Ultimate Soundtracker stored bytes; checked during playback)",
                ambiguousLoops);
        }

        long expectedEnd = sampleDataOffset + sampleBytes;
        if (data.Length > expectedEnd)
        {
            notes.Add("{Extra} bytes after the last sample's data; ignored", data.Length - expectedEnd);
        }

        if (layout.Format == ModFormat.Soundtracker15)
        {
            notes.Add(
                "Byte {Offset} holds {Value}: Ultimate Soundtracker reads it as the tempo, later Soundtrackers as the restart position; kept as stored and interpreted during playback",
                layout.SongLengthOffset + 1,
                restart);
        }

        return new ModModule
        {
            Title = Name(data[..TitleLength]),
            Format = layout.Format,
            Tag = tag,
            ChannelCount = Channels,
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
    internal static int PatternCount(long fileLength, IReadOnlyList<int> orderTable, int songLength, long sampleBytes, int patternOffset = 1084)
    {
        int all = orderTable.Max() + 1;
        int played = orderTable.Take(songLength).Max() + 1;
        if (all == played)
        {
            return all;
        }

        long Distance(int patterns) => Math.Abs(fileLength - (patternOffset + ((long)patterns * PatternSize) + sampleBytes));
        return Distance(all) <= Distance(played) ? all : played;
    }

    private static int[] OrderTable(ReadOnlySpan<byte> data, Layout layout)
    {
        int[] table = new int[MaxOrders];
        ReadOnlySpan<byte> bytes = data.Slice(layout.OrderTableOffset, MaxOrders);
        for (int i = 0; i < MaxOrders; i++)
        {
            table[i] = bytes[i];
        }

        return table;
    }

    private static long SampleBytes(ReadOnlySpan<byte> data, Layout layout)
    {
        long total = 0;
        for (int i = 0; i < layout.SampleCount; i++)
        {
            total += ReadSampleHeader(data, TitleLength + (i * SampleHeaderSize), i + 1).Length;
        }

        return total;
    }

    private static SampleHeader ReadSampleHeader(ReadOnlySpan<byte> data, int offset, int number)
    {
        ReadOnlySpan<byte> header = data.Slice(offset, SampleHeaderSize);
        return new SampleHeader(
            number,
            Name(header[..SampleNameLength]),
            HasUnprintable(header[..SampleNameLength]),
            BinaryPrimitives.ReadUInt16BigEndian(header[22..]) * 2,
            header[24],
            header[25],
            BinaryPrimitives.ReadUInt16BigEndian(header[26..]),
            BinaryPrimitives.ReadUInt16BigEndian(header[28..]) * 2);
    }

    /// <summary>Signs of a damaged or unusual sample header. Nothing is changed; each sign is a conversion note.</summary>
    private static void NoteHeaderHints(SampleHeader header, Layout layout, ConversionNotes notes)
    {
        if (header.NameHasUnprintable)
        {
            notes.Add("Sample {Sample} {Name}: the name contains unprintable bytes; the header may be damaged", header.Number, header.Name);
        }

        if (header.Volume > MaxVolume)
        {
            notes.Add("Sample {Sample} {Name}: volume {Volume} is above the maximum of {Max}; players cap it there", header.Number, header.Name, header.Volume, MaxVolume);
        }

        if (header.Length > MaxSampleBytes)
        {
            notes.Add(
                "Sample {Sample} {Name}: length {Length} bytes is over the {Max} bytes trackers allow; the header may be damaged",
                header.Number,
                header.Name,
                header.Length,
                MaxSampleBytes);
        }

        if ((header.FineTuneByte & 0xF0) != 0)
        {
            notes.Add(
                "Sample {Sample} {Name}: finetune byte 0x{Byte} has its unused high bits set; only the low 4 bits are used",
                header.Number,
                header.Name,
                header.FineTuneByte.ToString("X2", CultureInfo.InvariantCulture));
        }

        if (layout.Format == ModFormat.Soundtracker15 && header.FineTune != 0)
        {
            notes.Add(
                "Sample {Sample} {Name}: finetune {FineTune} in a 15-sample file; Soundtracker had no finetune; kept as stored",
                header.Number,
                header.Name,
                header.FineTune);
        }
    }

    /// <summary>Counts pattern cells that no tracker would write: unknown sample numbers and periods out of range.</summary>
    private static void NotePatternHints(List<ModPattern> patterns, Layout layout, ConversionNotes notes)
    {
        int badSamples = 0;
        int badPeriods = 0;
        string? firstBadSample = null;
        string? firstBadPeriod = null;
        for (int p = 0; p < patterns.Count; p++)
        {
            for (int row = 0; row < ModPattern.Rows; row++)
            {
                for (int channel = 0; channel < Channels; channel++)
                {
                    ModCell cell = patterns[p][row, channel];
                    if (cell.Sample > layout.SampleCount)
                    {
                        badSamples++;
                        firstBadSample ??= Location(p, row, channel);
                    }

                    if (cell.Period != 0 && cell.Period is < MinPeriod or > MaxPeriod)
                    {
                        badPeriods++;
                        firstBadPeriod ??= Location(p, row, channel);
                    }
                }
            }
        }

        if (badSamples > 0)
        {
            notes.Add(
                "{Count} pattern cells use a sample number above {Max} (first at {Location}); the pattern data may be damaged",
                badSamples,
                layout.SampleCount,
                firstBadSample);
        }

        if (badPeriods > 0)
        {
            notes.Add(
                "{Count} pattern cells hold a period outside {Min}–{Max} (first at {Location}); the pattern data may be damaged",
                badPeriods,
                MinPeriod,
                MaxPeriod,
                firstBadPeriod);
        }
    }

    private static string Location(int pattern, int row, int channel) =>
        string.Create(CultureInfo.InvariantCulture, $"pattern {pattern} row {row} channel {channel + 1}");

    /// <summary>
    /// Loop start in bytes. Files store words, but some early Soundtracker files stored bytes
    /// (docs/formats/mod.md#sample-header). In a 15-sample file, a loop that only fits inside the sample when its
    /// start is read in bytes is read that way; one that fits either way is read as words and counted.
    /// </summary>
    private static int LoopStart(SampleHeader header, Layout layout, ConversionNotes notes, ref int ambiguous)
    {
        int asWords = header.LoopStartWords * 2;
        if (layout.Format != ModFormat.Soundtracker15 || header.LoopLength <= 2 || header.LoopStartWords == 0)
        {
            return asWords;
        }

        bool wordsFit = asWords + header.LoopLength <= header.Length;
        bool bytesFit = header.LoopStartWords + header.LoopLength <= header.Length;
        if (!wordsFit && bytesFit)
        {
            notes.Add(
                "Sample {Sample} {Name}: the loop only fits when its start is read in bytes, as early Soundtrackers stored it; loop starts at byte {Start}",
                header.Number,
                header.Name,
                header.LoopStartWords);
            return header.LoopStartWords;
        }

        if (wordsFit)
        {
            ambiguous++;
        }

        return asWords;
    }

    private static ModPattern ReadPattern(ReadOnlySpan<byte> bytes)
    {
        ModCell[] cells = new ModCell[ModPattern.Rows * Channels];
        for (int i = 0; i < cells.Length; i++)
        {
            ReadOnlySpan<byte> c = bytes.Slice(i * 4, 4);
            cells[i] = new ModCell(
                Sample: (c[0] & 0xF0) | (c[2] >> 4),
                Period: ((c[0] & 0x0F) << 8) | c[1],
                Effect: c[2] & 0x0F,
                EffectParameter: c[3]);
        }

        return new ModPattern(Channels, cells);
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

    /// <summary>The rejection message for a recognised but unsupported tag, or null for an unknown one.</summary>
    private static string? UnsupportedTagMessage(string tag)
    {
        if (OtherFourChannelTags.TryGetValue(tag, out string? tracker))
        {
            return $"Unsupported MOD variant: tag '{tag}' at byte offset {TagOffset} ({tracker}, 31 samples). Mod2Xgm doesn't read this variant yet.";
        }

        int channels = tag switch
        {
            "6CHN" => 6,
            "8CHN" or "FLT8" or "CD81" or "OKTA" or "OCTA" => 8,
            _ when tag.Length == 4 && tag.EndsWith("CH", StringComparison.Ordinal) && int.TryParse(tag.AsSpan(0, 2), out int n) => n,
            _ => 0,
        };

        return channels > 0
            ? $"Unsupported MOD: {channels}-channel module (tag '{tag}' at byte offset {TagOffset}). Mod2Xgm converts 4-channel MODs."
            : null;
    }

    private static ConversionException Invalid(int offset, string reason) =>
        new(ExitCode.InvalidInput, $"Not a valid MOD file: {reason} (byte offset {offset}).");

    /// <summary>Latin-1 text up to the first NUL, trailing spaces removed (leading spaces are meaningful).</summary>
    private static string Name(ReadOnlySpan<byte> bytes)
    {
        int nul = bytes.IndexOf((byte)0);
        return Latin1(nul < 0 ? bytes : bytes[..nul]).TrimEnd(' ');
    }

    /// <summary>True if the text before the first NUL holds control characters (C0, DEL or C1).</summary>
    private static bool HasUnprintable(ReadOnlySpan<byte> bytes)
    {
        int nul = bytes.IndexOf((byte)0);
        foreach (byte b in nul < 0 ? bytes : bytes[..nul])
        {
            if (b < 0x20 || b is >= 0x7F and < 0xA0)
            {
                return true;
            }
        }

        return false;
    }

    private static string Latin1(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);

    private static string Printable(string text) =>
        string.Concat(text.Select(c => char.IsControl(c) ? '·' : c));

    /// <summary>Where the fields of one MOD layout sit (docs/formats/mod.md#layout-31-sample).</summary>
    internal sealed record Layout(ModFormat Format, int SampleCount, int SongLengthOffset, int PatternOffset)
    {
        public int OrderTableOffset => SongLengthOffset + 2;
    }

    /// <summary>One sample header as stored. <see cref="LoopStartWords"/> is raw; lengths are in bytes.</summary>
    private readonly record struct SampleHeader(
        int Number,
        string Name,
        bool NameHasUnprintable,
        int Length,
        int FineTuneByte,
        int Volume,
        int LoopStartWords,
        int LoopLength)
    {
        /// <summary>Signed finetune, −8 to +7, from the low nibble.</summary>
        public int FineTune => (FineTuneByte & 0x0F) > 7 ? (FineTuneByte & 0x0F) - 16 : FineTuneByte & 0x0F;
    }
}
