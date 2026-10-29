using Mod2Xgm.Dmf;
using Mod2Xgm.Mapping;
using Mod2Xgm.Tests.Mod;
using Serilog.Events;

namespace Mod2Xgm.Tests.Dmf;

/// <summary>
/// Tests for <see cref="DmfReport"/>, report section 3 for DMF output (plan T5.6, docs/cli.md#console-report): the summary,
/// each FM patch with every sample it stands in for, the noise instruments, and each DAC sample copy with its rate, size,
/// start point and loudness. Songs are MODs built in code and run through the real pipeline (<see cref="DmfConversion"/>).
/// </summary>
public sealed class DmfReportTests
{
    /// <summary>ProTracker period of C-2 (finetune 0).</summary>
    private const int C2 = 428;

    private const int Lead = 1;
    private const int Pad = 2;
    private const int Drum = 3;
    private const int Hat = 4;

    /// <summary>White noise from a fixed seed, so the hat is the same on every run.</summary>
    private static readonly sbyte[] HatData = [.. new Random(7).GetItems<sbyte>([-100, -50, 50, 100], 4000)];

    /// <summary>
    /// Two looped sines (one FM patch), a drum played loud, quieter and from a start point (three DAC copies), and a hat
    /// required on the PSG (a noise instrument).
    /// </summary>
    private static DmfConversion.Result Convert()
    {
        byte[] mod = new ModBuilder { Title = "report" }
            .WithSample(Lead, "lead", 32, loopStart: 0, loopLength: 32, data: i => (sbyte)Math.Round(100 * Math.Sin(2 * Math.PI * i / 32)))
            .WithSample(Pad, "pad {x}", 32, loopStart: 0, loopLength: 32, data: i => (sbyte)Math.Round(100 * Math.Sin(2 * Math.PI * i / 32)))
            .WithSample(Drum, "drum", 4000, data: i => (sbyte)Math.Round(100 * Math.Sin(2 * Math.PI * i / 40) * Math.Exp(-i / 800.0)))
            .WithSample(Hat, "hat", HatData.Length, data: i => HatData[i])
            .WithCell(0, 0, 0, Lead, C2)
            .WithCell(0, 0, 1, Pad, C2)
            .WithCell(0, 0, 2, Drum, C2)
            .WithCell(0, 4, 2, Drum, C2, 0xC, 0x20)
            .WithCell(0, 8, 2, Drum, C2, 0x9, 0x08)
            .WithCell(0, 12, 3, Hat, C2)
            .Build();
        return DmfConversion.Convert(mod, MappingOptions.Default with { RequirePsg = new HashSet<int> { Hat - 1 } });
    }

    /// <summary>The summary counts what the file holds; one line follows per instrument, then per DAC sample, in file order.</summary>
    [Fact]
    public void ListsEveryInstrumentAndSampleWithItsSource()
    {
        DmfConversion.Result result = Convert();

        IReadOnlyList<string> lines = DmfReport.Lines(result.Built, result.Song);

        Assert.Equal(
            [
                "Output (DMF): 1 FM instrument, 1 PSG noise instrument, 3 DAC samples, 13 KB PCM",
                " FM  01  ← 01 \"lead\", 02 \"pad {x}\" (Sine patch)",
                " PSG 02  ← 04 \"hat\" (noise)",
                " DAC 01  ← 03 \"drum\"                    11025 Hz  5321 B",
                " DAC 02  ← 03 \"drum\"                    11025 Hz  5321 B  -6 dB",
                " DAC 03  ← 03 \"drum\"                    11025 Hz  2596 B  from frame 2048",
            ],
            lines);
    }

    /// <summary>The sources match the file: one per instrument and one per sample, with the names the builder gave them.</summary>
    [Fact]
    public void SourcesMatchTheFile()
    {
        DmfConversion.Result result = Convert();

        Assert.Equal(result.File.Instruments.Count, result.Built.Instruments.Count);
        Assert.Equal(result.File.Samples.Count, result.Built.Samples.Count);
        Assert.Equal([VoiceKind.Fm, VoiceKind.PsgNoise], result.Built.Instruments.Select(i => i.Kind));
        Assert.All(result.Built.Samples, s => Assert.Equal(Drum - 1, s.Sample));
    }

    /// <summary>A song with nothing to play says so in the summary and lists no instruments; one copy needs no plural.</summary>
    [Fact]
    public void AnEmptySongListsNothing()
    {
        DmfConversion.Result result = DmfConversion.Convert(new ModBuilder().Build());

        Assert.Equal(["Output (DMF): 0 FM instruments, 0 PSG noise instruments, 0 DAC samples, 0 KB PCM"], DmfReport.Lines(result.Built, result.Song));
    }

    /// <summary>Each line is logged as a property at <c>Information</c>, so braces in names are printed, not read as holes.</summary>
    [Fact]
    public void LinesAreLoggedAsTheyAre()
    {
        DmfConversion.Result result = Convert();
        CollectingSink sink = new();

        DmfReport.Log(sink.CreateLogger(), result.Built, result.Song);

        Assert.Equal(DmfReport.Lines(result.Built, result.Song), sink.Messages(LogEventLevel.Information));
    }
}
