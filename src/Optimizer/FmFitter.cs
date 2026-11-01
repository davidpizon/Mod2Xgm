using System.Diagnostics;
using System.Globalization;
using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;

namespace Mod2Xgm.Optimizer;

/// <summary>Which search found a fit.</summary>
internal enum FmFitSource
{
    /// <summary>A built-in patch (<see cref="FmPatchLibrary"/>), unchanged.</summary>
    Library,

    /// <summary>The pruned exhaustive structure search, then the level fit.</summary>
    Exhaustive,

    /// <summary>The genetic structure search, then the level fit.</summary>
    Genetic,
}

/// <summary>One FM patch fitted to one pitch region, with how close it sounds.</summary>
/// <param name="Patch">The patch.</param>
/// <param name="LfoRate">The LFO rate it was fitted with (0–7), or <c>null</c> when it doesn't use the LFO. The LFO is one setting for the whole chip, so plan M9 picks one rate per song and gives the other instruments their <see cref="FmRegionFit.NoLfo"/> twins.</param>
/// <param name="Source">Which search found it.</param>
/// <param name="Score">How close it sounds to the region.</param>
internal sealed record FmFit(FmPatch Patch, int? LfoRate, FmFitSource Source, NoteScore Score);

/// <summary>The fits for one pitch region of one sample (docs/adr/0069-fm-patch-fitting.md).</summary>
/// <param name="Region">Index into <see cref="SampleAnalysis.Regions"/>.</param>
/// <param name="Library">The closest built-in patch, the search's seed and the bar a fit must beat.</param>
/// <param name="Exhaustive">The exhaustive search's fit, or <c>null</c> when it didn't run.</param>
/// <param name="Genetic">The genetic search's fit, or <c>null</c> when it didn't run.</param>
/// <param name="Best">The closest of all of them, the library patch included: the candidate for the allocator.</param>
/// <param name="NoLfo">The closest that doesn't use the LFO: <see cref="Best"/>'s twin for a song whose LFO rate is another instrument's.</param>
/// <param name="Renders">Notes rendered for this region.</param>
/// <param name="Elapsed">How long the fit took (for reports only; never changes a result).</param>
internal sealed record FmRegionFit(int Region, FmFit Library, FmFit? Exhaustive, FmFit? Genetic, FmFit Best, FmFit NoLfo, int Renders, TimeSpan Elapsed)
{
    /// <summary>The better of the two searches' fits, or <c>null</c> when neither ran.</summary>
    public FmFit? Fitted => new[] { Exhaustive, Genetic }.Where(f => f is not null).MinBy(f => f!.Score.MelDb);

    /// <summary>True when a search's fit sounds closer than the best built-in patch (plan T8.2's acceptance).</summary>
    public bool FittedBeatsLibrary => Fitted is { } f && f.Score.MelDb < Library.Score.MelDb;
}

/// <summary>The fits for every pitch region of one sample.</summary>
/// <param name="Sample">Index into <c>Song.Samples</c>.</param>
/// <param name="Name">The sample's name.</param>
/// <param name="Regions">One per region with sound, in <see cref="SampleAnalysis.Regions"/> order.</param>
internal sealed record FmSampleFit(int Sample, string Name, IReadOnlyList<FmRegionFit> Regions);

/// <summary>
/// Fits FM patches to samples, one per pitch region (plan T8.2; docs/references/fm-matching.md;
/// docs/adr/0069-fm-patch-fitting.md):
/// <list type="number">
/// <item>Judge every built-in patch; the closest is the seed and the bar.</item>
/// <item>Search structures (algorithm, multiples, modulator level) with the pruned exhaustive search and the genetic
/// search, each by the quick harmonic score.</item>
/// <item>Fit levels and envelopes of each search's best structures by coordinate descent on the whole note's harmonics,
/// first without the LFO, then from there with it.</item>
/// <item>Pick the closest by the rendered log-mel distance; keep the closest without the LFO as its twin.</item>
/// </list>
/// Every sample with sound is fitted, drums and noise too, so the allocator (plan M9) can weigh FM against PCM and PSG
/// by numbers. Same inputs and options, same fits.
/// </summary>
internal static class FmFitter
{
    /// <summary>Structures each search hands to the level fit.</summary>
    private const int StructuresPerSearch = 2;

    /// <summary>Fits every region of <paramref name="analysis"/>; none for a silent sample.</summary>
    public static FmSampleFit Fit(SampleAnalysis analysis, FmFitOptions options)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(options);
        List<FmRegionFit> regions = [];
        if (analysis.Kind != SampleKind.Silent)
        {
            for (int r = 0; r < analysis.Regions.Count; r++)
            {
                if (NoteTarget.Of(analysis.Regions[r]) is { } target)
                {
                    regions.Add(FitRegion(r, target, options, PatchName(analysis.Sample, r)));
                }
            }
        }

        return new FmSampleFit(analysis.Sample, analysis.Name, regions);
    }

    /// <summary>Fits one region's <paramref name="target"/>; fitted patches are called <paramref name="name"/>.</summary>
    internal static FmRegionFit FitRegion(int region, NoteTarget target, FmFitOptions options, string name)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        Stopwatch clock = Stopwatch.StartNew();
        FmEvaluator evaluator = new(target);
        FmFit library = FmPatchLibrary.All
            .Select(p => new FmFit(p, null, FmFitSource.Library, evaluator.Judge(p, null)))
            .MinBy(f => f.Score.MelDb)!; // The library is never empty.

        List<FmFit> fits = [library];
        FmFit? exhaustive = null;
        FmFit? genetic = null;
        if (options.Exhaustive)
        {
            IReadOnlyList<FmStructure> structures = FmExhaustiveSearch.Search(evaluator, library.Patch, StructuresPerSearch, options.MaxRenders);
            exhaustive = Refine(evaluator, structures, library.Patch, FmFitSource.Exhaustive, options, name, fits);
        }

        if (options.Genetic)
        {
            IReadOnlyList<FmStructure> structures = FmGeneticSearch.Search(evaluator, library.Patch, StructuresPerSearch, options.MaxRenders, options.Seed);
            genetic = Refine(evaluator, structures, library.Patch, FmFitSource.Genetic, options, name, fits);
        }

        // MinBy keeps the first of equals: the library patch, then the exhaustive search's, then the genetic search's.
        FmFit best = fits.MinBy(f => f.Score.MelDb)!;
        FmFit noLfo = fits.Where(f => f.LfoRate is null).MinBy(f => f.Score.MelDb)!; // The library fit has no LFO.
        return new FmRegionFit(region, library, exhaustive, genetic, best, noLfo, evaluator.Renders, clock.Elapsed);
    }

    /// <summary>The level fit of each structure, without and then with the LFO; adds every result to <paramref name="fits"/> and returns the closest.</summary>
    private static FmFit? Refine(FmEvaluator evaluator, IReadOnlyList<FmStructure> structures, FmPatch seed, FmFitSource source, FmFitOptions options, string name, List<FmFit> fits)
    {
        List<FmFit> found = [];
        foreach (FmStructure structure in structures)
        {
            FmVoiceSetting plain = FmLocalSearch.Refine(evaluator, new FmVoiceSetting(structure.Patch(seed, name), null), false, options.SsgEg, options.MaxRenders);
            found.Add(Judged(evaluator, plain, source));
            if (options.Lfo)
            {
                FmVoiceSetting withLfo = FmLocalSearch.Refine(evaluator, plain, true, options.SsgEg, options.MaxRenders);
                if (withLfo.LfoRate is not null)
                {
                    found.Add(Judged(evaluator, withLfo, source));
                }
            }
        }

        fits.AddRange(found);
        return found.MinBy(f => f.Score.MelDb);
    }

    private static FmFit Judged(FmEvaluator evaluator, FmVoiceSetting setting, FmFitSource source) =>
        new(setting.Patch, setting.LfoRate, source, evaluator.Judge(setting.Patch, setting.LfoRate));

    /// <summary>A fitted patch's name: the sample's number and the region's, e.g. "Fit 3.1".</summary>
    private static string PatchName(int sample, int region) =>
        string.Create(CultureInfo.InvariantCulture, $"Fit {sample + 1}.{region + 1}");
}
