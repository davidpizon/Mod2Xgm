using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;
using Mod2Xgm.Optimizer;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="FmEvaluator"/> and <see cref="NoteTarget"/>: what a fit aims at and how close a note is.</summary>
public sealed class FmEvaluatorTests
{
    /// <summary>Frames of a made-up spectrogram: a slope from 0 dB down, 8 bins wide.</summary>
    private static float[][] Slope(int frames, float gainDb = 0) =>
        [.. Enumerable.Range(0, frames).Select(_ => Enumerable.Range(0, 8).Select(k => gainDb - (6f * k)).ToArray())];

    /// <summary>A spectrogram scores 0 against itself.</summary>
    [Fact]
    public void Distance_WithSameSpectrogram_IsZero()
    {
        // Act
        double db = NoteScorer.Distance(Slope(10), Slope(10), 10).Db;

        // Assert
        Assert.Equal(0, db, 1e-9);
    }

    /// <summary>A spectrogram scores 0 against itself made quieter: only the shape counts.</summary>
    [Fact]
    public void Distance_WithQuieterCopy_IsZero()
    {
        // Act
        double db = NoteScorer.Distance(Slope(10), Slope(10, -12), 10).Db;

        // Assert
        Assert.Equal(0, db, 1e-6);
    }

    /// <summary>The level difference comes back as the offset, not as distance.</summary>
    [Fact]
    public void Distance_WithLouderCopy_ReportsOffset()
    {
        // Act
        double offset = NoteScorer.Distance(Slope(10), Slope(10, 12), 10).OffsetDb;

        // Assert
        Assert.Equal(-12, offset, 1e-6);
    }

    /// <summary>A silent note scores the whole range; a note that stops halfway scores worse than one that plays on.</summary>
    [Fact]
    public void Score_WithSilenceOrMissingFrames_Costs()
    {
        // Act
        float[][] silent = [.. Enumerable.Range(0, 10).Select(_ => Enumerable.Repeat((float)SampleAnalyzer.FloorDb, 8).ToArray())];

        // Assert
        Assert.Equal(NoteScorer.RangeDb, NoteScorer.Distance(Slope(10), silent, 10).Db);
        Assert.True(NoteScorer.Distance(Slope(10), Slope(5), 10).Db > 1);
    }

    /// <summary>A patch scores about 0 against its own sound, and a different patch scores worse on every measure.</summary>
    [Fact]
    public void Score_WithOwnSound_IsClose()
    {
        // Arrange
        FmEvaluator sut = new(FmTargets.Of(FmPatchLibrary.Lead));
        NoteScore same = sut.Judge(FmPatchLibrary.Lead, null);

        // Act
        NoteScore other = sut.Judge(FmPatchLibrary.Organ, null);

        // Assert
        Assert.True(same.MelDb < 0.01, $"mel {same.MelDb}");
        Assert.True(same.HarmonicDb < 0.01, $"harmonic {same.HarmonicDb}");
        Assert.True(other.MelDb > 1, $"mel {other.MelDb}");
        Assert.True(other.HarmonicDb > 1, $"harmonic {other.HarmonicDb}");
        Assert.True(other.MfccDistance > same.MfccDistance);
        Assert.Equal(same.HarmonicDb, same.Search);
        Assert.Equal(other.Search, sut.Search(FmPatchLibrary.Organ, null));
        Assert.True(sut.Quick(FmPatchLibrary.Lead, null) < sut.Quick(FmPatchLibrary.Organ, null));
    }

    /// <summary>Each patch is rendered once per score: asking again, or under another name, renders nothing new.</summary>
    [Fact]
    public void Score_Twice_RemembersIt()
    {
        // Arrange
        FmEvaluator sut = new(FmTargets.Of(FmPatchLibrary.Organ));
        sut.Judge(FmPatchLibrary.Organ, null);
        sut.Quick(FmPatchLibrary.Organ, null);
        int renders = sut.Renders;
        sut.Judge(FmPatchLibrary.Organ with { Name = "another" }, null);
        sut.Search(FmPatchLibrary.Organ, null);

        // Act
        sut.Quick(FmPatchLibrary.Organ, 5);

        // Assert
        Assert.Equal(2, renders);
        Assert.Equal(renders, sut.Renders);
    }

    /// <summary>An LFO rate counts only for a patch that takes the LFO.</summary>
    [Fact]
    public void Key_WithLfoRate_CountsOnlyWhenUsed()
    {
        // Act
        FmPatch vibrato = FmPatchLibrary.Sine with { Fms = 3 };

        // Assert
        Assert.Null(FmEvaluator.Lfo(FmPatchLibrary.Sine, 4));
        Assert.Equal(4, FmEvaluator.Lfo(vibrato, 4));
        Assert.Equal(FmEvaluator.Key(FmPatchLibrary.Sine, null), FmEvaluator.Key(FmPatchLibrary.Sine, 4));
        Assert.NotEqual(FmEvaluator.Key(vibrato, null), FmEvaluator.Key(vibrato, 4));
    }

    /// <summary>A pitched region aims at its fundamental; an unpitched one at the mapper's fallback (C-2 → C-4); an empty one at nothing.</summary>
    [Fact]
    public void Targets_WithRegion_FollowIt()
    {
        // Arrange
        NoteTarget pitched = FmTargets.Of(FmTargets.Saw);

        // Act
        RegionSpectrum spectrum = pitched.Spectrum;

        // Assert
        Assert.Equal(FmTargets.C2 / 64, pitched.NoteHz, 0.1);
        Assert.Equal(pitched.NoteHz, pitched.FundamentalHz);
        Assert.Equal(261.63, NoteTarget.Of(new PitchRegion(FmTargets.C2, FmTargets.C2, FmTargets.C2, 1, null, spectrum))!.NoteHz, 0.01);
        Assert.Null(NoteTarget.Of(new PitchRegion(FmTargets.C2, FmTargets.C2, FmTargets.C2, 1, null, new RegionSpectrum(0.01, [], null))));
    }
}
