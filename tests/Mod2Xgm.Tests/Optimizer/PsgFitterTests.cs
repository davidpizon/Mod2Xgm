using Mod2Xgm.Analysis;
using Mod2Xgm.Model;
using Mod2Xgm.Optimizer;
using Mod2Xgm.Render.Psg;
using Serilog.Events;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="PsgFitter"/>: PSG tries for made-up samples, scored and marked for room.</summary>
public sealed class PsgFitterTests
{
    /// <summary>A song with room for anything: one note at a time.</summary>
    private static readonly SongVoices Quiet = new(1, [1, 1, 1]);

    /// <summary>The square sample's tries, made once for the tests that read them.</summary>
    private static readonly Lazy<PsgSampleFit> SquareFit = new(() => PsgFitter.Fit(FmTargets.Analyze(FmTargets.Square), Quiet));

    /// <summary>A square wave is closest as a PSG tone, at the note's pitch to within a few cents.</summary>
    [Fact]
    public void ASquareIsBestAsATone()
    {
        PsgRegionFit region = Assert.Single(SquareFit.Value.Regions);

        Assert.Equal(PsgSound.Tone, region.Best.Setting.Sound);
        Assert.Same(region.Tone, region.Best);
        Assert.InRange(region.Best.CentsOff!.Value, -10, 10);
        Assert.True(region.Best.Score.MelDb < region.BestNoise.Score.MelDb);
        Assert.All(region.Best.Setting.Volumes, v => Assert.Equal(0, v));
        Assert.Same(region.Best, region.BestThatFits);
    }

    /// <summary>Every region gets the tone, the noise at every fixed rate in both modes, the tuned hiss and the tuned buzz.</summary>
    [Fact]
    public void EveryTryIsMade()
    {
        PsgRegionFit region = SquareFit.Value.Regions[0];

        Assert.Equal(9, region.Candidates.Count);
        Assert.Equal(1, region.Candidates.Count(c => c.Setting.Sound == PsgSound.Tone));
        Assert.Equal(4, region.Candidates.Count(c => c.Setting.Sound == PsgSound.WhiteNoise));
        Assert.Equal(4, region.Candidates.Count(c => c.Setting.Sound == PsgSound.PeriodicNoise));
        Assert.Equal(2, region.Candidates.Count(c => c.Setting.Channels == 2));
        Assert.True(region.Renders > region.Candidates.Count);
        Assert.All(region.Candidates.Where(c => c.Setting.Sound == PsgSound.WhiteNoise), c => Assert.Null(c.CentsOff));

        PsgCandidate buzz = region.Candidates.Single(c => c.Setting is { Sound: PsgSound.PeriodicNoise, NoiseRate: PsgSetting.Tone3Rate });
        Assert.InRange(buzz.CentsOff!.Value, -50, 50);
    }

    /// <summary>Hiss is closest as PSG noise, and fades with the sample, a step every 1/60 s.</summary>
    [Fact]
    public void AFadingHissIsBestAsNoise()
    {
        Random random = new(7);
        float[] data = [.. Enumerable.Range(0, 8000).Select(i => (float)((random.NextDouble() - 0.5) * Math.Exp(-i / 2000.0)))];
        SampleAnalysis analysis = SampleAnalyzer.Analyze(1, new Sample("hat", data, null), [FmTargets.C2]);

        PsgRegionFit region = Assert.Single(PsgFitter.Fit(analysis, Quiet).Regions);

        Assert.Equal(PsgSound.WhiteNoise, region.Best.Setting.Sound);
        IReadOnlyList<int> volumes = region.Best.Setting.Volumes;
        Assert.Equal(0, volumes.Take(3).Min());
        Assert.True(volumes[30] > volumes[2]);
    }

    /// <summary>A note below a tone channel's range gets no tone, but the buzz tuned to it.</summary>
    [Fact]
    public void ANoteTooLowForAToneGetsTheBuzz()
    {
        PsgSetting low = new(PsgSound.PeriodicNoise, PsgSetting.Tone3Rate, PsgSetting.NearestPeriod(50 * 16), [.. Enumerable.Repeat(0, 60)]);
        float[] sound = PsgNoteRenderer.Render(low, SampleAnalyzer.RegionSeconds);
        NoteTarget target = new(50, 50, SampleAnalyzer.Spectrum(sound, 50));

        PsgRegionFit region = PsgFitter.FitRegion(0, target, 1, 4);

        Assert.Null(region.Tone);
        Assert.Equal(8, region.Candidates.Count);
        Assert.Equal(low.Period, region.Best.Setting.Period);
        Assert.Equal(PsgSound.PeriodicNoise, region.Best.Setting.Sound);
        Assert.True(region.Best.Score.MelDb < 1);
    }

    /// <summary>The tries are the same every run.</summary>
    [Fact]
    public void FitsAreRepeatable()
    {
        PsgSampleFit again = PsgFitter.Fit(FmTargets.Analyze(FmTargets.Square), Quiet);

        Assert.Equal(
            SquareFit.Value.Regions[0].Candidates.Select(c => (c.Setting.Period, c.Score.MelDb)),
            again.Regions[0].Candidates.Select(c => (c.Setting.Period, c.Score.MelDb)));
    }

    /// <summary>
    /// The room marks: 3 tone channels and 1 noise channel for the sample's notes at once; a try that borrows tone
    /// channel 3 also needs a Genesis channel to spare.
    /// </summary>
    [Fact]
    public void TriesAreMarkedForRoom()
    {
        int[] loud = [0];
        PsgSetting tone = new(PsgSound.Tone, 0, 100, loud);
        PsgSetting hiss = new(PsgSound.WhiteNoise, 0, 0, loud);
        PsgSetting tuned = new(PsgSound.WhiteNoise, PsgSetting.Tone3Rate, 100, loud);

        Assert.Equal(PsgRoom.Fits, PsgFitter.Room(tone, 3, 10));
        Assert.Equal(PsgRoom.SampleTooOften, PsgFitter.Room(tone, 4, 4));
        Assert.Equal(PsgRoom.Fits, PsgFitter.Room(hiss, 1, 10));
        Assert.Equal(PsgRoom.SampleTooOften, PsgFitter.Room(hiss, 2, 2));
        Assert.Equal(PsgRoom.Fits, PsgFitter.Room(tuned, 1, 9));
        Assert.Equal(PsgRoom.SongTooFull, PsgFitter.Room(tuned, 1, 10));
        Assert.Equal(PsgRoom.SampleTooOften, PsgFitter.Room(tuned, 2, 4));
    }

    /// <summary>A sample the song sounds on two channels at once keeps its noise tries, marked as having no room.</summary>
    [Fact]
    public void TriesWithoutRoomAreKept()
    {
        PsgSampleFit fit = PsgFitter.Fit(FmTargets.Analyze(FmTargets.Square), new SongVoices(2, [2]));

        PsgRegionFit region = fit.Regions[0];
        Assert.Equal(2, fit.MostAtOnce);
        Assert.Equal(9, region.Candidates.Count);
        Assert.All(region.Candidates.Where(c => c.Setting.UsesNoise), c => Assert.Equal(PsgRoom.SampleTooOften, c.Room));
        Assert.Equal(PsgSound.Tone, region.BestThatFits!.Setting.Sound);
    }

    /// <summary>The volumes follow the loudness: the loudest step at 0, quieter steps higher, silence off.</summary>
    [Fact]
    public void VolumesFollowTheLoudness()
    {
        float[] fading = [.. Enumerable.Range(0, SampleAnalyzer.AnalysisRateHz).Select(i => (float)(Math.Sin(2 * Math.PI * 440 * i / SampleAnalyzer.AnalysisRateHz) * Math.Exp(-i / 4000.0)))];

        int[] volumes = PsgFitter.Volumes(SampleAnalyzer.Spectrum(fading, 440));

        Assert.Equal(PsgSetting.StepsPerSecond, volumes.Length);
        Assert.Equal(0, volumes.Min());
        Assert.True(volumes[40] > volumes[10]);
        Assert.Equal(PsgLevels.Off, volumes[^1]);
        Assert.All(PsgFitter.Volumes(new RegionSpectrum(0.01, [], null)), v => Assert.Equal(PsgLevels.Off, v));
    }

    /// <summary>A silent sample gets no tries.</summary>
    [Fact]
    public void ASilentSampleGetsNoTries()
    {
        SampleAnalysis silent = SampleAnalyzer.Analyze(0, new Sample("silent", new float[256], null), [FmTargets.C2]);

        Assert.Empty(PsgFitter.Fit(silent, Quiet).Regions);
    }

    /// <summary>The report logs one Debug line per region with the tone, the best noise and the best that fits.</summary>
    [Fact]
    public void TheReportLogsEveryRegion()
    {
        CollectingSink sink = new();

        PsgFitReport.Log(sink.CreateLogger(), [SquareFit.Value, new PsgSampleFit(5, "empty", 0, [])]);

        string line = Assert.Single(sink.Messages(LogEventLevel.Debug));
        Assert.StartsWith("Sample 1 \"square\", region 1: PSG tone tone, period ", line, StringComparison.Ordinal);
        Assert.Contains(" cents off: ", line, StringComparison.Ordinal);
        Assert.Contains("; noise ", line, StringComparison.Ordinal);
        Assert.Contains("; best with room tone", line, StringComparison.Ordinal);
        Assert.Contains(" notes rendered in ", line, StringComparison.Ordinal);
    }

    /// <summary>Settings and room marks read as words.</summary>
    [Fact]
    public void SettingsReadAsText()
    {
        int[] loud = [0];

        Assert.Equal("tone, period 254", PsgFitReport.Settings(new PsgSetting(PsgSound.Tone, 0, 254, loud)));
        Assert.Equal("white noise, rate 2", PsgFitReport.Settings(new PsgSetting(PsgSound.WhiteNoise, 2, 0, loud)));
        Assert.Equal("periodic noise, rate 3, period 40", PsgFitReport.Settings(new PsgSetting(PsgSound.PeriodicNoise, 3, 40, loud)));
        Assert.Equal("fits", PsgFitReport.RoomName(PsgRoom.Fits));
        Assert.Equal("sample too often", PsgFitReport.RoomName(PsgRoom.SampleTooOften));
        Assert.Equal("song too full", PsgFitReport.RoomName(PsgRoom.SongTooFull));
        Assert.Equal("periodic", PsgFitReport.SoundName(PsgSound.PeriodicNoise));
    }
}
