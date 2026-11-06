using System.Text;
using Mod2Xgm.Mod;
using Serilog.Core;

namespace Mod2Xgm.Tests.Mod;

/// <summary>Tests for <see cref="ModReader"/>: 15-sample Soundtracker detection (plan T2.3), player rules and the conversion notes for signs of a damaged file.</summary>
public sealed class SoundtrackerTests
{
    private const string Detected = "reading the file as a 15-sample Soundtracker module";

    private readonly ConversionNotes _notes = new(Logger.None);

    /// <summary>The 15-sample layout is read: no tag, 15 samples, orders at 472, patterns at 600.</summary>
    [Fact]
    public void Read_WithFifteenSampleLayout_ReadsEveryField()
    {
        // Arrange
        byte[] bytes = Soundtracker()
            .WithOrders(0, 1, 0)
            .WithSample(1, "kick", 64, fineTune: 0, volume: 40)
            .WithSample(15, "  by Karsten", 0)
            .WithCell(1, row: 3, channel: 1, sample: 15, period: 428, effect: 0xC, parameter: 0x20)
            .Build();
        int[] expectedOrders = [0, 1, 0];

        // Act
        ModModule module = Read(bytes);

        // Assert
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

    /// <summary>A clean 15-sample file gets exactly the two assumption notes: no tag, and how byte 471 is read.</summary>
    [Fact]
    public void Read_WithCleanFifteenSampleFile_NotesOnlyTwoAssumptions()
    {
        // Arrange
        byte[] bytes = Soundtracker().WithSample(1, "kick", 64).Build();

        // Act
        Read(bytes);

        // Assert
        Assert.Equal(2, _notes.Count);
        AssertNote("No ProTracker tag at byte offset 1080");
        AssertNote(Detected);
        AssertNote("Byte 471 holds 120");
        AssertNote("Ultimate Soundtracker reads it as the tempo");
    }

    /// <summary>A clean ProTracker file gets no notes.</summary>
    [Fact]
    public void Read_WithCleanProTrackerFile_AddsNoNotes()
    {
        // Arrange
        byte[] bytes = new ModBuilder().WithOrders(0, 1).WithSample(1, "kick", 64).WithSample(31, "credits", 0).Build();

        // Act
        Read(bytes);

        // Assert
        Assert.Empty(_notes.Messages);
    }

    /// <summary>Garbage is rejected with both reasons: no tag, and not a valid 15-sample file.</summary>
    [Fact]
    public void Read_WithGarbage_ThrowsWithBothReasons()
    {
        // Arrange
        byte[] bytes = new byte[3000];
        Array.Fill(bytes, (byte)0xFF);

        // Act
        ConversionException error = Rejected(bytes);

        // Assert
        Assert.Contains("byte offset 1080 (found 'ÿÿÿÿ')", error.Message, StringComparison.Ordinal);
        Assert.Contains("15-sample Soundtracker", error.Message, StringComparison.Ordinal);
        Assert.Contains("song length 255", error.Message, StringComparison.Ordinal);
        Assert.Contains("byte offset 470", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A file too short for a tag says so, and why it isn't a 15-sample file either.</summary>
    [Fact]
    public void Read_WithFileTooShortForTag_ThrowsSayingWhy()
    {
        // Act
        ConversionException error = Rejected(new byte[700]);

        // Assert
        Assert.Contains("too short for a ProTracker tag", error.Message, StringComparison.Ordinal);
        Assert.Contains("song length 0", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A played order above 127 is rejected with its offset.</summary>
    [Fact]
    public void Read_WithPlayedOrderAbove127_ThrowsWithOffset()
    {
        // Arrange
        byte[] bytes = Soundtracker().WithOrders(0, 0).Build();
        bytes[473] = 200; // second order, docs/formats/mod.md: orders at 472

        // Act
        ConversionException error = Rejected(bytes);

        // Assert
        Assert.Contains("order 1 plays pattern 200", error.Message, StringComparison.Ordinal);
        Assert.Contains("byte offset 473", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A 15-sample file that ends inside the patterns is rejected.</summary>
    [Fact]
    public void Read_WithFifteenSampleFileEndingInsidePatterns_Throws()
    {
        // Arrange
        byte[] bytes = Soundtracker().WithOrders(0, 1, 2).Build()[..(600 + 1500)];

        // Act
        ConversionException error = Rejected(bytes);

        // Assert
        Assert.Contains("15-sample", error.Message, StringComparison.Ordinal);
        Assert.Contains("ends inside the pattern data", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Truncated 15-sample data is kept as far as it goes, with a note.</summary>
    [Fact]
    public void Read_WithTruncatedFifteenSampleData_KeepsWhatThereIsAndNotes()
    {
        // Arrange
        byte[] full = Soundtracker().WithSample(1, "kick", 64).WithSample(2, "snare", 100).Build();

        // Act
        ModModule module = Read(full[..^40]);

        // Assert
        Assert.Equal(60, module.Samples[1].Length);
        AssertNote("kept 60 of 100 bytes");
    }

    /// <summary>Other four-channel trackers' tags are rejected, naming the tracker.</summary>
    [Theory]
    [InlineData("N.T.", "NoiseTracker")]
    [InlineData("M&K!", "His Master's Noise")]
    [InlineData("FEST", "His Master's Noise")]
    [InlineData("LARD", "unknown tracker")]
    [InlineData("NSMS", "unknown tracker")]
    public void Read_WithOtherTrackerTag_ThrowsNamingTracker(string tag, string tracker)
    {
        // Arrange
        byte[] bytes = new ModBuilder { Tag = tag }.WithSample(1, "s", 64).Build();

        // Act
        ConversionException error = Rejected(bytes);

        // Assert
        Assert.Contains($"tag '{tag}' at byte offset 1080", error.Message, StringComparison.Ordinal);
        Assert.Contains(tracker, error.Message, StringComparison.Ordinal);
        Assert.Contains("31 samples", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Under ProTracker rules, a loop that only fits when its start is read in bytes is read that way, with a note.</summary>
    [Fact]
    public void Read_WithProTrackerRulesAndLoopFittingOnlyInBytes_ReadsBytesAndNotes()
    {
        // Arrange: 100-byte sample, loop start 80, length 20: as words (160) the loop can't fit; as bytes it ends exactly at 100.
        byte[] bytes = Soundtracker().WithSample(1, "pad", 100, loopStartWords: 80, loopLength: 20).Build();

        // Act
        ModModule module = Read(bytes, ModPlayer.ProTracker2);

        // Assert
        Assert.Equal(80, module.Samples[0].LoopStart);
        Assert.Equal(20, module.Samples[0].LoopLength);
        AssertNote("Sample 1 pad: the loop only fits when its start is read in bytes");
    }

    /// <summary>A 31-sample file never reads a loop start as bytes: the loop is removed instead.</summary>
    [Fact]
    public void Read_WithProTrackerFileAndLoopFittingOnlyInBytes_RemovesLoop()
    {
        // Arrange
        byte[] bytes = new ModBuilder().WithSample(1, "pad", 100, loopStartWords: 80, loopLength: 20).Build();

        // Act
        ModModule module = Read(bytes);

        // Assert
        Assert.False(module.Samples[0].Loops);
        AssertNote("loop starts past the sample end");
    }

    /// <summary>Under ProTracker rules, loops that fit either way are read as words, and counted in one note.</summary>
    [Fact]
    public void Read_WithProTrackerRulesAndLoopsFittingEitherWay_ReadsWordsAndCounts()
    {
        // Arrange
        byte[] bytes = Soundtracker()
            .WithSample(1, "a", 100, loopStartWords: 10, loopLength: 20)
            .WithSample(2, "b", 100, loopStartWords: 0, loopLength: 20)
            .WithSample(3, "c", 100, loopStartWords: 20, loopLength: 40)
            .Build();

        // Act
        ModModule module = Read(bytes, ModPlayer.ProTracker2);

        // Assert
        Assert.Equal(20, module.Samples[0].LoopStart);
        Assert.Equal(40, module.Samples[2].LoopStart);
        AssertNote("2 looping samples have a loop start that fits whether it's stored in words or in bytes");
    }

    /// <summary>Under ProTracker rules, a loop that fits neither way is removed, with no bytes note.</summary>
    [Fact]
    public void Read_WithProTrackerRulesAndLoopFittingNeitherWay_RemovesLoop()
    {
        // Arrange
        byte[] bytes = Soundtracker().WithSample(1, "pad", 100, loopStartWords: 90, loopLength: 20).Build();

        // Act
        ModModule module = Read(bytes, ModPlayer.ProTracker2);

        // Assert
        Assert.False(module.Samples[0].Loops);
        AssertNote("loop starts past the sample end");
        Assert.DoesNotContain(_notes.Messages, m => m.Contains("read in bytes", StringComparison.Ordinal));
    }

    /// <summary>Odd header values are noted in both layouts: unprintable names, loud volumes, odd finetune bytes, huge samples.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_WithOddHeaderValues_NotesEachOne(bool soundtracker)
    {
        // Arrange
        byte[] badName = new byte[22];
        Encoding.Latin1.GetBytes("bad\u0007name").CopyTo(badName, 0);
        byte[] bytes = new ModBuilder { Soundtracker = soundtracker, Title = "ti\u0001tle" }
            .WithSample(1, "loud", 64, volume: 80)
            .WithSample(2, string.Empty, 64, rawName: badName)
            .WithSample(3, "odd", 64, fineTuneByte: 0x13)
            .WithSample(4, "huge", 65538)
            .Build();

        // Act
        Read(bytes);

        // Assert
        AssertNote("The song title contains unprintable bytes");
        AssertNote("Sample 1 loud: volume 80 is above the maximum of 64");
        AssertNote("Sample 2 bad\u0007name: the name contains unprintable bytes");
        AssertNote("Sample 3 odd: finetune byte 0x13 has its unused high bits set");
        AssertNote("Sample 4 huge: length 65538 bytes is over the 65536 bytes trackers allow");
    }

    /// <summary>A finetune in a 15-sample file is kept, but noted: Soundtracker had none.</summary>
    [Fact]
    public void Read_WithFinetuneInFifteenSampleFile_KeepsAndNotes()
    {
        // Arrange
        byte[] bytes = Soundtracker().WithSample(1, "s", 64, fineTune: -2).Build();

        // Act
        ModModule module = Read(bytes);

        // Assert
        Assert.Equal(-2, module.Samples[0].FineTune);
        AssertNote("Sample 1 s: finetune -2 in a 15-sample file");
    }

    /// <summary>Impossible pattern cells are counted, each kind in one note naming the first.</summary>
    [Theory]
    [InlineData(false, 40, 32, 31)]
    [InlineData(true, 20, 16, 15)]
    public void Read_WithImpossibleCells_CountsEachKind(bool soundtracker, int badSample, int secondBadSample, int sampleCount)
    {
        // Arrange
        byte[] bytes = new ModBuilder { Soundtracker = soundtracker }
            .WithSample(1, "s", 64)
            .WithCell(0, row: 2, channel: 3, sample: badSample, period: 428)
            .WithCell(0, row: 5, channel: 0, sample: secondBadSample, period: 428)
            .WithCell(0, row: 6, channel: 1, sample: 1, period: 50)
            .WithCell(0, row: 7, channel: 1, sample: 1, period: 1000)
            .WithCell(0, row: 8, channel: 1, sample: 1, period: 0)
            .Build();

        // Act
        Read(bytes);

        // Assert
        AssertNote($"2 pattern cells use a sample number above {sampleCount} (first at pattern 0 row 2 channel 4)");
        AssertNote("2 pattern cells hold a period outside 108–907 (first at pattern 0 row 6 channel 2)");
    }

    /// <summary>Bytes after the last sample's data are ignored, with a note.</summary>
    [Fact]
    public void Read_WithTrailingBytes_Notes()
    {
        // Arrange
        byte[] bytes = new ModBuilder { TrailingBytes = new byte[10] }.WithSample(1, "s", 64).Build();

        // Act
        Read(bytes);

        // Assert
        AssertNote("10 bytes after the last sample's data; ignored");
    }

    /// <summary>Garbage unused order entries are ignored, with a note.</summary>
    [Fact]
    public void Read_WithGarbageOrderEntries_IgnoresAndNotes()
    {
        // Arrange
        ModBuilder builder = new ModBuilder().WithOrders(0, 1).WithSample(1, "s", 64);
        byte[] honest = builder.Build();
        builder.UnusedOrderEntries = [90];
        byte[] bytes = [.. builder.Build()[..1084], .. honest[1084..]];

        // Act
        ModModule module = Read(bytes);

        // Assert
        Assert.Equal(2, module.Patterns.Count);
        AssertNote("point at patterns the file doesn't hold (up to pattern 90); ignored");
    }

    /// <summary>A readable 15-sample file has no Soundtracker problem.</summary>
    [Fact]
    public void SoundtrackerProblem_WithReadableFile_ReturnsNull()
    {
        // Arrange
        byte[] bytes = Soundtracker().WithSample(1, "s", 64).Build();

        // Act
        string? problem = ModReader.SoundtrackerProblem(bytes);

        // Assert
        Assert.Null(problem);
    }

    /// <summary>The pattern count uses the layout's pattern offset, so garbage entries are ignored in 15-sample files too.</summary>
    [Fact]
    public void PatternCount_WithSoundtrackerOffset_IgnoresGarbageEntry()
    {
        // Arrange
        int[] table = new int[128];
        table[1] = 1;
        table[2] = 90; // garbage after a 2-order song

        // Act
        int count = ModReader.PatternCount(600 + (2 * 1024) + 64, table, 2, 64, ModReader.Soundtracker.PatternOffset);

        // Assert
        Assert.Equal(2, count);
    }

    /// <summary>The two layouts' offsets are those of docs/formats/mod.md.</summary>
    [Fact]
    public void Layouts_Offsets_MatchFormatDoc()
    {
        // Assert
        Assert.Equal(600, ModReader.Soundtracker.PatternOffset);
        Assert.Equal(472, ModReader.Soundtracker.OrderTableOffset);
        Assert.Equal(1084, ModReader.ProTracker.PatternOffset);
    }

    /// <summary>15-sample files default to Ultimate Soundtracker rules: every loop start is in bytes, with one note.</summary>
    [Fact]
    public void Read_WithFifteenSampleFileByDefault_ReadsEveryLoopStartInBytes()
    {
        // Arrange
        byte[] bytes = Soundtracker()
            .WithSample(1, "a", 100, loopStartWords: 10, loopLength: 20)
            .WithSample(2, "b", 100, loopStartWords: 80, loopLength: 20)
            .WithSample(3, "c", 100, loopStartWords: 0, loopLength: 20)
            .Build();

        // Act
        ModModule module = Read(bytes);

        // Assert
        Assert.Equal(ModPlayer.UltimateSoundtracker, module.Player);
        Assert.Equal(10, module.Samples[0].LoopStart);
        Assert.Equal(80, module.Samples[1].LoopStart);
        Assert.Equal(20, module.Samples[1].LoopLength);
        Assert.Equal(0, module.Samples[2].LoopStart);
        AssertNote("2 looping samples: loop starts read in bytes, as Ultimate Soundtracker stored them");
        Assert.DoesNotContain(_notes.Messages, m => m.Contains("fits whether", StringComparison.Ordinal));
    }

    /// <summary>The player is the file's default (ProTracker 2 for 31 samples, Ultimate Soundtracker for 15) unless one is chosen.</summary>
    [Theory]
    [InlineData(false, null, nameof(ModPlayer.ProTracker2))]
    [InlineData(false, nameof(ModPlayer.UltimateSoundtracker), nameof(ModPlayer.UltimateSoundtracker))]
    [InlineData(true, null, nameof(ModPlayer.UltimateSoundtracker))]
    [InlineData(true, nameof(ModPlayer.ProTracker2), nameof(ModPlayer.ProTracker2))]
    public void Read_WithFormatAndChosenPlayer_UsesChosenOrDefaultPlayer(bool soundtracker, string? chosen, string expected)
    {
        // Arrange
        byte[] bytes = (soundtracker ? Soundtracker() : new ModBuilder()).WithSample(1, "kick", 64).Build();
        ModPlayer? player = chosen is null ? null : Enum.Parse<ModPlayer>(chosen);

        // Act
        ModModule module = Read(bytes, player);

        // Assert
        Assert.Equal(Enum.Parse<ModPlayer>(expected), module.Player);
    }

    /// <summary>The byte-471 note says how the byte is read under the player used.</summary>
    [Theory]
    [InlineData(null, "and so does this conversion")]
    [InlineData(nameof(ModPlayer.ProTracker2), "read as the restart position (ProTracker rules)")]
    public void Read_WithFifteenSampleFile_NotesHowByte471IsRead(string? chosen, string expected)
    {
        // Arrange
        byte[] bytes = Soundtracker().WithSample(1, "kick", 64).Build();
        ModPlayer? player = chosen is null ? null : Enum.Parse<ModPlayer>(chosen);

        // Act
        Read(bytes, player);

        // Assert
        AssertNote(expected);
    }

    /// <summary>The test builder refuses sample 16 in the 15-sample layout, so tests can't build impossible files by mistake.</summary>
    [Fact]
    public void ModBuilder_WithSampleBeyondFifteenInSoundtrackerLayout_Throws()
    {
        // Arrange
        ModBuilder sut = Soundtracker().WithSample(16, "s", 64);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => sut.Build());
    }

    /// <summary>A 15-sample builder whose byte 471 holds 120, as Soundtracker files do.</summary>
    private static ModBuilder Soundtracker() => new() { Soundtracker = true, RestartPosition = 120 };

    /// <summary>Reads <paramref name="bytes"/> with <paramref name="player"/>'s rules (default: the file's own) into this test's notes.</summary>
    private ModModule Read(byte[] bytes, ModPlayer? player = null) => ModReader.Read(bytes, _notes, player);

    /// <summary>The <see cref="ConversionException"/> reading <paramref name="bytes"/> throws, checked to be InvalidInput.</summary>
    private static ConversionException Rejected(byte[] bytes)
    {
        ConversionException error = Assert.Throws<ConversionException>(() => ModReader.Read(bytes, new ConversionNotes(Logger.None)));
        Assert.Equal(ExitCode.InvalidInput, error.Code);
        return error;
    }

    /// <summary>Asserts that some note contains <paramref name="text"/>.</summary>
    private void AssertNote(string text) => Assert.Contains(_notes.Messages, m => m.Contains(text, StringComparison.Ordinal));
}
