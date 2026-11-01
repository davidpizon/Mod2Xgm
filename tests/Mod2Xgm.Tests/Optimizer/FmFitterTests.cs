using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;
using Mod2Xgm.Optimizer;
using Serilog.Events;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="FmFitter"/> and <see cref="FmFitReport"/>: the whole fit of a sample, per pitch region.</summary>
public sealed class FmFitterTests
{
    /// <summary>A small budget, so a fit takes seconds; the harness fits without one.</summary>
    private static readonly FmFitOptions Small = new(MaxRenders: 60);

    /// <summary>The saw fitted once with <see cref="Small"/>, shared by the tests that only read it.</summary>
    private static readonly Lazy<FmSampleFit> SawFit = new(() => FmFitter.Fit(FmTargets.Analyze(FmTargets.Saw), Small));

    /// <summary>
    /// Plan T8.2's acceptance on a made-up sample: a fitted patch sounds closer to a sawtooth than the closest built-in
    /// patch. The best fit is never worse than the library's, and its twin uses no LFO.
    /// </summary>
    [Fact]
    public void FittedPatchesBeatTheLibrary()
    {
        FmRegionFit region = Assert.Single(SawFit.Value.Regions);

        Assert.True(region.FittedBeatsLibrary, $"library {region.Library.Score.MelDb} dB, fitted {region.Fitted?.Score.MelDb} dB");
        Assert.NotNull(region.Exhaustive);
        Assert.NotNull(region.Genetic);
        Assert.True(region.Best.Score.MelDb <= region.Library.Score.MelDb);
        Assert.Equal(FmFitSource.Library, region.Library.Source);
        Assert.Contains(region.Library.Patch, FmPatchLibrary.All);
        Assert.Null(region.NoLfo.LfoRate);
        Assert.False(region.NoLfo.Patch.UsesLfo);
        Assert.True(region.Renders > 0);
    }

    /// <summary>Fitted patches are named after the sample and region; a fit is the same every time.</summary>
    [Fact]
    public void FitsAreNamedAndRepeatable()
    {
        FmSampleFit again = FmFitter.Fit(FmTargets.Analyze(FmTargets.Saw), Small);
        FmRegionFit first = SawFit.Value.Regions[0];
        FmRegionFit second = again.Regions[0];

        Assert.Equal("saw", again.Name);
        Assert.Equal("Fit 1.1", first.Exhaustive!.Patch.Name);
        Assert.Equal(FmEvaluator.Key(first.Best.Patch, first.Best.LfoRate), FmEvaluator.Key(second.Best.Patch, second.Best.LfoRate));
        Assert.Equal(first.Best.Score, second.Best.Score);
        Assert.Equal(first.Renders, second.Renders);
    }

    /// <summary>One region per pitch region: a sample played two octaves apart gets two fits; a silent sample none.</summary>
    [Fact]
    public void EveryRegionWithSoundIsFitted()
    {
        FmFitOptions exhaustiveOnly = Small with { Genetic = false, Lfo = false, SsgEg = false, MaxRenders = 20 };
        SampleAnalysis twoRegions = SampleAnalyzer.Analyze(2, FmTargets.Saw, [FmTargets.C2, FmTargets.C2 * 4]);
        SampleAnalysis silent = SampleAnalyzer.Analyze(3, new Sample("quiet", new float[64], new SampleLoop(0, 64)), [FmTargets.C2]);

        FmSampleFit fit = FmFitter.Fit(twoRegions, exhaustiveOnly);

        Assert.Equal([0, 1], fit.Regions.Select(r => r.Region));
        Assert.Equal("Fit 3.2", fit.Regions[1].Exhaustive!.Patch.Name);
        Assert.All(fit.Regions, r => Assert.Null(r.Genetic));
        Assert.Empty(FmFitter.Fit(silent, exhaustiveOnly).Regions);
    }

    /// <summary>With neither search, the library patch is the best and there is no fitted patch to beat it.</summary>
    [Fact]
    public void WithoutSearchesTheLibraryWins()
    {
        FmRegionFit region = FmFitter.FitRegion(0, FmTargets.Of(FmTargets.Saw), new FmFitOptions(Exhaustive: false, Genetic: false), "Fit");

        Assert.Null(region.Fitted);
        Assert.False(region.FittedBeatsLibrary);
        Assert.Same(region.Library, region.Best);
        Assert.Same(region.Library, region.NoLfo);
        Assert.Equal(FmPatchLibrary.All.Count, region.Renders);
    }

    /// <summary>The report logs one Debug line per region with the library's, both searches' and the best's distances.</summary>
    [Fact]
    public void TheReportLogsEveryRegion()
    {
        CollectingSink sink = new();

        FmFitReport.Log(sink.CreateLogger(), [SawFit.Value, new FmSampleFit(5, "empty", [])]);

        string line = Assert.Single(sink.Messages(LogEventLevel.Debug));
        Assert.StartsWith("Sample 1 \"saw\", region 1: library ", line, StringComparison.Ordinal);
        Assert.Contains("; exhaustive ", line, StringComparison.Ordinal);
        Assert.Contains(" dB; genetic ", line, StringComparison.Ordinal);
        Assert.Contains("ALG ", line, StringComparison.Ordinal);
        Assert.Contains(" notes rendered in ", line, StringComparison.Ordinal);
    }

    /// <summary>A fit's settings read as text: structure, multiples, LFO and SSG-EG.</summary>
    [Fact]
    public void SettingsReadAsText()
    {
        FmPatch patch = FmPatchLibrary.Lead with { Fms = 2, Ams = 1, Operators = [FmPatchLibrary.Lead.Operators[0] with { SsgEg = 9 }, .. FmPatchLibrary.Lead.Operators.Skip(1)] };
        FmScore score = new(1, 2, 3);

        Assert.Equal("ALG 4 FB 5 MULT 1 1 2 2, LFO 3 FMS 2 AMS 1, SSG-EG", FmFitReport.Settings(new FmFit(patch, 3, FmFitSource.Genetic, score)));
        Assert.Equal("ALG 7 FB 0 MULT 1 1 1 1, no LFO", FmFitReport.Settings(new FmFit(FmPatchLibrary.Sine, null, FmFitSource.Library, score)));
        Assert.Equal(["library", "exhaustive", "genetic"], Enum.GetValues<FmFitSource>().Select(FmFitReport.SourceName));
    }
}
