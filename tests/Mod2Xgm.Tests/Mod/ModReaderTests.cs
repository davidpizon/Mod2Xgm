using System.Text;
using Mod2Xgm.Mod;
using Serilog.Core;

namespace Mod2Xgm.Tests.Mod;

public sealed class ModReaderTests
{
    private static ModModule Read(byte[] bytes) => ModReader.Read(bytes, new ConversionNotes(Logger.None));

    private static (ModModule Module, ConversionNotes Notes) ReadWithNotes(byte[] bytes)
    {
        ConversionNotes notes = new(Logger.None);
        return (ModReader.Read(bytes, notes), notes);
    }

    private static ConversionException Rejects(byte[] bytes)
    {
        ConversionException error = Assert.Throws<ConversionException>(() => Read(bytes));
        Assert.Equal(ExitCode.InvalidInput, error.Code);
        return error;
    }

    [Theory]
    [InlineData("M.K.")]
    [InlineData("M!K!")]
    [InlineData("FLT4")]
    [InlineData("4CHN")]
    public void FourChannelTagsAreRead(string tag)
    {
        ModModule module = Read(new ModBuilder { Tag = tag }.WithSample(1, "kick", 64).Build());

        Assert.Equal(ModFormat.ProTracker31, module.Format);
        Assert.Equal(tag, module.Tag);
        Assert.Equal(4, module.ChannelCount);
    }

    [Fact]
    public void HeaderFieldsAreRead()
    {
        byte[] bytes = new ModBuilder { Title = "space debris", RestartPosition = 1 }
            .WithOrders(0, 2, 1, 2)
            .WithSample(1, "kick", 64)
            .Build();

        ModModule module = Read(bytes);

        Assert.Equal("space debris", module.Title);
        Assert.Equal(4, module.SongLength);
        Assert.Equal(1, module.RestartPosition);
        int[] orders = [0, 2, 1, 2];
        Assert.Equal(orders, module.Orders);
        Assert.Equal(128, module.OrderTable.Count);
        Assert.Equal(3, module.Patterns.Count);
        Assert.Equal(bytes.Length, module.FileSize);
    }

    [Fact]
    public void AllSampleSlotsAreReturnedIncludingEmptyOnes()
    {
        ModModule module = Read(new ModBuilder().WithSample(1, "kick", 64).WithSample(31, "  by Jester", 0).Build());

        Assert.Equal(31, module.Samples.Count);
        Assert.Equal(Enumerable.Range(1, 31), module.Samples.Select(s => s.Number));
        Assert.True(module.Samples[1].IsEmpty);
        Assert.Equal("  by Jester", module.Samples[30].Name);
    }

    [Fact]
    public void SampleHeaderFieldsAreRead()
    {
        ModModule module = Read(new ModBuilder()
            .WithSample(3, "bass", 1000, fineTune: -3, volume: 48, loopStart: 200, loopLength: 600)
            .Build());

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

    [Theory]
    [InlineData(0, 0)]
    [InlineData(7, 7)]
    [InlineData(-8, -8)]
    [InlineData(-1, -1)]
    public void FinetuneIsSigned(int stored, int expected)
    {
        ModModule module = Read(new ModBuilder().WithSample(1, "s", 64, fineTune: stored).Build());
        Assert.Equal(expected, module.Samples[0].FineTune);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ShortLoopsMeanNoLoop(int loopLength)
    {
        ModModule module = Read(new ModBuilder().WithSample(1, "s", 64, loopStart: 10, loopLength: loopLength).Build());

        Assert.False(module.Samples[0].Loops);
        Assert.Equal(0, module.Samples[0].LoopStart);
        Assert.Equal(0, module.Samples[0].LoopLength);
    }

    [Fact]
    public void SampleDataIsSignedAndInOrder()
    {
        ModModule module = Read(new ModBuilder()
            .WithSample(1, "a", 4, data: i => (sbyte)(-1 - i))
            .WithSample(2, "b", 2, data: i => (sbyte)(100 + i))
            .Build());

        Assert.Equal(new sbyte[] { -1, -2, -3, -4 }, module.Samples[0].Data);
        Assert.Equal(new sbyte[] { 100, 101 }, module.Samples[1].Data);
    }

    [Fact]
    public void PatternCellsAreDecoded()
    {
        ModModule module = Read(new ModBuilder()
            .WithCell(0, row: 5, channel: 2, sample: 0x1F, period: 0x1AC, effect: 0xC, parameter: 0x40)
            .WithSample(1, "s", 64)
            .Build());

        ModCell cell = module.Patterns[0][5, 2];
        Assert.Equal(0x1F, cell.Sample);
        Assert.Equal(0x1AC, cell.Period);
        Assert.Equal(0xC, cell.Effect);
        Assert.Equal(0x40, cell.EffectParameter);
        Assert.Equal(default(ModCell), module.Patterns[0][0, 0]);
        Assert.Equal(4, module.Patterns[0].ChannelCount);
    }

    [Fact]
    public void NamesAreLatin1WithTrailingPaddingRemoved()
    {
        byte[] raw = new byte[22];
        Encoding.Latin1.GetBytes("caf\u00e9  ").CopyTo(raw, 0); // é is one byte (0xE9) in Latin-1
        ModModule module = Read(new ModBuilder { Title = "title   " }.WithSample(1, "", 64, rawName: raw).Build());

        Assert.Equal("café", module.Samples[0].Name);
        Assert.Equal("title", module.Title);
    }

    [Fact]
    public void UnusedOrderEntriesCountWhenTheFileHoldsThosePatterns()
    {
        ModModule module = Read(new ModBuilder { UnusedOrderEntries = [3] }.WithOrders(0, 1).WithSample(1, "s", 64).Build());

        Assert.Equal(4, module.Patterns.Count);
    }

    [Fact]
    public void GarbageUnusedOrderEntriesAreIgnoredWhenTheFileDoesNotHoldThem()
    {
        // The builder writes patterns up to the highest entry; cut the file back to 2 patterns + sample data.
        ModBuilder builder = new ModBuilder().WithOrders(0, 1).WithSample(1, "s", 64);
        byte[] honest = builder.Build();
        builder.UnusedOrderEntries = [90];
        byte[] header = builder.Build()[..1084];
        byte[] bytes = [.. header, .. honest[1084..]];

        ModModule module = Read(bytes);

        Assert.Equal(2, module.Patterns.Count);
        Assert.Equal(64, module.Samples[0].Length);
    }

    [Fact]
    public void TruncatedSampleDataIsKeptAndNoted()
    {
        byte[] full = new ModBuilder().WithSample(1, "kick", 64).WithSample(2, "snare", 100).Build();
        (ModModule module, ConversionNotes notes) = ReadWithNotes(full[..^40]);

        Assert.Equal(64, module.Samples[0].Length);
        Assert.Equal(60, module.Samples[1].Length);
        Assert.Contains(notes.Messages, m => m.Contains("kept 60 of 100 bytes", StringComparison.Ordinal));
    }

    [Fact]
    public void LoopPastTheEndIsShortenedAndNoted()
    {
        byte[] bytes = new ModBuilder().WithSample(1, "s", 100, loopStart: 40, loopLength: 100).Build();
        (ModModule module, ConversionNotes notes) = ReadWithNotes(bytes);

        Assert.Equal(40, module.Samples[0].LoopStart);
        Assert.Equal(60, module.Samples[0].LoopLength);
        Assert.Contains(notes.Messages, m => m.Contains("loop runs past the sample end", StringComparison.Ordinal));
    }

    [Fact]
    public void LoopStartingPastTheEndIsRemovedAndNoted()
    {
        byte[] bytes = new ModBuilder().WithSample(1, "s", 100, loopStart: 200, loopLength: 50).Build();
        (ModModule module, ConversionNotes notes) = ReadWithNotes(bytes);

        Assert.False(module.Samples[0].Loops);
        Assert.Contains(notes.Messages, m => m.Contains("loop starts past the sample end", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("6CHN", "6-channel")]
    [InlineData("8CHN", "8-channel")]
    [InlineData("FLT8", "8-channel")]
    [InlineData("CD81", "8-channel")]
    [InlineData("OKTA", "8-channel")]
    [InlineData("16CH", "16-channel")]
    public void MultiChannelModsAreRejected(string tag, string expected)
    {
        ConversionException error = Rejects(new ModBuilder { Tag = tag }.Build());
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownTagsAreRejectedWithTheOffset()
    {
        ConversionException error = Rejects(new ModBuilder { Tag = "AB\u0001D" }.Build());

        Assert.Contains("1080", error.Message, StringComparison.Ordinal);
        Assert.Contains("AB·D", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TooShortFilesAreRejected()
    {
        ConversionException error = Rejects(new byte[500]);
        Assert.Contains("only 500 bytes", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(129)]
    public void SongLengthOutOfRangeIsRejected(int songLength)
    {
        ConversionException error = Rejects(new ModBuilder { SongLength = songLength }.Build());
        Assert.Contains("song length", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FilesEndingInsideThePatternsAreRejected()
    {
        byte[] bytes = new ModBuilder().WithOrders(0, 1, 2).Build();
        ConversionException error = Rejects(bytes[..(1084 + 1500)]);
        Assert.Contains("pattern data", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3, 3, 0, 0, 3)]       // unused entries within the played range: nothing to decide
    [InlineData(3, 3, 90, 0, 3)]      // garbage entry; file sized for the played patterns
    [InlineData(5, 3, 4, 0, 5)]       // extra pattern stored after the played ones
    [InlineData(3, 3, 90, 5000, 3)]   // same with sample data after the patterns
    public void PatternCountPrefersTheReadingThatFitsTheFileSize(int filePatterns, int played, int unusedEntry, long sampleBytes, int expected)
    {
        int[] table = new int[128];
        for (int i = 0; i < played; i++)
        {
            table[i] = i;
        }

        table[played] = unusedEntry;
        long fileLength = 1084 + (filePatterns * 1024L) + sampleBytes;

        Assert.Equal(expected, ModReader.PatternCount(fileLength, table, played, sampleBytes));
    }
}
