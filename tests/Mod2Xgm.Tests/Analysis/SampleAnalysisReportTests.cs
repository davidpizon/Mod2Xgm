using Mod2Xgm.Analysis;
using Mod2Xgm.Output;
using Serilog.Events;

namespace Mod2Xgm.Tests.Analysis;

/// <summary>Tests for <see cref="SampleAnalysisReport"/>: the two Debug lines per sample that <c>--verbose</c> shows.</summary>
public sealed class SampleAnalysisReportTests
{
    /// <summary>An empty spectrum, for regions whose spectrum the report doesn't show.</summary>
    private static readonly RegionSpectrum Spectrum = new(0.01, [], [new float[12]]);

    private readonly CollectingSink _sink = new();

    /// <summary>A looped, pitched sample logs its scores, pitch, envelope and loop, then each region with its harmonics.</summary>
    [Fact]
    public void Log_WithLoopedTone_LogsEverything()
    {
        // Act
        SampleAnalysisReport.Log(_sink.CreateLogger(), [LoopedTone()]);

        // Assert
        Assert.Equal(
            [
                "Sample 1 \"bass\": plucked tone (percussive 0.75, tonal 0.98, noisy 0.02); a cycle of 64.0 frames, 97 % voiced, ±3 cents, drifting -12 cents; "
                    + "peak -6.0 dBFS, attack 5 ms, 20 dB down after 250 ms, holds at -20.5 dB (at 8287 Hz); loops 64 frames from 100 at -20.5 dB (1.0 cycles)",
                "Sample 1 \"bass\": 2 pitch regions: 8287 Hz (4 notes, analyzed at 8287 Hz, sounding 129.5 Hz with 12 harmonics); "
                    + "16574–20000 Hz (2 notes, analyzed at 18000 Hz, sounding 281.2 Hz with 12 harmonics)",
            ],
            _sink.Messages(LogEventLevel.Debug));
    }

    /// <summary>The first line carries the sample's facts as an <c>analysis</c> row.</summary>
    [Fact]
    public void Log_WithLoopedTone_AttachesAnalysisRow()
    {
        // Act
        SampleAnalysisReport.Log(_sink.CreateLogger(), [LoopedTone()]);

        // Assert
        StructureValue row = Assert.IsType<StructureValue>(ReportRow.Find(_sink.Events[0]));
        Assert.Equal("analysis", row.TypeTag);
        Assert.Equal(1, row.Get("n"));
        Assert.Equal("plucked tone", row.Get("kind"));
        Assert.Equal(64.0, row.Get("period_frames"));
        Assert.Equal(250.0, row.Get("decay_ms"));
        Assert.Equal(64, row.Get("loop_length"));
        Assert.Equal(2, row.Get("regions"));
    }

    /// <summary>The second line carries the regions as a <c>part</c> row.</summary>
    [Fact]
    public void Log_WithLoopedTone_AttachesPartRow()
    {
        // Act
        SampleAnalysisReport.Log(_sink.CreateLogger(), [LoopedTone()]);

        // Assert
        Assert.Equal("part", ReportRow.Find(_sink.Events[1])?.TypeTag);
    }

    /// <summary>A one-shot without a pitch says so, and its region has no fundamental.</summary>
    [Fact]
    public void Log_WithUnpitchedOneShot_SaysSo()
    {
        // Act
        SampleAnalysisReport.Log(_sink.CreateLogger(), [UnpitchedDrum()]);

        // Assert
        string[] lines = [.. _sink.Messages(LogEventLevel.Debug)];
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("; no clear pitch; peak -3.0 dBFS, attack 0 ms, never 20 dB down (at 8287 Hz); no loop", lines[0], StringComparison.Ordinal);
        Assert.Equal("Sample 4 \"snare\": 1 pitch regions: 8287 Hz (0 notes, analyzed at 8287 Hz)", lines[1]);
    }

    /// <summary>The row of a one-shot without a pitch leaves the missing facts out.</summary>
    [Fact]
    public void Log_WithUnpitchedOneShot_LeavesMissingFactsOutOfRow()
    {
        // Act
        SampleAnalysisReport.Log(_sink.CreateLogger(), [UnpitchedDrum()]);

        // Assert
        StructureValue row = Assert.IsType<StructureValue>(ReportRow.Find(_sink.Events[0]));
        Assert.Null(row.Get("period_frames"));
        Assert.Null(row.Get("decay_ms"));
        Assert.Null(row.Get("loop_start"));
    }

    /// <summary>Every label has its own words.</summary>
    [Fact]
    public void KindName_ForEveryKind_ReturnsItsWords()
    {
        // Act
        IEnumerable<string> names = Enum.GetValues<SampleKind>().Select(SampleAnalysisReport.KindName);

        // Assert
        Assert.Equal(["silent", "tone", "plucked tone", "drum", "noise", "unclear"], names);
    }

    /// <summary>A looped, pitched sample with two pitch regions.</summary>
    private static SampleAnalysis LoopedTone() => new(
        0,
        "bass",
        SampleKind.PluckedTone,
        0.75,
        0.98,
        0.02,
        new PitchStability(64, 0.97, 3.4, -12),
        new AmplitudeEnvelope(8287, 0.005, [-6], -6.04, 0.005, 0.25, -20.5),
        new LoopAnalysis(100, 64, -20.5, 1),
        [new PitchRegion(8287, 8287, 8287, 4, 129.5, Spectrum), new PitchRegion(16574, 20000, 18000, 2, 281.2, Spectrum)]);

    /// <summary>A one-shot without a pitch.</summary>
    private static SampleAnalysis UnpitchedDrum() => new(
        3,
        "snare",
        SampleKind.Drum,
        1,
        0.1,
        0.9,
        null,
        new AmplitudeEnvelope(8287, 0.005, [-3], -3, 0, null, null),
        null,
        [new PitchRegion(8287, 8287, 8287, 0, null, new RegionSpectrum(0.01, [], null))]);
}
