using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;
using Mod2Xgm.Optimizer;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="FmEvaluator"/> and <see cref="FmTarget"/>: what a fit aims at and how close a note is.</summary>
public sealed class FmEvaluatorTests
{
    /// <summary>Frames of a made-up spectrogram: a slope from 0 dB down, 8 bins wide.</summary>
    private static float[][] Slope(int frames, float gainDb = 0) =>
        [.. Enumerable.Range(0, frames).Select(_ => Enumerable.Range(0, 8).Select(k => gainDb - (6f * k)).ToArray())];

    /// <summary>A spectrogram scores 0 against itself, and against itself made louder or quieter: only the shape counts.</summary>
    [Fact]
    public void TheDistanceIgnoresTheOverallLevel()
    {
        Assert.Equal(0, FmEvaluator.Distance(Slope(10), Slope(10), 10).Db, 1e-9);
        Assert.Equal(0, FmEvaluator.Distance(Slope(10), Slope(10, -12), 10).Db, 1e-6);
        Assert.Equal(-12, FmEvaluator.Distance(Slope(10), Slope(10, 12), 10).OffsetDb, 1e-6);
    }

    /// <summary>A silent note scores the whole range; a note that stops halfway scores worse than one that plays on.</summary>
    [Fact]
    public void SilenceAndMissingFramesCost()
    {
        float[][] silent = [.. Enumerable.Range(0, 10).Select(_ => Enumerable.Repeat((float)SampleAnalyzer.FloorDb, 8).ToArray())];

        Assert.Equal(FmEvaluator.RangeDb, FmEvaluator.Distance(Slope(10), silent, 10).Db);
        Assert.True(FmEvaluator.Distance(Slope(10), Slope(5), 10).Db > 1);
    }

    /// <summary>A patch scores about 0 against its own sound, and a different patch scores worse on every measure.</summary>
    [Fact]
    public void APatchMatchesItsOwnSound()
    {
        FmEvaluator evaluator = new(FmTargets.Of(FmPatchLibrary.Lead));

        FmScore same = evaluator.Judge(FmPatchLibrary.Lead, null);
        FmScore other = evaluator.Judge(FmPatchLibrary.Organ, null);

        Assert.True(same.MelDb < 0.01, $"mel {same.MelDb}");
        Assert.True(same.HarmonicDb < 0.01, $"harmonic {same.HarmonicDb}");
        Assert.True(other.MelDb > 1, $"mel {other.MelDb}");
        Assert.True(other.HarmonicDb > 1, $"harmonic {other.HarmonicDb}");
        Assert.True(other.MfccDistance > same.MfccDistance);
        Assert.Equal(same.HarmonicDb, same.Search);
        Assert.Equal(other.Search, evaluator.Search(FmPatchLibrary.Organ, null));
        Assert.True(evaluator.Quick(FmPatchLibrary.Lead, null) < evaluator.Quick(FmPatchLibrary.Organ, null));
    }

    /// <summary>Each patch is rendered once per score: asking again, or under another name, renders nothing new.</summary>
    [Fact]
    public void ScoresAreRemembered()
    {
        FmEvaluator evaluator = new(FmTargets.Of(FmPatchLibrary.Organ));
        evaluator.Judge(FmPatchLibrary.Organ, null);
        evaluator.Quick(FmPatchLibrary.Organ, null);
        int renders = evaluator.Renders;

        evaluator.Judge(FmPatchLibrary.Organ with { Name = "another" }, null);
        evaluator.Search(FmPatchLibrary.Organ, null);
        evaluator.Quick(FmPatchLibrary.Organ, 5);

        Assert.Equal(2, renders);
        Assert.Equal(renders, evaluator.Renders);
    }

    /// <summary>An LFO rate counts only for a patch that takes the LFO.</summary>
    [Fact]
    public void TheLfoRateCountsOnlyWhenUsed()
    {
        FmPatch vibrato = FmPatchLibrary.Sine with { Fms = 3 };

        Assert.Null(FmEvaluator.Lfo(FmPatchLibrary.Sine, 4));
        Assert.Equal(4, FmEvaluator.Lfo(vibrato, 4));
        Assert.Equal(FmEvaluator.Key(FmPatchLibrary.Sine, null), FmEvaluator.Key(FmPatchLibrary.Sine, 4));
        Assert.NotEqual(FmEvaluator.Key(vibrato, null), FmEvaluator.Key(vibrato, 4));
    }

    /// <summary>A pitched region aims at its fundamental; an unpitched one at the mapper's fallback (C-2 → C-4); an empty one at nothing.</summary>
    [Fact]
    public void TargetsFollowTheRegion()
    {
        FmTarget pitched = FmTargets.Of(FmTargets.Saw);
        RegionSpectrum spectrum = pitched.Spectrum;

        Assert.Equal(FmTargets.C2 / 64, pitched.NoteHz, 0.1);
        Assert.Equal(pitched.NoteHz, pitched.FundamentalHz);
        Assert.Equal(261.63, FmTarget.Of(new PitchRegion(FmTargets.C2, FmTargets.C2, FmTargets.C2, 1, null, spectrum))!.NoteHz, 0.01);
        Assert.Null(FmTarget.Of(new PitchRegion(FmTargets.C2, FmTargets.C2, FmTargets.C2, 1, null, new RegionSpectrum(0.01, [], null))));
    }
}
