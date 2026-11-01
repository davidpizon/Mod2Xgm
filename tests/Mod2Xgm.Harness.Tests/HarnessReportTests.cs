using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;
using Mod2Xgm.Optimizer;
using Mod2Xgm.Scoring;

namespace Mod2Xgm.Harness.Tests;

/// <summary>Tests for <see cref="HarnessReport"/>: what the CSV and markdown say.</summary>
public sealed class HarnessReportTests
{
    private static readonly ScoreReport Score = new(4.5, 30.25, 0.9, 0.8, 0.85, 0.012, 0.1, -0.5, 4.95);

    private static readonly ScoreReport Clip = new(3.25, 20.5, 1, 1, 1, 0.002, 0.1, 0, 3.25);

    private static readonly BalanceResult Balance = new(
        new BalanceReport(4.25, 3.5, 4, -1.5, [new InstrumentBalance(1, 2.5, 3, 200, -20), new InstrumentBalance(3, -6.25, 6.25, 100, -25), new InstrumentBalance(4, 0, 0, 0, -150)]),
        [new StemInfo(1, "bass, low", "FM"), new StemInfo(3, "snare | 2", "DAC+noise"), new StemInfo(4, "silent", string.Empty)],
        0.01,
        0.75,
        [2, -5.5, null]);

    private static readonly LoudnessComparison SongLoudness = new(new Loudness(-14.25, 6.5, -0.5), new Loudness(-16.5, 4, 0.25));

    private static readonly LoudnessComparison ClipLoudness = new(new Loudness(-15, 3, -1), new Loudness(null, 0, null));

    private static readonly SongResult[] Results =
    [
        new("alpha", new DurationCheck(60.05, 60.02, 60.0, 0.02, null, false), 3, [new("furnace", Score, null, Clip, Balance, SongLoudness, ClipLoudness), new("deflemask", null, "stale, \"old\"")], null),
        new("beta", null, 0, [], "Not a MOD | broken"),
        new("gamma", new DurationCheck(61.1, 61, 60, 0.02, new RowTiming(3, 16, 30.6, 30.5, 30.0), false), 1, [], null),
        new("delta", new DurationCheck(93.38, 93.385, 95, 0.026, null, true), 31, [], null),
        new("epsilon", new DurationCheck(10, 10, 10.0000000001, 0.02, null, false), 0, [], null),
    ];

    /// <summary>One CSV line per song and renderer, numbers in invariant culture, and a remark with a comma and quotes quoted.</summary>
    [Fact]
    public void ScoresCsvHasOneLinePerRendering()
    {
        string[] lines = HarnessReport.ScoresCsv(Results).Split('\n');

        Assert.Equal(HarnessReport.ScoresHeader, lines[0]);
        Assert.Equal("alpha,furnace,4.95,4.5,30.25,0.9,0.8,0.85,12,100,-0.5,", lines[1]);
        Assert.Equal("alpha,deflemask,,,,,,,,,,\"stale, \"\"old\"\"\"", lines[2]);
        Assert.Equal(string.Empty, lines[3]);
    }

    /// <summary>The clip CSV has the same columns, with each rendering's clip scores (empty where there are none).</summary>
    [Fact]
    public void ClipScoresCsvHasTheClipScores()
    {
        string[] lines = HarnessReport.ClipScoresCsv(Results).Split('\n');

        Assert.Equal(HarnessReport.ScoresHeader, lines[0]);
        Assert.Equal("alpha,furnace,3.25,3.25,20.5,1,1,1,2,100,0,", lines[1]);
        Assert.Equal("alpha,deflemask,,,,,,,,,,\"stale, \"\"old\"\"\"", lines[2]);
    }

    /// <summary>The balance CSV has one line per rendering with a balance; the instruments CSV one line per stem.</summary>
    [Fact]
    public void BalanceCsvsHaveEveryRenderingAndStem()
    {
        string[] lines = HarnessReport.BalanceCsv(Results).Split('\n');
        string[] instruments = HarnessReport.BalanceInstrumentsCsv(Results).Split('\n');

        Assert.Equal([HarnessReport.BalanceHeader, "alpha,furnace,4.25,3.5,4,-1.5,3,0.01,0.75", string.Empty], lines);
        Assert.Equal(HarnessReport.BalanceInstrumentsHeader, instruments[0]);
        Assert.Equal("alpha,furnace,1,\"bass, low\",FM,2.5,3,200,-20,2", instruments[1]);
        Assert.Equal("alpha,furnace,3,snare | 2,DAC+noise,-6.25,6.25,100,-25,-5.5", instruments[2]);
        Assert.Equal("alpha,furnace,4,silent,,0,0,0,-150,", instruments[3]);
        Assert.Equal(5, instruments.Length);
    }

    /// <summary>The loudness CSV has a song line and a clip line per rendering that has them, and empty cells for a silent clip.</summary>
    [Fact]
    public void LoudnessCsvHasTheSongAndTheClip()
    {
        string[] lines = HarnessReport.LoudnessCsv(Results).Split('\n');

        Assert.Equal(
            [
                HarnessReport.LoudnessHeader,
                "alpha,furnace,song,-14.25,-16.5,-2.25,6.5,4,-0.5,0.25",
                "alpha,furnace,clip,-15,,,3,0,-1,",
                string.Empty,
            ],
            lines);
    }

    /// <summary>One CSV line per song: both timings, the check, the first row off, the F00 end, and a value that rounds to zero without its minus sign.</summary>
    [Fact]
    public void DurationsCsvHasOneLinePerSong()
    {
        string[] lines = HarnessReport.DurationsCsv(Results).Split('\n');

        Assert.Equal(HarnessReport.DurationsHeader, lines[0]);
        Assert.Equal("alpha,60.05,60.02,60,0.02,0.03,0.02,yes,,no,3,", lines[1]);
        Assert.Equal("beta,,,,,,,,,,0,Not a MOD | broken", lines[2]);
        Assert.Equal("gamma,61.1,61,60,1,0.1,0.02,no,3:16,no,1,", lines[3]);
        Assert.Equal("delta,93.38,93.385,95,-1.615,-0.005,0.026,yes,,yes,31,", lines[4]);
        Assert.Equal("epsilon,10,10,10,0,0,0.02,yes,,no,0,", lines[5]);
    }

    /// <summary>The markdown has the scores, the clip scores, the average per renderer and the timeline table, with table-safe text.</summary>
    [Fact]
    public void MarkdownHasEverySection()
    {
        string md = HarnessReport.Markdown(Results, "libopenmpt 0.8.9", "0.1.0", 44_100);

        Assert.Contains("Reference: libopenmpt 0.8.9. Converter: Mod2Xgm 0.1.0. Scored mono at 44100 Hz.", md, StringComparison.Ordinal);
        Assert.Contains("| `alpha` | furnace | 4.95 | 4.50 | 30.25 | 0.85 | 12.0 | 100 | -0.50 |  |", md, StringComparison.Ordinal);
        Assert.Contains("| `alpha` | deflemask |  |  |  |  |  |  |  | stale, \"old\" |", md, StringComparison.Ordinal);
        Assert.Contains("## First seconds only (listening clips)", md, StringComparison.Ordinal);
        Assert.Contains("| `alpha` | furnace | 3.25 | 3.25 | 20.50 | 1.00 |", md, StringComparison.Ordinal);
        Assert.Contains("## Instrument balance (listening clips)", md, StringComparison.Ordinal);
        Assert.Contains("| `alpha` | furnace | 4.25 | -1.5 | 3 | 3 snare \\| 2 (DAC+noise) | -6.3 |", md, StringComparison.Ordinal);
        Assert.Contains("## Loudness (EBU R128)", md, StringComparison.Ordinal);
        Assert.Contains("| `alpha` | furnace | -14.3 | -16.5 | -2.3 | 6.5 | 4.0 | +0.3 |", md, StringComparison.Ordinal);
        Assert.Contains("| furnace | 1 | 4.95 | 4.50 | 30.25 | 0.85 |", md, StringComparison.Ordinal);
        Assert.DoesNotContain("| deflemask | 1", md, StringComparison.Ordinal);
        Assert.Contains("| `alpha` | 60.050 | 60.020 | 60.000 | 0.020 | 0.030 | 0.020 | ✅ |  | 3 |  |", md, StringComparison.Ordinal);
        Assert.Contains("| `beta` |  |  |  |  |  |  | – |  | 0 | Not a MOD \\| broken |", md, StringComparison.Ordinal);
        Assert.Contains("| `gamma` | 61.100 | 61.000 | 60.000 | 1.000 | 0.100 | 0.020 | ❌ | 3:16 | 1 |  |", md, StringComparison.Ordinal);
        Assert.Contains("| `delta` | 93.380 | 93.385 | 95.000 | -1.615 | -0.005 | 0.026 | ✅ (stops at F00; end not compared) |  | 31 |  |", md, StringComparison.Ordinal);
        Assert.Contains("| `epsilon` | 10.000 | 10.000 | 10.000 | 0.000 | 0.000 | 0.020 | ✅ |  | 0 |  |", md, StringComparison.Ordinal);
        Assert.Contains("3 of 4 songs within one tick; 1 songs failed.", md, StringComparison.Ordinal);
    }

    /// <summary>The in-app renderer's section names the worst song and says whether every song is within the bar.</summary>
    [Fact]
    public void MarkdownChecksTheModRendererAgainstTheBar()
    {
        static SongResult Song(string name, double melDb) =>
            new(name, null, 0, [new(ModRendererCheck.RendererName, Score with { MelDistanceDb = melDb }, null)], null);

        string within = HarnessReport.Markdown([Song("alpha", 0.5), Song("beta", 1.0)], "r", "v", 8000);
        string over = HarnessReport.Markdown([Song("alpha", 0.5), Song("beta", 1.5)], "r", "v", 8000);

        Assert.Contains("## In-app MOD renderer against the reference (plan T7.1)", within, StringComparison.Ordinal);
        Assert.Contains("Bar: whole-song log-mel ≤ 1.25 dB on every song. Worst: `beta`, 1.00 dB. ✅ Every song is within the bar.", within, StringComparison.Ordinal);
        Assert.Contains("Worst: `beta`, 1.50 dB. ❌ 1 songs are over the bar.", over, StringComparison.Ordinal);
        Assert.DoesNotContain("In-app MOD renderer", HarnessReport.Markdown(Results, "r", "v", 8000), StringComparison.Ordinal);
    }

    /// <summary>Without clip scores there is no clip section, and without loudness no loudness section.</summary>
    [Fact]
    public void MarkdownLeavesOutTheClipSectionWithoutClips()
    {
        SongResult song = new("alpha", null, 0, [new("furnace", Score, null)], null);

        Assert.DoesNotContain("First seconds", HarnessReport.Markdown([song], "r", "v", 8000), StringComparison.Ordinal);
        Assert.DoesNotContain("Instrument balance", HarnessReport.Markdown([song], "r", "v", 8000), StringComparison.Ordinal);
        Assert.DoesNotContain("Loudness (EBU R128)", HarnessReport.Markdown([song], "r", "v", 8000), StringComparison.Ordinal);
    }

    /// <summary>The sample-analysis CSV has one line per song and sample, with the region lists separated by spaces (plan T8.1).</summary>
    [Fact]
    public void SampleAnalysisCsvHasOneLinePerSample()
    {
        AmplitudeEnvelope held = new(8287, 0.005, [-9, -9], -9.034, 0.0049, null, 0);
        AmplitudeEnvelope hit = new(16574, 0.005, [-3, -30], -3, 0, 0.1234, null);
        RegionSpectrum spectrum = new(0.01, [], null);
        SampleAnalysis bass = new(
            0,
            "bass, low",
            SampleKind.Tone,
            0.0004,
            0.98765,
            0.01,
            new PitchStability(64.0001, 1, 2.25, -0.04),
            held,
            new LoopAnalysis(0, 64, 0, 1),
            [new PitchRegion(8287, 8287, 8287.4, 3, 129.49, spectrum), new PitchRegion(16574, 20000, 16574.8, 1, 258.98, spectrum)]);
        SampleAnalysis snare = new(4, "snare", SampleKind.Drum, 0.95, 0.1, 0.9, null, hit, null, [new PitchRegion(16574, 16574, 16574, 0, null, spectrum)]);
        SongResult[] results = [Results[0] with { Samples = [bass, snare] }, Results[1]];

        string[] lines = HarnessReport.SampleAnalysisCsv(results).Split('\n');

        Assert.Equal(HarnessReport.SampleAnalysisHeader, lines[0]);
        Assert.Equal("alpha,1,\"bass, low\",tone,0,0.988,0.01,64,1,2.3,0,-9.03,4.9,,0,64,1,2,8287 16575,129.5 259,3 1", lines[1]);
        Assert.Equal("alpha,5,snare,drum,0.95,0.1,0.9,,,,,-3,0,123.4,,,,1,16574,,0", lines[2]);
        Assert.Equal(string.Empty, lines[3]);
    }

    /// <summary>The FM fit CSV has one line per song, sample and region: library, both searches, the best and its settings (plan T8.2).</summary>
    [Fact]
    public void FmFitCsvHasOneLinePerRegion()
    {
        FmFit library = new(FmPatchLibrary.Lead, null, FmFitSource.Library, new NoteScore(7.1234, 90.5, 9.5));
        FmFit exhaustive = new(FmPatchLibrary.Organ with { Name = "Fit 1.1" }, null, FmFitSource.Exhaustive, new NoteScore(3.5, 40.25, 4.25));
        FmFit genetic = new(FmPatchLibrary.Sine with { Fms = 2 }, 5, FmFitSource.Genetic, new NoteScore(3, 35, 4));
        FmRegionFit bass = new(0, library, exhaustive, genetic, genetic, exhaustive, 1234, TimeSpan.FromSeconds(12.34));
        FmRegionFit lib = new(1, library, null, null, library, library, 11, TimeSpan.FromSeconds(0.5));
        SongResult[] results = [Results[0] with { FmFits = [new FmSampleFit(0, "bass, low", [bass, lib])] }, Results[1]];

        string[] lines = HarnessReport.FmFitCsv(results).Split('\n');

        Assert.Equal(HarnessReport.FmFitHeader, lines[0]);
        Assert.Equal("alpha,1,\"bass, low\",1,Lead,7.123,3.5,3,genetic,3,35,4,3.5,5,\"ALG 7 FB 0 MULT 1 1 1 1, LFO 5 FMS 2 AMS 0\",yes,1234,12.3", lines[1]);
        Assert.Equal("alpha,1,\"bass, low\",2,Lead,7.123,,,library,7.123,90.5,9.5,7.123,,\"ALG 4 FB 5 MULT 1 1 2 2, no LFO\",no,11,0.5", lines[2]);
        Assert.Equal(string.Empty, lines[3]);
        Assert.Equal(HarnessReport.FmFitCsv(results), HarnessReport.FmFitCsv(results));
    }

    /// <summary>The PSG fit CSV has one line per song, sample, region and try: settings, pitch, channels, room, distances, and which is best (plan T8.3).</summary>
    [Fact]
    public void PsgFitCsvHasOneLinePerTry()
    {
        int[] loud = [0];
        PsgCandidate tone = new(new PsgSetting(PsgSound.Tone, 0, 254, loud), new NoteScore(2.5, 20.25, 3.125), 1.25, PsgRoom.SampleTooOften);
        PsgCandidate buzz = new(new PsgSetting(PsgSound.PeriodicNoise, PsgSetting.Tone3Rate, 16, loud), new NoteScore(4, 30, null), -3, PsgRoom.Fits);
        PsgCandidate hiss = new(new PsgSetting(PsgSound.WhiteNoise, 1, 0, loud), new NoteScore(9, 50, null), null, PsgRoom.Fits);
        PsgRegionFit region = new(0, [tone, buzz, hiss], 30, TimeSpan.FromSeconds(1));
        SongResult[] results = [Results[0] with { PsgFits = [new PsgSampleFit(2, "lead, hi", 4, [region])] }, Results[1]];

        string[] lines = HarnessReport.PsgFitCsv(results).Split('\n');

        Assert.Equal(HarnessReport.PsgFitHeader, lines[0]);
        Assert.Equal("alpha,3,\"lead, hi\",1,4,tone,,254,440.4,1.3,1,sample too often,2.5,20.25,3.125,yes,no", lines[1]);
        Assert.Equal("alpha,3,\"lead, hi\",1,4,periodic,3,16,436.96,-3,2,fits,4,30,,no,yes", lines[2]);
        Assert.Equal("alpha,3,\"lead, hi\",1,4,white,1,,,,1,fits,9,50,,no,no", lines[3]);
        Assert.Equal(string.Empty, lines[4]);
    }

    /// <summary>The same results give the same files: the report holds no time or machine detail (plan T6.3).</summary>
    [Fact]
    public void ReportsAreTheSameEachTime()
    {
        Assert.Equal(HarnessReport.Markdown(Results, "r", "v", 8000), HarnessReport.Markdown(Results, "r", "v", 8000));
        Assert.Equal(HarnessReport.ScoresCsv(Results), HarnessReport.ScoresCsv(Results));
        Assert.Equal(HarnessReport.DurationsCsv(Results), HarnessReport.DurationsCsv(Results));
        Assert.Equal(HarnessReport.LoudnessCsv(Results), HarnessReport.LoudnessCsv(Results));
        Assert.Equal(HarnessReport.SampleAnalysisCsv(Results), HarnessReport.SampleAnalysisCsv(Results));
    }
}
