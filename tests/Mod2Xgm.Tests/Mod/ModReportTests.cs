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
    public void ProTrackerReportMatchesGolden()
    {
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

        Golden.Verify(Path.Combine("report", "protracker-31.txt"), Render(bytes, ignored: new HashSet<int> { 7 }));
    }

    /// <summary>A 15-sample module: no tag, 15 slots, and byte 471 shown with both of its readings.</summary>
    [Fact]
    public void SoundtrackerReportMatchesGolden()
    {
        byte[] bytes = new ModBuilder { Soundtracker = true, Title = "old song", RestartPosition = 120 }
            .WithSample(1, "piano", 1000)
            .WithSample(15, "last slot", 200)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: Period)
            .Build();

        Golden.Verify(Path.Combine("report", "soundtracker-15.txt"), Render(bytes, ignored: new HashSet<int>()));
    }

    /// <summary>Without <c>Fxx</c> on the first row, the tempo line shows ProTracker's defaults.</summary>
    [Fact]
    public void TempoDefaultsWhenTheFirstRowSetsNone()
    {
        ModModule module = Read(new ModBuilder()
            .WithSample(1, "kick", 64)
            .WithCell(0, row: 1, channel: 0, sample: 1, period: Period, effect: SetSpeed, parameter: 3)
            .Build());

        Assert.Equal(new InitialTempo(6, 125), ModReport.FindInitialTempo(module));
    }

    /// <summary>Channels are read left to right, so the rightmost speed and BPM win; <c>F00</c> (stop) sets neither.</summary>
    [Fact]
    public void TempoTakesTheRightmostCommandOfEachKind()
    {
        ModModule module = Read(new ModBuilder()
            .WithSample(1, "kick", 64)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: Period, effect: SetSpeed, parameter: 3)
            .WithCell(0, row: 0, channel: 1, sample: 0, period: 0, effect: SetSpeed, parameter: 0x90)
            .WithCell(0, row: 0, channel: 2, sample: 0, period: 0, effect: SetSpeed, parameter: 5)
            .WithCell(0, row: 0, channel: 3, sample: 0, period: 0, effect: SetSpeed, parameter: 0)
            .Build());

        Assert.Equal(new InitialTempo(5, 0x90), ModReport.FindInitialTempo(module));
    }

    /// <summary>The first row played is row 0 of the first order's pattern, not of pattern 0.</summary>
    [Fact]
    public void TempoComesFromTheFirstOrdersPattern()
    {
        ModModule module = Read(new ModBuilder()
            .WithSample(1, "kick", 64)
            .WithOrders(1, 0)
            .WithCell(0, row: 0, channel: 0, sample: 1, period: Period, effect: SetSpeed, parameter: 8)
            .WithCell(1, row: 0, channel: 0, sample: 1, period: Period, effect: SetSpeed, parameter: 3)
            .Build());

        Assert.Equal(3, ModReport.FindInitialTempo(module).Speed);
    }

    /// <summary>The format line names the tracker that writes each 4-channel tag.</summary>
    [Theory]
    [InlineData("M.K.", "ProTracker M.K.")]
    [InlineData("M!K!", "ProTracker M!K!")]
    [InlineData("FLT4", "StarTrekker FLT4")]
    [InlineData("4CHN", "FastTracker 4CHN")]
    public void FormatLineNamesTheTracker(string tag, string expected)
    {
        ModModule module = Read(new ModBuilder { Tag = tag }.WithSample(1, "kick", 64).WithCell(0, row: 0, channel: 0, sample: 1, period: Period).Build());

        string format = ModReport.MetadataLines(module, "song.mod").Single(l => l.StartsWith("Format", StringComparison.Ordinal));
        Assert.Equal($"Format      : {expected} — 4 channels, 31 sample slots", format);
    }

    /// <summary>Names keep their spaces and braces (quoted, never read as template holes); control characters show as '·'.</summary>
    [Fact]
    public void NamesArePrintedAsStored()
    {
        Assert.Equal("\"  {x} a·b \"", ModReport.Quoted("  {x} a\u0001b "));
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

    private static ModModule Read(byte[] bytes) => ModReader.Read(bytes, new ConversionNotes(Logger.None));
}
