using System.Text;
using Mod2Xgm.Mod;
using Serilog.Core;

namespace Mod2Xgm.Tests.Mod;

/// <summary>Tests for <see cref="ModReader"/> on 31-sample files: every header, sample and pattern field, damaged files and rejections.</summary>
public sealed class ModReaderTests
{
    /// <summary>Each four-channel tag is read as a ProTracker 31-sample module.</summary>
    [Theory]
    [InlineData("M.K.")]
    [InlineData("M!K!")]
    [InlineData("FLT4")]
    [InlineData("4CHN")]
    public void Read_WithFourChannelTag_ReadsProTracker31Module(string tag)
    {
        // Arrange
        byte[] bytes = new ModBuilder { Tag = tag }.WithSample(1, "kick", 64).Build();

        // Act
        ModModule module = Read(bytes);

        // Assert
        Assert.Equal(ModFormat.ProTracker31, module.Format);
        Assert.Equal(tag, module.Tag);
        Assert.Equal(4, module.ChannelCount);
    }

    /// <summary>The title, song length, restart position, order table and pattern count are read.</summary>
    [Fact]
    public void Read_WithHeaderFields_ReadsEachField()
    {
        // Arrange
        byte[] bytes = new ModBuilder { Title = "space debris", RestartPosition = 1 }
            .WithOrders(0, 2, 1, 2)
            .WithSample(1, "kick", 64)
            .Build();

        // Act
        ModModule module = Read(bytes);

        // Assert
        Assert.Equal("space debris", module.Title);
        Assert.Equal(4, module.SongLength);
        Assert.Equal(1, module.RestartPosition);
        int[] orders = [0, 2, 1, 2];
        Assert.Equal(orders, module.Orders);
        Assert.Equal(128, module.OrderTable.Count);
        Assert.Equal(3, module.Patterns.Count);
        Assert.Equal(bytes.Length, module.FileSize);
    }

    /// <summary>All 31 sample slots are returned, numbered from 1, empty ones (which often hold credits) included.</summary>
    [Fact]
    public void Read_WithEmptySlots_ReturnsAll31SlotsIncludingEmpty()
    {
        // Arrange
        byte[] bytes = new ModBuilder().WithSample(1, "kick", 64).WithSample(31, "  by Jester", 0).Build();

        // Act
        ModModule module = Read(bytes);

        // Assert
        Assert.Equal(Enumerable.Range(1, 31), module.Samples.Select(s => s.Number));
        Assert.True(module.Samples[1].IsEmpty);
        Assert.Equal("  by Jester", module.Samples[30].Name);
    }

    /// <summary>Every field of a sample header is read.</summary>
    [Fact]
    public void Read_WithSampleHeader_ReadsEachField()
    {
        // Arrange
        byte[] bytes = new ModBuilder().WithSample(3, "bass", 1000, fineTune: -3, volume: 48, loopStart: 200, loopLength: 600).Build();

        // Act
        ModModule module = Read(bytes);

        // Assert
        ModSample sample = module.Samples[2];
        Assert.Equal(3, sample.Number);
        Assert.Equal("bass", sample.Name);
        Assert.Equal(1000, sample.Length);
        Assert.Equal(-3, sample.FineTune);
        Assert.Equal(48, sample.Volume);
        Assert.Equal(200, sample.LoopStart);
        Assert.Equal(600, sample.LoopLength);
        Assert.True(sample.Loops);
    }

    /// <summary>The finetune nibble is signed: 8–15 are −8 to −1.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(7, 7)]
    [InlineData(-8, -8)]
    [InlineData(-1, -1)]
    public void Read_WithFinetune_ReadsItSigned(int stored, int expected)
    {
        // Arrange
        byte[] bytes = new ModBuilder().WithSample(1, "s", 64, fineTune: stored).Build();

        // Act
        ModModule module = Read(bytes);

        // Assert
        Assert.Equal(expected, module.Samples[0].FineTune);
    }

    /// <summary>A loop of 0 or 1 word means no loop, and start and length are cleared.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Read_WithLoopOfOneWordOrLess_ReadsNoLoop(int loopLength)
    {
        // Arrange
        byte[] bytes = new ModBuilder().WithSample(1, "s", 64, loopStart: 10, loopLength: loopLength).Build();

        // Act
        ModModule module = Read(bytes);

        // Assert
        Assert.False(module.Samples[0].Loops);
        Assert.Equal(0, module.Samples[0].LoopStart);
        Assert.Equal(0, module.Samples[0].LoopLength);
    }

    /// <summary>Sample data is signed 8-bit, each sample's bytes in file order.</summary>
    [Fact]
    public void Read_WithTwoSamples_ReadsSignedDataInOrder()
    {
        // Arrange
        byte[] bytes = new ModBuilder()
            .WithSample(1, "a", 4, data: i => (sbyte)(-1 - i))
            .WithSample(2, "b", 4, data: i => (sbyte)(100 + i))
            .Build();

        // Act
        ModModule module = Read(bytes);

        // Assert
        Assert.Equal(new sbyte[] { -1, -2, -3, -4 }, module.Samples[0].Data);
        Assert.Equal(new sbyte[] { 100, 101, 102, 103 }, module.Samples[1].Data);
    }

    /// <summary>Each sample's data offset points at its bytes in the file: right after the patterns, one after another.</summary>
    [Fact]
    public void Read_WithTwoSamples_DataOffsetsPointIntoFile()
    {
        // Arrange
        byte[] bytes = new ModBuilder()
            .WithSample(1, "a", 4, data: i => (sbyte)(-1 - i))
            .WithSample(2, "b", 4, data: i => (sbyte)(100 + i))
            .Build();

        // Act
        ModModule module = Read(bytes);

        // Assert
        Assert.Equal(1084 + 1024, module.Samples[0].DataOffset);
        Assert.Equal(module.Samples[0].DataOffset + 4, module.Samples[1].DataOffset);
        Assert.Equal(100, (sbyte)bytes[module.Samples[1].DataOffset]);
    }

    /// <summary>
    /// A sample 1 word long is read as OpenMPT reads it: empty, without its 2 bytes, so the next sample starts on them
    /// and every later sample starts 2 bytes early (docs/adr/0062-one-word-samples.md).
    /// </summary>
    [Fact]
    public void Read_WithOneWordSample_ReadsItEmptyAndShiftsLaterSamples()
    {
        // Arrange
        byte[] bytes = OneWordSampleSong();

        // Act
        ModModule module = Read(bytes);

        // Assert
        Assert.True(module.Samples[1].IsEmpty);
        Assert.Equal("credits", module.Samples[1].Name);
        Assert.Equal(new sbyte[] { 90, 91, 20, 21 }, module.Samples[2].Data);
        Assert.Equal(new sbyte[] { 22, 23, 30, 31 }, module.Samples[3].Data);
        Assert.Equal(module.Samples[0].DataOffset + 4, module.Samples[2].DataOffset);
    }

    /// <summary>A 1-word sample is noted, with the bytes left unread at the end, and not as extra data.</summary>
    [Fact]
    public void Read_WithOneWordSample_NotesShiftAndUnreadBytes()
    {
        // Arrange
        ConversionNotes notes = new(Logger.None);

        // Act
        ModReader.Read(OneWordSampleSong(), notes);

        // Assert
        Assert.Contains(notes.Messages, m => m.Contains("Samples 2 are 1 word (2 bytes) long", StringComparison.Ordinal));
        Assert.Contains(notes.Messages, m => m.Contains("the file's last 2 bytes go unread", StringComparison.Ordinal));
        Assert.DoesNotContain(notes.Messages, m => m.Contains("after the last sample's data", StringComparison.Ordinal));
    }

    /// <summary>Each 1-word sample moves the later samples 2 more bytes.</summary>
    [Fact]
    public void Read_WithTwoOneWordSamples_ShiftsLaterSampleFourBytes()
    {
        // Arrange
        byte[] bytes = TwoOneWordSamplesSong();

        // Act
        ModModule module = Read(bytes);

        // Assert
        Assert.Equal(new sbyte[] { 0, 0, 0, 0, 20, 21, 22, 23 }, module.Samples[2].Data);
    }

    /// <summary>The note lists every 1-word sample and the largest shift.</summary>
    [Fact]
    public void Read_WithTwoOneWordSamples_NoteListsBoth()
    {
        // Arrange
        ConversionNotes notes = new(Logger.None);

        // Act
        ModReader.Read(TwoOneWordSamplesSong(), notes);

        // Assert
        Assert.Contains(notes.Messages, m => m.Contains("Samples 1, 2 are 1 word", StringComparison.Ordinal) && m.Contains("up to 4 bytes early", StringComparison.Ordinal));
    }

    /// <summary>Without 1-word samples nothing moves and no note is added.</summary>
    [Fact]
    public void Read_WithoutOneWordSamples_AddsNoOneWordNote()
    {
        // Arrange
        ConversionNotes notes = new(Logger.None);
        byte[] bytes = new ModBuilder().WithSample(1, "a", 4).WithSample(2, "b", 0).Build();

        // Act
        ModReader.Read(bytes, notes);

        // Assert
        Assert.DoesNotContain(notes.Messages, m => m.Contains("1 word", StringComparison.Ordinal));
    }

    /// <summary>A pattern cell's sample, period, effect and parameter are decoded; untouched cells are empty.</summary>
    [Fact]
    public void Read_WithPatternCell_DecodesEachField()
    {
        // Arrange
        byte[] bytes = new ModBuilder()
            .WithCell(0, row: 5, channel: 2, sample: 0x1F, period: 0x1AC, effect: 0xC, parameter: 0x40)
            .WithSample(1, "s", 64)
            .Build();

        // Act
        ModModule module = Read(bytes);

        // Assert
        ModCell cell = module.Patterns[0][5, 2];
        Assert.Equal(0x1F, cell.Sample);
        Assert.Equal(0x1AC, cell.Period);
        Assert.Equal(0xC, cell.Effect);
        Assert.Equal(0x40, cell.EffectParameter);
        Assert.Equal(default(ModCell), module.Patterns[0][0, 0]);
        Assert.Equal(4, module.Patterns[0].ChannelCount);
    }

    /// <summary>Names are Latin-1, with trailing padding removed.</summary>
    [Fact]
    public void Read_WithLatin1NamesAndPadding_DecodesAndTrims()
    {
        // Arrange
        byte[] raw = new byte[22];
        Encoding.Latin1.GetBytes("café  ").CopyTo(raw, 0); // é is one byte (0xE9) in Latin-1
        byte[] bytes = new ModBuilder { Title = "title   " }.WithSample(1, "", 64, rawName: raw).Build();

        // Act
        ModModule module = Read(bytes);

        // Assert
        Assert.Equal("café", module.Samples[0].Name);
        Assert.Equal("title", module.Title);
    }

    /// <summary>An unused order entry counts as a pattern when the file is long enough to hold it.</summary>
    [Fact]
    public void Read_WithUnusedOrderEntryInFile_CountsItsPattern()
    {
        // Arrange
        byte[] bytes = new ModBuilder { UnusedOrderEntries = [3] }.WithOrders(0, 1).WithSample(1, "s", 64).Build();

        // Act
        ModModule module = Read(bytes);

        // Assert
        Assert.Equal(4, module.Patterns.Count);
    }

    /// <summary>A garbage unused order entry is ignored when the file doesn't hold that many patterns.</summary>
    [Fact]
    public void Read_WithGarbageUnusedOrderEntry_IgnoresIt()
    {
        // Arrange: the builder writes patterns up to the highest entry; cut the file back to 2 patterns + sample data.
        ModBuilder builder = new ModBuilder().WithOrders(0, 1).WithSample(1, "s", 64);
        byte[] honest = builder.Build();
        builder.UnusedOrderEntries = [90];
        byte[] header = builder.Build()[..1084];
        byte[] bytes = [.. header, .. honest[1084..]];

        // Act
        ModModule module = Read(bytes);

        // Assert
        Assert.Equal(2, module.Patterns.Count);
        Assert.Equal(64, module.Samples[0].Length);
    }

    /// <summary>Sample data cut short by the end of the file is kept as far as it goes, with a note.</summary>
    [Fact]
    public void Read_WithTruncatedSampleData_KeepsWhatThereIsAndNotes()
    {
        // Arrange
        byte[] full = new ModBuilder().WithSample(1, "kick", 64).WithSample(2, "snare", 100).Build();
        ConversionNotes notes = new(Logger.None);

        // Act
        ModModule module = ModReader.Read(full[..^40], notes);

        // Assert
        Assert.Equal(64, module.Samples[0].Length);
        Assert.Equal(60, module.Samples[1].Length);
        Assert.Contains(notes.Messages, m => m.Contains("kept 60 of 100 bytes", StringComparison.Ordinal));
    }

    /// <summary>A loop running past the sample's end is shortened to end there, with a note.</summary>
    [Fact]
    public void Read_WithLoopPastEnd_ShortensLoopAndNotes()
    {
        // Arrange
        byte[] bytes = new ModBuilder().WithSample(1, "s", 100, loopStart: 40, loopLength: 100).Build();
        ConversionNotes notes = new(Logger.None);

        // Act
        ModModule module = ModReader.Read(bytes, notes);

        // Assert
        Assert.Equal(40, module.Samples[0].LoopStart);
        Assert.Equal(60, module.Samples[0].LoopLength);
        Assert.Contains(notes.Messages, m => m.Contains("loop runs past the sample end", StringComparison.Ordinal));
    }

    /// <summary>A loop starting past the sample's end is removed, with a note.</summary>
    [Fact]
    public void Read_WithLoopStartingPastEnd_RemovesLoopAndNotes()
    {
        // Arrange
        byte[] bytes = new ModBuilder().WithSample(1, "s", 100, loopStart: 200, loopLength: 50).Build();
        ConversionNotes notes = new(Logger.None);

        // Act
        ModModule module = ModReader.Read(bytes, notes);

        // Assert
        Assert.False(module.Samples[0].Loops);
        Assert.Contains(notes.Messages, m => m.Contains("loop starts past the sample end", StringComparison.Ordinal));
    }

    /// <summary>Modules with more than four channels are rejected, saying how many channels they have.</summary>
    [Theory]
    [InlineData("6CHN", "6-channel")]
    [InlineData("8CHN", "8-channel")]
    [InlineData("FLT8", "8-channel")]
    [InlineData("CD81", "8-channel")]
    [InlineData("OKTA", "8-channel")]
    [InlineData("16CH", "16-channel")]
    public void Read_WithMultiChannelTag_ThrowsInvalidInputNamingChannels(string tag, string expected)
    {
        // Arrange
        byte[] bytes = new ModBuilder { Tag = tag }.Build();

        // Act
        ConversionException error = Rejected(bytes);

        // Assert
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    /// <summary>An unknown tag is rejected with its offset and its bytes, control characters shown.</summary>
    [Fact]
    public void Read_WithUnknownTag_ThrowsInvalidInputWithOffset()
    {
        // Arrange
        byte[] bytes = new ModBuilder { Tag = "AB\u0001D" }.Build();

        // Act
        ConversionException error = Rejected(bytes);

        // Assert
        Assert.Contains("1080", error.Message, StringComparison.Ordinal);
        Assert.Contains("AB·D", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A file too short to hold a header is rejected, saying its size.</summary>
    [Fact]
    public void Read_WithTooShortFile_ThrowsInvalidInputWithSize()
    {
        // Act
        ConversionException error = Rejected(new byte[500]);

        // Assert
        Assert.Contains("only 500 bytes", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A song length of 0 or above 128 is rejected.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(129)]
    public void Read_WithSongLengthOutOfRange_ThrowsInvalidInput(int songLength)
    {
        // Arrange
        byte[] bytes = new ModBuilder { SongLength = songLength }.Build();

        // Act
        ConversionException error = Rejected(bytes);

        // Assert
        Assert.Contains("song length", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A file that ends inside the pattern data is rejected.</summary>
    [Fact]
    public void Read_WithFileEndingInsidePatterns_ThrowsInvalidInput()
    {
        // Arrange
        byte[] bytes = new ModBuilder().WithOrders(0, 1, 2).Build()[..(1084 + 1500)];

        // Act
        ConversionException error = Rejected(bytes);

        // Assert
        Assert.Contains("pattern data", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The pattern count prefers the reading that fits the file size.</summary>
    [Theory]
    [InlineData(3, 3, 0, 0, 3)]       // unused entries within the played range: nothing to decide
    [InlineData(3, 3, 90, 0, 3)]      // garbage entry; file sized for the played patterns
    [InlineData(5, 3, 4, 0, 5)]       // extra pattern stored after the played ones
    [InlineData(3, 3, 90, 5000, 3)]   // same with sample data after the patterns
    public void PatternCount_WithFileSize_PrefersReadingThatFits(int filePatterns, int played, int unusedEntry, long sampleBytes, int expected)
    {
        // Arrange
        int[] table = new int[128];
        for (int i = 0; i < played; i++)
        {
            table[i] = i;
        }

        table[played] = unusedEntry;
        long fileLength = 1084 + (filePatterns * 1024L) + sampleBytes;

        // Act
        int count = ModReader.PatternCount(fileLength, table, played, sampleBytes);

        // Assert
        Assert.Equal(expected, count);
    }

    /// <summary>Reads <paramref name="bytes"/> with notes going nowhere.</summary>
    private static ModModule Read(byte[] bytes) => ModReader.Read(bytes, new ConversionNotes(Logger.None));

    /// <summary>The <see cref="ConversionException"/> reading <paramref name="bytes"/> throws, checked to be InvalidInput.</summary>
    private static ConversionException Rejected(byte[] bytes)
    {
        ConversionException error = Assert.Throws<ConversionException>(() => Read(bytes));
        Assert.Equal(ExitCode.InvalidInput, error.Code);
        return error;
    }

    /// <summary>Four samples, the second 1 word long.</summary>
    private static byte[] OneWordSampleSong() => new ModBuilder()
        .WithSample(1, "a", 4, data: i => (sbyte)(10 + i))
        .WithSample(2, "credits", 2, data: i => (sbyte)(90 + i))
        .WithSample(3, "b", 4, data: i => (sbyte)(20 + i))
        .WithSample(4, "c", 4, data: i => (sbyte)(30 + i))
        .Build();

    /// <summary>Two 1-word samples, then an 8-byte one.</summary>
    private static byte[] TwoOneWordSamplesSong() => new ModBuilder()
        .WithSample(1, "x", 2, data: _ => 0)
        .WithSample(2, "y", 2, data: _ => 0)
        .WithSample(3, "b", 8, data: i => (sbyte)(20 + i))
        .Build();
}
