using Mod2Xgm.Analysis;
using Mod2Xgm.Optimizer;

namespace Mod2Xgm.Mapping;

/// <summary>
/// FM patches fitted to the song's samples that the simple mapper may play in place of its built-in ones (plan T5.3q,
/// docs/adr/0137-simple-mapper-fitted-patches.md): one sample's analysis and fits per entry.
/// </summary>
/// <param name="Analyses">The samples' analyses, whose pitch regions the fits are for.</param>
/// <param name="Fits">The fits, one per sample the mapper plays on FM.</param>
internal sealed record FittedFm(IReadOnlyList<SampleAnalysis> Analyses, IReadOnlyList<FmSampleFit> Fits)
{
    /// <summary>
    /// The most notes each step of a fit renders at <c>--quality normal</c>: about 5 minutes for FunkyChristmas on four
    /// cores, where <c>best</c> has no limit (David, 2026-11-21).
    /// </summary>
    public const int NormalRendersPerStep = 1000;

    /// <summary>
    /// How the samples are fitted at <paramref name="best"/> (<c>--quality best</c>) or <c>normal</c>: with the LFO, which
    /// the song may then run at one rate (docs/adr/0137-simple-mapper-fitted-patches.md).
    /// </summary>
    public static FmFitOptions Options(bool best, int seed) => new(best ? null : NormalRendersPerStep, seed);
}

/// <summary>A sample's built-in FM patch, as the simple mapper plays it, for judging its fits against.</summary>
/// <param name="Patch">The built-in or harmonic patch.</param>
/// <param name="LiftCents">How far above the sample's measured pitch it plays (<see cref="SamplePitch.LibraryLiftCents"/>).</param>
/// <param name="Loops">True when the sample loops, so its level is measured as a held note.</param>
internal sealed record BuiltInFm(FmPatch Patch, double LiftCents, bool Loops);

/// <summary>The one patch a sample plays for all its notes.</summary>
/// <param name="Fitted">The fitted patch, at full level and with its heard level measured; <c>null</c> to keep the built-in one.</param>
/// <param name="FitRegion">Index into the sample's <see cref="SampleAnalysis.Regions"/> of the region the closest fit was made for.</param>
/// <param name="Regions">How many pitch regions it was judged on.</param>
/// <param name="Notes">How many notes the song plays with the sample in those regions.</param>
/// <param name="FitDb">The closest fit's log-mel distance from the sample, averaged over the regions by notes, of the fits the song's LFO allows.</param>
/// <param name="BuiltInDb">The built-in patch's distance, judged the same way.</param>
internal sealed record SamplePatch(FmPatch? Fitted, int FitRegion, int Regions, int Notes, double FitDb, double BuiltInDb);

/// <summary>Which fits the song plays, and the one LFO rate they share.</summary>
/// <param name="LfoRate">The song's LFO rate (0–7), or <c>null</c> to leave the LFO off.</param>
/// <param name="Samples">Each fitted sample's choice, by sample index.</param>
/// <param name="LfoSamples">How many samples play a fit that uses the LFO.</param>
internal sealed record FitChoice(int? LfoRate, IReadOnlyDictionary<int, SamplePatch> Samples, int LfoSamples)
{
    /// <summary>No fits: every sample keeps its built-in patch.</summary>
    public static FitChoice None { get; } = new(null, new Dictionary<int, SamplePatch>(), 0);

    /// <summary>
    /// The patch every note of <paramref name="sample"/> plays, or <c>null</c> for the built-in patch: one sound for the
    /// whole pitch range, since a patch that changes between notes is heard as the instrument switching (round 38).
    /// </summary>
    public FmPatch? PatchFor(int sample) => Samples.GetValueOrDefault(sample)?.Fitted;

    /// <summary>
    /// Picks the song's LFO rate and each sample's one patch (docs/adr/0137-simple-mapper-fitted-patches.md). Every
    /// candidate (the built-in patch, and each region's fit with and without the LFO) is judged by the fitter's own
    /// log-mel score on every pitch region of the sample, and the scores are averaged by the regions' notes. The built-in
    /// patch is judged at the pitch it plays (lifted for a chord's cycle). The LFO rate is the one whose fits gain the
    /// most over the samples' other choices, weighed by notes. Each sample then plays the closest candidate the rate
    /// allows; the built-in patch wins a tie, so a fit plays only where it sounds closer.
    /// </summary>
    /// <param name="fits">The fits.</param>
    /// <param name="builtIn">The built-in patch of every sample that may play a fit, by sample index.</param>
    public static FitChoice Choose(FittedFm fits, IReadOnlyDictionary<int, BuiltInFm> builtIn)
    {
        ArgumentNullException.ThrowIfNull(fits);
        ArgumentNullException.ThrowIfNull(builtIn);
        List<Judged> judged = [];
        foreach (FmSampleFit sample in fits.Fits)
        {
            if (builtIn.TryGetValue(sample.Sample, out BuiltInFm? own) && fits.Analyses.FirstOrDefault(a => a.Sample == sample.Sample) is { } analysis
                && Judge(sample, analysis, own) is { } one)
            {
                judged.Add(one);
            }
        }

        int? rate = judged
            .SelectMany(j => j.Candidates.Where(c => c.Fit.LfoRate is not null).GroupBy(c => c.Fit.LfoRate!.Value)
                .Select(g => (Rate: g.Key, Gain: Math.Max(0, j.Floor - g.Min(c => c.Db)) * j.Weight)))
            .GroupBy(g => g.Rate)
            .Select(g => (Rate: g.Key, Gain: g.Sum(x => x.Gain)))
            .Where(g => g.Gain > 0)
            .OrderByDescending(g => g.Gain)
            .ThenBy(g => g.Rate)
            .Select(g => (int?)g.Rate)
            .FirstOrDefault();

        Dictionary<int, SamplePatch> samples = [];
        int lfoSamples = 0;
        foreach (Judged one in judged)
        {
            Candidate closest = one.Candidates.Where(c => c.Fit.LfoRate is null || c.Fit.LfoRate == rate).MinBy(c => c.Db)!; // Every region has a fit without the LFO.
            FmPatch? patch = null;
            if (closest.Db < one.BuiltInDb)
            {
                FmPatch full = SoundMenu.AtFullLevel(closest.Fit.LfoRate is null ? WithoutLfo(closest.Fit.Patch) : closest.Fit.Patch);                patch = SoundMenu.Measured(full, one.LevelHz, one.Loops);
                lfoSamples += closest.Fit.LfoRate is null ? 0 : 1;
            }

            samples[one.Sample] = new SamplePatch(patch, closest.FitRegion,one.Regions, one.Notes, closest.Db, one.BuiltInDb);
        }

        return new FitChoice(lfoSamples > 0 ? rate : null, samples, lfoSamples);
    }

    /// <summary>
    /// Scores the built-in patch and every fit of one sample on all its pitch regions, averaged by notes; <c>null</c>
    /// when no region can be judged.
    /// </summary>
    private static Judged? Judge(FmSampleFit sample, SampleAnalysis analysis, BuiltInFm own)
    {
        List<Region> regions = [];
        foreach (FmRegionFit fit in sample.Regions.Where(f => f.Region < analysis.Regions.Count))
        {
            if (NoteTarget.Of(analysis.Regions[fit.Region]) is { } target)
            {
                regions.Add(new Region(fit.Region, target, analysis.Regions[fit.Region].Notes));
            }
        }

        if (regions.Count == 0)
        {
            return null;
        }

        double total = regions.Sum(r => r.Weight);
        double Mean(Func<Region, double> db) => regions.Sum(r => db(r) * r.Weight) / total;
        Region busiest = regions.MaxBy(r => r.Weight)!; // Not empty, checked above.
        double lift = Math.Pow(2, own.LiftCents / 1200);
        double builtInDb = Mean(r => new FmEvaluator(r.Target with { NoteHz = r.Target.NoteHz * lift }).Judge(own.Patch, null).MelDb
            + LevelOffDb(own.Patch, null, r.Target.NoteHz * lift, busiest.Target.NoteHz * lift, own.Loops));

        List<Candidate> candidates = [];
        foreach (FmRegionFit fit in sample.Regions.Where(f => regions.Any(r => r.Index == f.Region)))
        {
            FmFit[] options = fit.Best.LfoRate is not null && fit.Best.Score.MelDb < fit.NoLfo.Score.MelDb ? [fit.NoLfo, fit.Best] : [fit.NoLfo];
            foreach (FmFit found in options)
            {
                FmFit option = found with { Patch = WithoutSsgEg(found.Patch) };
                bool asFitted = ReferenceEquals(option.Patch, found.Patch);

                // A fit's own region keeps the score the fitter gave it, unless SSG-EG was taken out; the others are judged as the fitter would.
                double db = Mean(r => (asFitted && r.Index == fit.Region ? option.Score.MelDb : new FmEvaluator(r.Target).Judge(option.Patch, option.LfoRate).MelDb)
                    + LevelOffDb(option.Patch, option.LfoRate, r.Target.NoteHz, busiest.Target.NoteHz, own.Loops));
                candidates.Add(new Candidate(fit.Region, option, db));
            }
        }

        double floor = Math.Min(builtInDb, candidates.Where(c => c.Fit.LfoRate is null).Min(c => c.Db));
        int notes = regions.Sum(r => r.Notes);
        return new Judged(sample.Sample, candidates, builtInDb, floor, total, regions.Count, notes, busiest.Target.NoteHz, own.Loops);
    }

    /// <summary>
    /// How many dB louder or quieter <paramref name="patch"/> sounds at <paramref name="hz"/> than at
    /// <paramref name="levelHz"/>, where its level is set. The fitter's score lines the levels up before it compares, so
    /// a patch that nearly vanishes at another pitch scores as close as one that doesn't. Every note of the sample plays
    /// at the one level, so that note sounds this much too loud or too quiet, which counts as that much farther from the
    /// sample (round 39: a fit some 30 dB down on part of the melody).
    /// </summary>
    internal static double LevelOffDb(FmPatch patch, int? lfoRate, double hz, double levelHz, bool loops)
    {
        if (Math.Abs(Math.Log(hz / levelHz)) < 1e-9)
        {
            return 0;
        }

        double at = Heard(patch, lfoRate, hz, loops);
        double set = Heard(patch, lfoRate, levelHz, loops);
        return at > 0 && set > 0 ? Math.Abs(20 * Math.Log10(at / set)) : NoteScorer.RangeDb;
    }

    /// <summary>The level of <paramref name="patch"/> held at <paramref name="hz"/>, as <see cref="SoundMenu.Measured(FmPatch, double?, bool)"/> hears it.</summary>
    private static double Heard(FmPatch patch, int? lfoRate, double hz, bool loops) =>
        VoiceLevel.Heard(FmNoteRenderer.Render(patch, FmEvaluator.Lfo(patch, lfoRate), hz, SampleAnalyzer.RegionSeconds), SampleAnalyzer.AnalysisRateHz, loops);

    /// <summary>
    /// <paramref name="patch"/> with no LFO depth: it was judged with the LFO off, and the song's LFO may run for another
    /// sample's fit.
    /// </summary>
    private static FmPatch WithoutLfo(FmPatch patch) => patch.UsesLfo ? patch with { Fms = 0, Ams = 0 } : patch;

    /// <summary>
    /// <paramref name="patch"/> with SSG-EG off on every operator (the same patch when none uses it). The fitter judges one
    /// note on a chip just reset, but in a song the SSG-EG envelope's state carries from note to note: in round 39 it left
    /// FunkyChristmas's Fit 8.1 some 30 dB too quiet for five seconds of the melody, where the same patch without it held
    /// within 4 dB of the sample. So the simple mapper plays fits without it, judged again as they will play.
    /// </summary>
    internal static FmPatch WithoutSsgEg(FmPatch patch) =>
        patch.Operators.Any(o => o.SsgEg != 0) ? patch with { Operators = [.. patch.Operators.Select(o => o with { SsgEg = 0 })] } : patch;

    /// <summary>A pitch region a sample is judged on, weighed by its notes.</summary>
    private sealed record Region(int Index, NoteTarget Target, int Notes)
    {
        /// <summary>The region's weight: its notes, and at least one, so a sample whose notes weren't counted is still judged.</summary>
        public double Weight => Math.Max(1, Notes);
    }

    /// <summary>One fit a sample may play, made for pitch region <paramref name="FitRegion"/>, with its note-weighted distance over all regions.</summary>
    private sealed record Candidate(int FitRegion, FmFit Fit, double Db);

    /// <summary>
    /// One sample's candidates and its built-in patch's distance; <paramref name="Floor"/> is the closest it gets without
    /// the LFO, and <paramref name="LevelHz"/> the pitch of its busiest region, where a fit's heard level is measured.
    /// </summary>
    private sealed record Judged(int Sample, IReadOnlyList<Candidate> Candidates, double BuiltInDb, double Floor, double Weight, int Regions, int Notes, double LevelHz, bool Loops);
}
