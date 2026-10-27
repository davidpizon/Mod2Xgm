using System.Text;
using Mod2Xgm.Mod;
using Serilog.Core;

namespace Mod2Xgm.Tests.Mod;

/// <summary>15-sample Soundtracker detection (plan T2.3) and the conversion notes for signs of a damaged file.</summary>
public sealed class SoundtrackerTests
{
    private const string Detected = "reading the file as a 15-sample Soundtracker module";

    private static (ModModule Module, ConversionNotes Notes) Read(byte[] bytes)
    {
        ConversionNotes notes = new(Logger.None);
        return (ModReader.Read(bytes, notes), notes);
    }

    private static ConversionException Rejects(byte[] bytes)
    {
        ConversionException error = Assert.Throws<ConversionException>(() => ModReader.Read(bytes, new ConversionNotes(Logger.None)));
        Assert.Equal(ExitCode.InvalidInput, error.Code);
        return error;
    }

    private static ModBuilder Soundtracker() => new() { Soundtracker = true, RestartPosition = 120 };

    private static void HasNote(ConversionNotes notes, string text) =>
        Assert.Contains(notes.Messages, m => m.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void FifteenSampleLayoutIsRead()
    {
        byte[] bytes = Soundtracker()
            .WithOrders(0, 1, 0)
            .WithSample(1, "kick", 64, fineTune: 0, volume: 40)
            .WithSample(15, "  by Karsten", 0)
            .WithCell(1, row: 3, channel: 1, sample: 15, period: 428, effect: 0xC, parameter: 0x20)
            .Build();

        (ModModule module, _) = Read(bytes);
        int[] expectedOrders = [0, 1, 0];

        Assert.Equal(ModFormat.Soundtracker15, module.Format);
        Assert.Equal(string.Empty, module.Tag);
        Assert.Equal(4, module.ChannelCount);
        Assert.Equal("test song", module.Title);
        Assert.Equal(15, module.Samples.Count);
        Assert.Equal("kick", module.Samples[0].Name);
        Assert.Equal(40, module.Samples[0].Volume);
        Assert.Equal(64, module.Samples[0].Length);
        Assert.Equal("  by Karsten", module.Samples[14].Name);
        Assert.Equal(expectedOrders, module.Orders);
        Assert.Equal(120, module.RestartPosition);
        Assert.Equal(2, module.Patterns.Count);
        Assert.Equal(new ModCell(15, 428, 0xC, 0x20), module.Patterns[1][3, 1]);
        Assert.Equal(bytes.Length, module.FileSize);
    }

    [Fact]
    public void ACleanFifteenSampleFileGetsOnlyTheTwoAssumptionNotes()
    {
        (_, ConversionNotes notes) = Read(Soundtracker().WithSample(1, "kick", 64).Build());

        Assert.Equal(2, notes.Count);
        HasNote(notes, "No ProTracker tag at byte offset 1080");
        HasNote(notes, Detected);
        HasNote(notes, "Byte 471 holds 120");
        HasNote(notes, "Ultimate Soundtracker reads it as the tempo");
    }

    [Fact]
    public void ACleanProTrackerFileGetsNoNotes()
    {
        (_, ConversionNotes notes) = Read(new ModBuilder().WithOrders(0, 1).WithSample(1, "kick", 64).WithSample(31, "credits", 0).Build());

        Assert.Empty(notes.Messages);
    }

    [Fact]
    public void GarbageIsRejectedWithBothReasons()
    {
        byte[] bytes = new byte[3000];
        Array.Fill(bytes, (byte)0xFF);

        ConversionException error = Rejects(bytes);

        Assert.Contains("byte offset 1080 (found 'ÿÿÿÿ')", error.Message, StringComparison.Ordinal);
        Assert.Contains("15-sample Soundtracker", error.Message, StringComparison.Ordinal);
        Assert.Contains("song length 255", error.Message, StringComparison.Ordinal);
        Assert.Contains("byte offset 470", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FilesTooShortForATagSayWhy()
    {
        ConversionException error = Rejects(new byte[700]);

        Assert.Contains("too short for a ProTracker tag", error.Message, StringComparison.Ordinal);
        Assert.Contains("song length 0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlayedOrdersBeyond127AreRejected()
    {
        byte[] bytes = Soundtracker().WithOrders(0, 0).Build();
        bytes[473] = 200; // second order, docs/formats/mod.md: orders at 472

        ConversionException error = Rejects(bytes);

        Assert.Contains("order 1 plays pattern 200", error.Message, StringComparison.Ordinal);
        Assert.Contains("byte offset 473", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FifteenSampleFilesEndingInsideThePatternsAreRejected()
    {
        byte[] bytes = Soundtracker().WithOrders(0, 1, 2).Build();

        ConversionException error = Rejects(bytes[..(600 + 1500)]);

        Assert.Contains("15-sample", error.Message, StringComparison.Ordinal);
        Assert.Contains("ends inside the pattern data", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatedFifteenSampleDataIsKeptAndNoted()
    {
        byte[] full = Soundtracker().WithSample(1, "kick", 64).WithSample(2, "snare", 100).Build();

        (ModModule module, ConversionNotes notes) = Read(full[..^40]);

        Assert.Equal(60, module.Samples[1].Length);
        HasNote(notes, "kept 60 of 100 bytes");
    }

    [Theory]
    [InlineData("N.T.", "NoiseTracker")]
    [InlineData("M&K!", "His Master's Noise")]
    [InlineData("FEST", "His Master's Noise")]
    [InlineData("LARD", "unknown tracker")]
    [InlineData("NSMS", "unknown tracker")]
    public void OtherFourChannelTagsAreRejectedByName(string tag, string tracker)
    {
        ConversionException error = Rejects(new ModBuilder { Tag = tag }.WithSample(1, "s", 64).Build());

        Assert.Contains($"tag '{tag}' at byte offset 1080", error.Message, StringComparison.Ordinal);
        Assert.Contains(tracker, error.Message, StringComparison.Ordinal);
        Assert.Contains("31 samples", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoopStartStoredInBytesIsDetected()
    {
        // 100-byte sample, loop start 80, length 20: as words (160) the loop can't fit; as bytes it ends exactly at 100.
        (ModModule module, ConversionNotes notes) = Read(Soundtracker()
            .WithSample(1, "pad", 100, loopStartWords: 80, loopLength: 20)
            .Build());

        Assert.Equal(80, module.Samples[0].LoopStart);
        Assert.Equal(20, module.Samples[0].LoopLength);
        HasNote(notes, "Sample 1 pad: the loop only fits when its start is read in bytes");
    }

    [Fact]
    public void ProTrackerFilesNeverReadLoopStartsAsBytes()
    {
        (ModModule module, ConversionNotes notes) = Read(new ModBuilder()
            .WithSample(1, "pad", 100, loopStartWords: 80, loopLength: 20)
            .Build());

        Assert.False(module.Samples[0].Loops);
        HasNote(notes, "loop starts past the sample end");
    }

    [Fact]
    public void LoopsThatFitEitherWayAreReadAsWordsAndCounted()
    {
        (ModModule module, ConversionNotes notes) = Read(Soundtracker()
            .WithSample(1, "a", 100, loopStartWords: 10, loopLength: 20)
            .WithSample(2, "b", 100, loopStartWords: 0, loopLength: 20)
            .WithSample(3, "c", 100, loopStartWords: 20, loopLength: 40)
            .Build());

        Assert.Equal(20, module.Samples[0].LoopStart);
        Assert.Equal(40, module.Samples[2].LoopStart);
        HasNote(notes, "2 looping samples have a loop start that fits whether it's stored in words or in bytes");
    }

    [Fact]
    public void LoopsThatFitNeitherWayAreRemoved()
    {
        (ModModule module, ConversionNotes notes) = Read(Soundtracker()
            .WithSample(1, "pad", 100, loopStartWords: 90, loopLength: 20)
            .Build());

        Assert.False(module.Samples[0].Loops);
        HasNote(notes, "loop starts past the sample end");
        Assert.DoesNotContain(notes.Messages, m => m.Contains("read in bytes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeaderHintsAreNoted(bool soundtracker)
    {
        byte[] badName = new byte[22];
        Encoding.Latin1.GetBytes("bad\u0007name").CopyTo(badName, 0);
        byte[] bytes = new ModBuilder { Soundtracker = soundtracker, Title = "ti\u0001tle" }
            .WithSample(1, "loud", 64, volume: 80)
            .WithSample(2, string.Empty, 64, rawName: badName)
            .WithSample(3, "odd", 64, fineTuneByte: 0x13)
            .WithSample(4, "huge", 65538)
            .Build();

        (_, ConversionNotes notes) = Read(bytes);

        HasNote(notes, "The song title contains unprintable bytes");
        HasNote(notes, "Sample 1 loud: volume 80 is above the maximum of 64");
        HasNote(notes, "Sample 2 bad\u0007name: the name contains unprintable bytes");
        HasNote(notes, "Sample 3 odd: finetune byte 0x13 has its unused high bits set");
        HasNote(notes, "Sample 4 huge: length 65538 bytes is over the 65536 bytes trackers allow");
    }

    [Fact]
    public void FinetuneInAFifteenSampleFileIsNoted()
    {
        (ModModule module, ConversionNotes notes) = Read(Soundtracker().WithSample(1, "s", 64, fineTune: -2).Build());

        Assert.Equal(-2, module.Samples[0].FineTune);
        HasNote(notes, "Sample 1 s: finetune -2 in a 15-sample file");
    }

    [Theory]
    [InlineData(false, 40, 32)]
    [InlineData(true, 20, 16)]
    public void PatternHintsAreCounted(bool soundtracker, int badSample, int secondBadSample)
    {
        byte[] bytes = new ModBuilder { Soundtracker = soundtracker }
            .WithSample(1, "s", 64)
            .WithCell(0, row: 2, channel: 3, sample: badSample, period: 428)
            .WithCell(0, row: 5, channel: 0, sample: secondBadSample, period: 428)
            .WithCell(0, row: 6, channel: 1, sample: 1, period: 50)
            .WithCell(0, row: 7, channel: 1, sample: 1, period: 1000)
            .WithCell(0, row: 8, channel: 1, sample: 1, period: 0)
            .Build();

        (_, ConversionNotes notes) = Read(bytes);

        HasNote(notes, $"2 pattern cells use a sample number above {(soundtracker ? 15 : 31)} (first at pattern 0 row 2 channel 4)");
        HasNote(notes, "2 pattern cells hold a period outside 108–907 (first at pattern 0 row 6 channel 2)");
    }

    [Fact]
    public void BytesAfterTheSampleDataAreNoted()
    {
        (_, ConversionNotes notes) = Read(new ModBuilder { TrailingBytes = new byte[10] }.WithSample(1, "s", 64).Build());

        HasNote(notes, "10 bytes after the last sample's data; ignored");
    }

    [Fact]
    public void IgnoredGarbageOrderEntriesAreNoted()
    {
        ModBuilder builder = new ModBuilder().WithOrders(0, 1).WithSample(1, "s", 64);
        byte[] honest = builder.Build();
        builder.UnusedOrderEntries = [90];
        byte[] bytes = [.. builder.Build()[..1084], .. honest[1084..]];

        (ModModule module, ConversionNotes notes) = Read(bytes);

        Assert.Equal(2, module.Patterns.Count);
        HasNote(notes, "point at patterns the file doesn't hold (up to pattern 90); ignored");
    }

    [Fact]
    public void SoundtrackerProblemAcceptsAReadableFile() =>
        Assert.Null(ModReader.SoundtrackerProblem(Soundtracker().WithSample(1, "s", 64).Build()));

    [Fact]
    public void PatternCountUsesTheLayoutsPatternOffset()
    {
        int[] table = new int[128];
        table[0] = 0;
        table[1] = 1;
        table[2] = 90; // garbage after a 2-order song

        Assert.Equal(2, ModReader.PatternCount(600 + (2 * 1024) + 64, table, 2, 64, ModReader.Soundtracker.PatternOffset));
        Assert.Equal(600, ModReader.Soundtracker.PatternOffset);
        Assert.Equal(472, ModReader.Soundtracker.OrderTableOffset);
        Assert.Equal(1084, ModReader.ProTracker.PatternOffset);
    }

    [Fact]
    public void TheBuilderRefusesSamplesBeyondFifteenInTheSoundtrackerLayout() =>
        Assert.Throws<InvalidOperationException>(() => Soundtracker().WithSample(16, "s", 64).Build());
}
