namespace Mod2Xgm.Optimizer;

/// <summary>How the FM fitter searches (plan T8.2, docs/adr/0069-fm-patch-fitting.md).</summary>
/// <param name="MaxRenders">
/// Most notes each step of a region's fit may render: the structure search, and each local search after it. <c>null</c>
/// (the default, David) for no limit, where every step runs until it finds nothing better. It counts renders, not time,
/// so a fit is the same on every machine; tests give a small number.
/// </param>
/// <param name="Seed">Seeds the genetic search's random choices, so a fit is the same every run.</param>
/// <param name="Exhaustive">Run the pruned exhaustive structure search.</param>
/// <param name="Genetic">Run the genetic structure search.</param>
/// <param name="SsgEg">Let the level fit try SSG-EG shapes.</param>
/// <param name="Lfo">Also fit with the LFO (vibrato, tremolo and its rate); the fit without it is always made too.</param>
internal sealed record FmFitOptions(
    int? MaxRenders = null,
    int Seed = 1,
    bool Exhaustive = true,
    bool Genetic = true,
    bool SsgEg = true,
    bool Lfo = true);
