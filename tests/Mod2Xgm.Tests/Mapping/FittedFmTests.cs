using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;
using Mod2Xgm.Optimizer;

namespace Mod2Xgm.Tests.Mapping;

/// <summary>
/// Tests for <see cref="FitChoice"/> and <see cref="FittedFm"/>: which fitted FM patch each sample plays for all its notes and the
/// song's one LFO rate (docs/adr/0137-simple-mapper-fitted-patches.md). Fits are made up with scores no real patch
/// reaches (below 0 dB) or none misses by (1000 dB), so the built-in patch's real score decides nothing by chance.
/// </summary>
public sealed class FittedFmTests
{
    /// <summary>The rate every note of the test samples plays at: ProTracker's C-2.</summary>
    private const double RateHz = 8287;

    /// <summary>A fit that sounds closer than the built-in patch, by a score no real patch reaches, plays in its place.</summary>
    [Fact]
    public void Choose_WithFitCloserThanBuiltIn_PlaysFit()
    {
        // Arrange
        FittedFm fits = Fits((0, Region(Fit("Fit 1.1", -1))));

        // Act
        FitChoice sut = FitChoice.Choose(fits, BuiltIn(0));

        // Assert
        Assert.Equal("Fit 1.1", sut.PatchFor(0)?.Name);
        Assert.Null(sut.LfoRate);
    }

    /// <summary>A fit no closer than the built-in patch leaves it in place.</summary>
    [Fact]
    public void Choose_WithFitFartherThanBuiltIn_KeepsBuiltIn()
    {
        // Arrange
        FittedFm fits = Fits((0, Region(Fit("Fit 1.1", 1000))));

        // Act
        FitChoice sut = FitChoice.Choose(fits, BuiltIn(0));

        // Assert
        Assert.Null(sut.PatchFor(0));
        Assert.Equal(1000, sut.Samples[0].FitDb);
    }

    /// <summary>A fit with the LFO that beats its twin without it runs the song's LFO at its rate and plays with it.</summary>
    [Fact]
    public void Choose_WithLfoFitClosest_RunsSongLfoAtItsRate()
    {
        // Arrange
        FittedFm fits = Fits((0, Region(Fit("Fit 1.1", -2, lfoRate: 5), Fit("Fit 1.1 flat", -1))));

        // Act
        FitChoice sut = FitChoice.Choose(fits, BuiltIn(0));

        // Assert
        Assert.Equal(5, sut.LfoRate);
        Assert.Equal(1, sut.LfoSamples);
        Assert.True(sut.PatchFor(0)?.UsesLfo);
    }

    /// <summary>
    /// Two fits that want different LFO rates: the rate whose fit gains more runs, and the other sample plays its fit
    /// without the LFO.
    /// </summary>
    [Fact]
    public void Choose_WithTwoLfoRates_RunsTheLargerGainAndPlaysTheOtherFlat()
    {
        // Arrange
        FittedFm fits = Fits(
            (0, Region(Fit("Fit 1.1", -2, lfoRate: 2), Fit("Fit 1.1 flat", -1))),
            (1, Region(Fit("Fit 2.1", -10, lfoRate: 6), Fit("Fit 2.1 flat", -1))));

        // Act
        FitChoice sut = FitChoice.Choose(fits, BuiltIn(0, 1));

        // Assert
        Assert.Equal(6, sut.LfoRate);
        Assert.Equal("Fit 1.1 flat", sut.PatchFor(0)?.Name);
        Assert.Equal("Fit 2.1", sut.PatchFor(1)?.Name);
    }

    /// <summary>A fit found without the LFO plays with no LFO depth, so the song's LFO run for another fit can't move it.</summary>
    [Fact]
    public void Choose_WithFlatFitWithDepth_PlaysItWithoutDepth()
    {
        // Arrange
        FittedFm fits = Fits((0, Region(Fit("Fit 1.1", -1) with { Patch = FmPatchLibrary.Sine with { Name = "Fit 1.1", Fms = 3 } })));

        // Act
        FitChoice sut = FitChoice.Choose(fits, BuiltIn(0));

        // Assert
        Assert.False(sut.PatchFor(0)?.UsesLfo);
    }

    /// <summary>A sample with no built-in patch to judge against (it isn't played on FM) gets no regions.</summary>
    [Fact]
    public void Choose_WithSampleNotOnFm_LeavesItOut()
    {
        // Arrange
        FittedFm fits = Fits((0, Region(Fit("Fit 1.1", -1))));

        // Act
        FitChoice sut = FitChoice.Choose(fits, new Dictionary<int, BuiltInFm>());

        // Assert
        Assert.Empty(sut.Samples);
    }

    /// <summary>
    /// A sample with a fit per pitch region plays one of them for all its notes: the one closest over both regions, so
    /// the instrument never switches sound between notes (round 38).
    /// </summary>
    [Fact]
    public void Choose_WithFitPerRegion_PlaysOneFitForAllNotes()
    {
        // Arrange
        SampleAnalysis analysis = Analysis(0, RateHz, RateHz * 4);
        FmSampleFit sample = new(0, "sine", [Region(Fit("Fit 1.1", -1)), Region(Fit("Fit 1.2", -1000)) with { Region = 1 }]);
        FittedFm fits = new([analysis], [sample]);

        // Act
        FitChoice sut = FitChoice.Choose(fits, BuiltIn(0));

        // Assert
        Assert.Equal(2, analysis.Regions.Count);
        Assert.Equal("Fit 1.2", sut.PatchFor(0)?.Name);
        Assert.Equal(1, sut.Samples[0].FitRegion);
        Assert.Equal(2, sut.Samples[0].Regions);
    }

    /// <summary>A fit that uses SSG-EG plays without it, since its state carries from note to note in a song (round 39).</summary>
    [Fact]
    public void Choose_WithSsgEgFit_PlaysItWithoutSsgEg()
    {
        // Arrange
        FmFit fit = Fit("Fit 1.1", -1);
        FmPatch withSsg = fit.Patch with { Operators = [fit.Patch.Operators[0] with { SsgEg = 10 }, .. fit.Patch.Operators.Skip(1)] };
        FittedFm fits = Fits((0, Region(fit with { Patch = withSsg })));
        Dictionary<int, BuiltInFm> brass = new() { [0] = new BuiltInFm(FmPatchLibrary.Brass, 0, true) };

        // Act
        FitChoice sut = FitChoice.Choose(fits, brass);

        // Assert
        Assert.NotNull(sut.PatchFor(0));
        Assert.All(sut.PatchFor(0)!.Operators, o => Assert.Equal(0, o.SsgEg));
    }

    /// <summary>Taking SSG-EG out leaves a patch without it as the same object, and clears it on every operator of one with it.</summary>
    [Fact]
    public void WithoutSsgEg_ForPatches_ClearsOnlyWhereSet()
    {
        // Arrange
        FmPatch plain = FmPatchLibrary.Organ;
        FmPatch withSsg = plain with { Operators = [.. plain.Operators.Select(o => o with { SsgEg = 9 })] };

        // Act
        FmPatch fromPlain = FitChoice.WithoutSsgEg(plain);
        FmPatch fromSsg = FitChoice.WithoutSsgEg(withSsg);

        // Assert
        Assert.Same(plain, fromPlain);
        Assert.All(fromSsg.Operators, o => Assert.Equal(0, o.SsgEg));
    }

    /// <summary>A held sine sounds as loud two octaves up as where it was levelled, so its notes there aren't off.</summary>
    [Fact]
    public void LevelOffDb_ForSteadySine_IsAboutZero()
    {
        // Act
        double off = FitChoice.LevelOffDb(FmPatchLibrary.Sine, null, 1760, 440, loops: true);

        // Assert
        Assert.InRange(off, 0, 0.5);
    }

    /// <summary>
    /// A patch whose decay quickens with pitch (rate scaling) fades much sooner two octaves up, so the notes there play
    /// quieter than its level was set for, and the difference counts against it (round 39).
    /// </summary>
    [Fact]
    public void LevelOffDb_ForDecayThatScalesWithPitch_IsLarge()
    {
        // Arrange
        FmOperator fading = new(1, 0, 0, 3, 31, false, 12, 0, 15, 15);
        FmPatch patch = FmPatchLibrary.Sine with { Operators = [fading, .. FmPatchLibrary.Sine.Operators.Skip(1)] };

        // Act
        double off = FitChoice.LevelOffDb(patch, null, 1760, 110, loops: true);

        // Assert
        Assert.True(off > 3, $"only {off:0.0} dB");
    }

    /// <summary>At the pitch it was levelled at, a patch is never off.</summary>
    [Fact]
    public void LevelOffDb_AtLevelPitch_IsZero()
    {
        // Act
        double off = FitChoice.LevelOffDb(FmPatchLibrary.Organ, null, 440, 440, loops: true);

        // Assert
        Assert.Equal(0, off);
    }

    /// <summary>A sample with no fits plays its built-in patch.</summary>
    [Fact]
    public void PatchFor_WithUnfittedSample_IsNull()
    {
        // Arrange
        FitChoice sut = FitChoice.None;

        // Act
        FmPatch? patch = sut.PatchFor(3);

        // Assert
        Assert.Null(patch);
    }

    /// <summary><c>--quality best</c> fits with no limit on renders; <c>normal</c> stops each step at its budget; both fit with the LFO.</summary>
    [Theory]
    [InlineData(true, null)]
    [InlineData(false, FittedFm.NormalRendersPerStep)]
    public void Options_ForQuality_LimitsRenders(bool best, int? expected)
    {
        // Act
        FmFitOptions options = FittedFm.Options(best, seed: 4);

        // Assert
        Assert.Equal(expected, options.MaxRenders);
        Assert.Equal(4, options.Seed);
        Assert.True(options.Lfo);
    }

    /// <summary>A looped 64-frame sine, analyzed as played at <paramref name="rates"/> (<see cref="RateHz"/> when none are given).</summary>
    private static SampleAnalysis Analysis(int index, params double[] rates)
    {
        Sample sine = new("sine", [.. Enumerable.Range(0, 64).Select(i => (float)Math.Sin(2 * Math.PI * i / 64))], new SampleLoop(0, 64));
        return SampleAnalyzer.Analyze(index, sine, rates.Length == 0 ? [RateHz] : rates);
    }

    /// <summary>Each sample's analysis and its one region's fit.</summary>
    private static FittedFm Fits(params (int Sample, FmRegionFit Region)[] samples) =>
        new([.. samples.Select(s => Analysis(s.Sample))], [.. samples.Select(s => new FmSampleFit(s.Sample, "sine", [s.Region]))]);

    /// <summary>Region 0's fits: <paramref name="best"/>, and <paramref name="flat"/> as its twin without the LFO (itself when not given).</summary>
    private static FmRegionFit Region(FmFit best, FmFit? flat = null) =>
        new(0, best, best, null, best, flat ?? best, 0, TimeSpan.Zero);

    /// <summary>A made-up fit called <paramref name="name"/> at <paramref name="melDb"/>; with <paramref name="lfoRate"/>, it uses the LFO.</summary>
    private static FmFit Fit(string name, double melDb, int? lfoRate = null) =>
        new(FmPatchLibrary.Sine with { Name = name, Fms = lfoRate is null ? 0 : 3 }, lfoRate, FmFitSource.Exhaustive, new NoteScore(melDb, 0, null));

    /// <summary>The built-in sine patch for each of <paramref name="samples"/>, unlifted, looping.</summary>
    private static Dictionary<int, BuiltInFm> BuiltIn(params int[] samples) =>
        samples.ToDictionary(s => s, _ => new BuiltInFm(FmPatchLibrary.Sine, 0, true));
}
