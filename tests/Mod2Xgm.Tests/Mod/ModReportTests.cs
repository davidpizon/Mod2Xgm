using System.Globalization;
using System.Text;
using Mod2Xgm.Mod;
using Serilog.Core;
using Serilog.Formatting.Display;

namespace Mod2Xgm.Tests.Mod;

/// <summary>Tests for <see cref="ModReport"/>: console report sections 1–2 (docs/cli.md#console-report, plan T2.5).</summary>
public sealed class ModReportTests
{
    private const int Period = 428; // C-2 (docs/formats/mod.md#pitch)
    private const int SetSpeed = 0xF;

    /// <summary>A 31-sample module with every kind of slot: used, unused, empty with credits, looped, ignored, odd names.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void LogSamples_WithProTrackerModule_MatchesGolden()
    {
        // Arrange
        byte[] bytes = new ModBuilder { Title = "space {debris}" }
            .WithSample(1, "bassdrum", 4532)
            .WithSample(2, "snare 2", 3870, fineTune: -2, volume: 48)
            .WithSample(3, "  by Jester", 0, volume: 0)
            .WithSample(4, "strings", 8000, loopStart: 2000, loopLength: 6000)
            .WithSample(5, "never played", 100)
            .WithSample(7, "vocal", 2104, fineTune: 7)
            .WithSample(8, string.Empty, 64, rawName: Encoding.Latin1.GetBytes("bad\u0007name"))
            .WithOrders(0, 1)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: Period, effect: SetSpeed, parameter: 4)
            .WithCell(0, row: 0, channel: 1, sample: 2, period: Period, effect: SetSpeed, parameter: 0x8C)
            .WithCell(0, row: 0, channel: 2, sample: 4, period: Period)
            .WithCell(0, row: 0, channel: 3, sample: 7, period: Period)
            .WithCell(1, row: 0, channel: 0, sample: 1, period: Period)
            .WithCell(1, row: 0, channel: 1, sample: 2, period: Period)
            .WithCell(1, row: 16, channel: 0, sample: 1, period: Period)
            .Build();

        // Act
        string report = Render(bytes, ignored: new HashSet<int> { 7 });

        // Assert
        Golden.Verify(Path.Combine("report", "protracker-31.txt"), report);
    }

    /// <summary>A 15-sample module: no tag, 15 slots, and byte 471 shown with both of its readings.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void LogSamples_WithSoundtrackerModule_MatchesGolden()
    {
        // Arrange
        byte[] bytes = new ModBuilder { Soundtracker = true, Title = "old song", RestartPosition = 120 }
            .WithSample(1, "piano", 1000)
            .WithSample(15, "last slot", 200)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: Period)
            .Build();

        // Act
        string report = Render(bytes, ignored: new HashSet<int>());

        // Assert
        Golden.Verify(Path.Combine("report", "soundtracker-15.txt"), report);
    }

    /// <summary>Without <c>Fxx</c> on the first row, the tempo line shows ProTracker's defaults.</summary>
    [Fact]
    public void FindInitialTempo_WithoutFxxOnFirstRow_ReturnsDefaults()
    {
        // Arrange
        ModModule module = Read(new ModBuilder()
            .WithSample(1, "kick", 64)
            .WithCell(0, row: 1, channel: 0, sample: 1, period: Period, effect: SetSpeed, parameter: 3)
            .Build());

        // Act
        InitialTempo tempo = ModReport.FindInitialTempo(module);

        // Assert
        Assert.Equal(new InitialTempo(6, 125), tempo);
    }

    /// <summary>Channels are read left to right, so the rightmost speed and BPM win; <c>F00</c> (stop) sets neither.</summary>
    [Fact]
    public void FindInitialTempo_WithSeveralCommands_TakesRightmostOfEachKind()
    {
        // Arrange
        ModModule module = Read(new ModBuilder()
            .WithSample(1, "kick", 64)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: Period, effect: SetSpeed, parameter: 3)
            .WithCell(0, row: 0, channel: 1, sample: 0, period: 0, effect: SetSpeed, parameter: 0x90)
            .WithCell(0, row: 0, channel: 2, sample: 0, period: 0, effect: SetSpeed, parameter: 5)
            .WithCell(0, row: 0, channel: 3, sample: 0, period: 0, effect: SetSpeed, parameter: 0)
            .Build());

        // Act
        InitialTempo tempo = ModReport.FindInitialTempo(module);

        // Assert
        Assert.Equal(new InitialTempo(5, 0x90), tempo);
    }

    /// <summary>The first row played is row 0 of the first order's pattern, not of pattern 0.</summary>
    [Fact]
    public void FindInitialTempo_WithOtherFirstPattern_ReadsFirstOrdersPattern()
    {
        // Arrange
        ModModule module = Read(new ModBuilder()
            .WithSample(1, "kick", 64)
            .WithOrders(1, 0)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: Period, effect: SetSpeed, parameter: 8)
            .WithCell(1, row: 0, channel: 0, sample: 1, period: Period, effect: SetSpeed, parameter: 3)
            .Build());

        // Act
        InitialTempo tempo = ModReport.FindInitialTempo(module);

        // Assert
        Assert.Equal(3, tempo.Speed);
    }

    /// <summary>Under Ultimate Soundtracker rules (the 15-sample default) the tempo line comes from byte 471.</summary>
    [Fact]
    public void FindInitialTempo_WithUltimateSoundtrackerRules_ReadsByte471()
    {
        // Arrange
        ModModule module = Read(new ModBuilder { Soundtracker = true, RestartPosition = 100 }.WithSample(1, "kick", 64).Build());

        // Act
        InitialTempo tempo = ModReport.FindInitialTempo(module);

        // Assert
        Assert.Equal(6, tempo.Speed);
        Assert.Equal(103.8318, tempo.Bpm, 1e-4);
    }

    /// <summary>ProTracker rules ignore byte 471 of a 15-sample file.</summary>
    [Fact]
    public void FindInitialTempo_WithProTrackerRules_IgnoresByte471()
    {
        // Arrange
        byte[] bytes = new ModBuilder { Soundtracker = true, RestartPosition = 100 }.WithSample(1, "kick", 64).Build();
        ModModule module = ModReader.Read(bytes, new ConversionNotes(Logger.None), ModPlayer.ProTracker2);

        // Act
        InitialTempo tempo = ModReport.FindInitialTempo(module);

        // Assert
        Assert.Equal(new InitialTempo(6, 125), tempo);
    }

    /// <summary>The format line names the tracker that writes each 4-channel tag.</summary>
    [Theory]
    [InlineData("M.K.", "ProTracker M.K.")]
    [InlineData("M!K!", "ProTracker M!K!")]
    [InlineData("FLT4", "StarTrekker FLT4")]
    [InlineData("4CHN", "FastTracker 4CHN")]
    public void MetadataLines_WithTag_NamesTracker(string tag, string expected)
    {
        // Arrange
        ModModule module = Read(new ModBuilder { Tag = tag }.WithSample(1, "kick", 64).WithCell(0, row: 0, channel: 0, sample: 1, period: Period).Build());

        // Act
        IEnumerable<string> lines = ModReport.MetadataLines(module, "song.mod");

        // Assert
        Assert.Equal($"Format      : {expected} — 4 channels, 31 sample slots", lines.Single(l => l.StartsWith("Format", StringComparison.Ordinal)));
    }

    /// <summary>Names keep their spaces and braces (quoted, never read as template holes); control characters show as '·'.</summary>
    [Fact]
    public void Quoted_WithBracesAndControlCharacters_PrintsAsStored()
    {
        // Act
        string quoted = ModReport.Quoted("  {x} a\u0001b ");

        // Assert
        Assert.Equal("\"  {x} a·b \"", quoted);
    }

    /// <summary>Renders sections 1–2 exactly as the console prints them (level prefix included).</summary>
    private static string Render(byte[] bytes, IReadOnlySet<int> ignored)
    {
        ConversionNotes notes = new(Logger.None);
        ModModule module = ModReader.Read(bytes, notes);
        SampleUsage usage = SampleUsageScanner.Scan(module, notes);

        CollectingSink sink = new();
        Serilog.ILogger logger = sink.CreateLogger();
        ModReport.LogMetadata(logger, module, "song.mod");
        ModReport.LogSamples(logger, module, usage, ignored);

        MessageTemplateTextFormatter formatter = new(Program.ConsoleOutputTemplate, CultureInfo.InvariantCulture);
        using StringWriter output = new(CultureInfo.InvariantCulture);
        foreach (Serilog.Events.LogEvent logEvent in sink.Events)
        {
            formatter.Format(logEvent, output);
        }

        return output.ToString();
    }

    /// <summary>Reads <paramref name="bytes"/> with the file's default rules, discarding the reader's notes.</summary>
    private static ModModule Read(byte[] bytes) => ModReader.Read(bytes, new ConversionNotes(Logger.None));
}
