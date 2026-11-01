using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;
using Mod2Xgm.Optimizer;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Targets for the FM fitter's tests: made-up samples analyzed as the converter does, and FM notes measured the same way.</summary>
internal static class FmTargets
{
    /// <summary>ProTracker's C-2 rate on a PAL Amiga.</summary>
    public const double C2 = AmigaPitch.PalClockHz / 428;

    /// <summary>A looped one-cycle sawtooth of 64 frames: every harmonic, falling as 1/k.</summary>
    public static Sample Saw { get; } = new("saw", [.. Enumerable.Range(0, 64).Select(i => (float)((i / 32.0) - 1))], new SampleLoop(0, 64));

    /// <summary>A looped one-cycle square wave of 64 frames: odd harmonics only.</summary>
    public static Sample Square { get; } = new("square", [.. Enumerable.Range(0, 64).Select(i => i < 32 ? 0.8f : -0.8f)], new SampleLoop(0, 64));

    /// <summary><paramref name="sample"/> analyzed as played at C-2.</summary>
    public static SampleAnalysis Analyze(Sample sample) => SampleAnalyzer.Analyze(0, sample, [C2, C2]);

    /// <summary>The first region of <paramref name="sample"/> played at C-2, as the fitter's target.</summary>
    public static FmTarget Of(Sample sample) => FmTarget.Of(Analyze(sample).Regions[0])!;

    /// <summary>The sound of <paramref name="patch"/> held at <paramref name="hz"/>, measured as a region is: a target any patch can be judged against.</summary>
    public static FmTarget Of(FmPatch patch, double hz = 220, int? lfoRate = null) =>
        new(hz, hz, SampleAnalyzer.Spectrum(FmNoteRenderer.Render(patch, lfoRate, hz, SampleAnalyzer.RegionSeconds), hz));
}
