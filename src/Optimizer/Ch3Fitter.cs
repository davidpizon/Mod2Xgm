using System.Diagnostics;
using System.Globalization;
using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;

namespace Mod2Xgm.Optimizer;

/// <summary>Where a CH3 voice comes from (docs/adr/0073-ch3-locked-pairs.md).</summary>
internal enum Ch3VoiceSource
{
    /// <summary>A new 2-operator fit: a structure search over the two multiples, then the level fit on the voice's operators.</summary>
    Algorithm4,

    /// <summary>The closest carrier of the sample's T8.2 fit with the modulator that feeds it (or alone), then the same level fit.</summary>
    CutDown,
}

/// <summary>One 2-operator voice fitted to one pitch region of one sample, on one half of channel 3.</summary>
/// <param name="Source">How it was made.</param>
/// <param name="OnOps12">True on OP1→OP2 (the half with the feedback), false on OP3→OP4.</param>
/// <param name="Patch">An algorithm-4 patch with the voice on its half and the other half silent.</param>
/// <param name="Score">How close it sounds to the sample's region, alone.</param>
internal sealed record Ch3Voice(Ch3VoiceSource Source, bool OnOps12, FmPatch Patch, NoteScore Score);

/// <summary>
/// A CH3 special-mode candidate (plan T8.5b, docs/adr/0073-ch3-locked-pairs.md): a locked pair played as two 2-operator
/// voices on channel 3, for the onsets that play both tracks in one pair of pitch regions. Offered for DMF and XGM2 only
/// (<see cref="Ch3Fitter.Targets"/>).
/// </summary>
/// <param name="Pair">The locked pair.</param>
/// <param name="RegionA">The first track's pitch region (index into its <see cref="SampleAnalysis.Regions"/>).</param>
/// <param name="RegionB">The second track's.</param>
/// <param name="Occurrences">Onsets of the pair in these regions.</param>
/// <param name="HzA">The first voice's pitch at the first such onset, where it is scored.</param>
/// <param name="HzB">The second voice's.</param>
/// <param name="WindowSeconds">How much of the onset is scored: until the first of the two notes ends, 0.1–1 s.</param>
/// <param name="Patch">The channel's patch: algorithm 4, one voice on each half, their levels balanced as in the song.</param>
/// <param name="AOnOps12">True when the first track's voice is on OP1→OP2 (with the feedback).</param>
/// <param name="VoiceA">The first track's voice, before balancing.</param>
/// <param name="VoiceB">The second track's.</param>
/// <param name="Score">How close the two voices together sound to the MOD renderer's mix of the two notes (mel bands only).</param>
/// <param name="Algorithm4Score">The closest pair of new algorithm-4 fits, by the same measure.</param>
/// <param name="CutDownScore">The closest pair of cut-down T8.2 fits.</param>
internal sealed record Ch3Candidate(
    LockedPair Pair,
    int RegionA,
    int RegionB,
    int Occurrences,
    double HzA,
    double HzB,
    double WindowSeconds,
    FmPatch Patch,
    bool AOnOps12,
    Ch3Voice VoiceA,
    Ch3Voice VoiceB,
    NoteScore Score,
    NoteScore Algorithm4Score,
    NoteScore CutDownScore);

/// <summary>A song's locked pairs and its CH3 candidates.</summary>
/// <param name="Pairs">Every locked pair (<see cref="LockedPairs.Find(SongAnalysis, IReadOnlySet{int})"/>).</param>
/// <param name="Candidates">One per pair and pair of pitch regions with sound.</param>
/// <param name="Renders">FM notes and mixes rendered, the T8.2 fits made here included.</param>
/// <param name="Elapsed">How long it took (for reports only; never changes a result).</param>
internal sealed record Ch3SongFit(IReadOnlyList<LockedPair> Pairs, IReadOnlyList<Ch3Candidate> Candidates, int Renders, TimeSpan Elapsed)
{
    /// <summary>True when it came from the candidate cache (docs/adr/0074-candidate-cache.md); everything else is as made then.</summary>
    public bool Cached { get; init; }
}

/// <summary>
/// Makes the CH3 special-mode candidates of plan T8.5b (docs/multi-pass.md#candidates, docs/adr/0073-ch3-locked-pairs.md):
/// for each locked pair, two 2-operator voices on channel 3's algorithm 4. Each track's voice is fitted to its own sample's
/// pitch region on each half, twice: a new algorithm-4 fit and a cut-down T8.2 fit. Every combination of halves and sources
/// is balanced to the song's levels and judged together against the MOD renderer's mix of the pair; the closest is kept.
/// No LFO: the two voices would share channel 3's depths.
/// </summary>
internal static class Ch3Fitter
{
    /// <summary>The outputs CH3 special mode is offered for: DMF (system <c>0x42</c>) and XGM2; XGM v1 waits for a probe ROM (plan T11.1).</summary>
    public static IReadOnlyList<PcmTarget> Targets { get; } = [PcmTarget.Dmf, PcmTarget.Xgm2];

    /// <summary>Structures the 2-operator search hands to the level fit.</summary>
    private const int StructuresPerVoice = 2;

    /// <summary>Scores look at least this much of a pair: about two of the scorer's spectrum frames.</summary>
    private const double MinScoreSeconds = 0.1;

    /// <summary>TL of one step, in dB (docs/genesis-audio.md).</summary>
    private const double TlStepDb = 0.75;

    /// <summary>A level under this, in dB, is silence: nothing to balance against.</summary>
    private const double SilentDb = -100;

    /// <summary>The operators of each half: OP1→OP2 and OP3→OP4.</summary>
    private static readonly int[] Ops12 = [0, 1];

    private static readonly int[] Ops34 = [2, 3];

    /// <summary>An operator that makes no sound: the other half of a voice fitted alone.</summary>
    private static readonly FmOperator Silent = new(1, 0, 127, 0, 31, false, 0, 0, 0, 15);

    /// <summary>
    /// Per algorithm, each carrier (0 = OP1) and the operators that feed it directly: what a cut-down voice keeps
    /// (docs/genesis-audio.md).
    /// </summary>
    private static readonly (int Carrier, int[] Modulators)[][] Branches =
    [
        [(3, [2])],
        [(3, [2])],
        [(3, [0, 2])],
        [(3, [1, 2])],
        [(1, [0]), (3, [2])],
        [(1, [0]), (2, [0]), (3, [0])],
        [(1, [0]), (2, []), (3, [])],
        [(0, []), (1, []), (2, []), (3, [])],
    ];

    /// <summary>True when CH3 special mode may be offered for <paramref name="target"/>.</summary>
    public static bool Allows(PcmTarget target) => Targets.Contains(target);

    /// <summary>The locked pairs of <paramref name="song"/> and their CH3 candidates.</summary>
    /// <param name="song">The song as analyzed.</param>
    /// <param name="options">How the voices are fitted (render limit, SSG-EG; the LFO is never used).</param>
    /// <param name="fmFits">The song's T8.2 fits, cut down where given; missing regions are fitted here.</param>
    /// <param name="keepOut">Samples that never go on FM (<c>--require-dac</c>, <c>--require-psg</c>).</param>
    public static Ch3SongFit Fit(SongAnalysis song, FmFitOptions options, IReadOnlyList<FmSampleFit>? fmFits = null, IReadOnlySet<int>? keepOut = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(options);
        Stopwatch clock = Stopwatch.StartNew();
        IReadOnlyList<LockedPair> pairs = LockedPairs.Find(song, keepOut);
        Run run = new(song, options, fmFits);
        List<Ch3Candidate> candidates = [];
        foreach (LockedPair pair in pairs)
        {
            SampleAnalysis a = run.Analysis(pair.SampleA);
            SampleAnalysis b = run.Analysis(pair.SampleB);
            foreach (IGrouping<(int A, int B), LockedOnset> regions in pair.Onsets
                .GroupBy(o => (RegionOf(a, o.A.RateHz), RegionOf(b, o.B.RateHz)))
                .OrderBy(g => g.Key.Item1)
                .ThenBy(g => g.Key.Item2))
            {
                if (run.Make(pair, regions.Key.A, regions.Key.B, [.. regions]) is { } candidate)
                {
                    candidates.Add(candidate);
                }
            }
        }

        return new Ch3SongFit(pairs, candidates, run.Renders, clock.Elapsed);
    }

    /// <summary>The pitch region of <paramref name="analysis"/> that holds <paramref name="rateHz"/>, or the nearest by pitch.</summary>
    internal static int RegionOf(SampleAnalysis analysis, double rateHz)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        IReadOnlyList<PitchRegion> regions = analysis.Regions;
        for (int r = 0; r < regions.Count; r++)
        {
            if (rateHz >= regions[r].LowRateHz && rateHz <= regions[r].HighRateHz)
            {
                return r;
            }
        }

        return Enumerable.Range(0, regions.Count).MinBy(r => Math.Abs(Math.Log(rateHz / regions[r].RateHz)));
    }

    /// <summary><paramref name="patch"/> with only one half sounding: the other half's operators silent, and no feedback for OP3→OP4.</summary>
    internal static FmPatch Half(FmPatch patch, bool onOps12)
    {
        ArgumentNullException.ThrowIfNull(patch);
        FmOperator[] ops = [.. patch.Operators.Select((o, i) => (i < 2) == onOps12 ? o with { Am = false } : Silent)];
        return new FmPatch(patch.Name, 4, onOps12 ? patch.Feedback : 0, ops);
    }

    /// <summary>The 2-operator voices a 4-operator <paramref name="patch"/> cuts down to, on the half <paramref name="onOps12"/>: each carrier with each modulator feeding it, or alone.</summary>
    internal static IEnumerable<FmPatch> CutDowns(FmPatch patch, bool onOps12)
    {
        ArgumentNullException.ThrowIfNull(patch);
        foreach ((int carrier, int[] modulators) in Branches[patch.Algorithm])
        {
            foreach (int? modulator in modulators.Select(m => (int?)m).Append(null))
            {
                FmOperator[] ops = [Silent, Silent, Silent, Silent];
                ops[onOps12 ? 0 : 2] = modulator is { } m ? patch.Operators[m] with { Am = false } : Silent;
                ops[onOps12 ? 1 : 3] = patch.Operators[carrier] with { Am = false };

                // OP1's feedback goes with it, if it lands on OP1 again. Two operators cut from four may share a factor
                // the others didn't (2 and 2), so the multiples are divided down to play at the note.
                if (FmPitch.Normalized(new FmPatch(patch.Name, 4, onOps12 && modulator == 0 ? patch.Feedback : 0, ops)) is { } voice)
                {
                    yield return voice;
                }
            }
        }
    }

    /// <summary>The channel's patch: <paramref name="v12"/>'s half OP1→OP2 (and its feedback), <paramref name="v34"/>'s OP3→OP4.</summary>
    internal static FmPatch Combine(Ch3Voice v12, Ch3Voice v34, string name)
    {
        ArgumentNullException.ThrowIfNull(v12);
        ArgumentNullException.ThrowIfNull(v34);
        IReadOnlyList<FmOperator> a = v12.Patch.Operators;
        IReadOnlyList<FmOperator> b = v34.Patch.Operators;
        return new FmPatch(name, 4, v12.Patch.Feedback, [a[0], a[1], b[2], b[3]]);
    }

    /// <summary>RMS level of <paramref name="sound"/> in dB of full scale; very low for silence.</summary>
    private static double Db(float[] sound)
    {
        double sum = 0;
        foreach (float s in sound)
        {
            sum += s * s;
        }

        return 10 * Math.Log10((sum / Math.Max(1, sound.Length)) + 1e-20);
    }

    /// <summary><paramref name="seconds"/> of <paramref name="sound"/> (at the analysis rate), or what there is.</summary>
    private static float[] Head(float[] sound, double seconds) =>
        sound[..Math.Min(sound.Length, (int)Math.Round(seconds * SampleAnalyzer.AnalysisRateHz))];

    /// <summary>One song's fits: evaluators, T8.2 fits and voices are made once and shared by every pair that needs them.</summary>
    private sealed class Run(SongAnalysis song, FmFitOptions options, IReadOnlyList<FmSampleFit>? fmFits)
    {
        private readonly Dictionary<(int Sample, int Region), FmEvaluator> _evaluators = [];
        private readonly Dictionary<(int Sample, int Region), FmRegionFit> _fits = [];
        private readonly Dictionary<(int Sample, int Region, bool OnOps12, Ch3VoiceSource Source), Ch3Voice> _voices = [];
        private readonly Dictionary<(Ch3Voice Voice, double Hz, double Seconds), double> _levels = [];
        private int _ownRenders;

        /// <summary>Notes and mixes rendered so far.</summary>
        public int Renders => _ownRenders + _evaluators.Values.Sum(e => e.Renders);

        /// <summary>The analysis of song sample <paramref name="sample"/>.</summary>
        public SampleAnalysis Analysis(int sample) => song.Samples.First(s => s.Sample == sample);

        /// <summary>The candidate for <paramref name="onsets"/> of <paramref name="pair"/> in regions <paramref name="ra"/> and <paramref name="rb"/>, or <c>null</c> when either region has no sound to fit.</summary>
        public Ch3Candidate? Make(LockedPair pair, int ra, int rb, IReadOnlyList<LockedOnset> onsets)
        {
            PitchRegion regionA = Analysis(pair.SampleA).Regions[ra];
            PitchRegion regionB = Analysis(pair.SampleB).Regions[rb];
            if (NoteTarget.Of(regionA) is not { } ta || NoteTarget.Of(regionB) is not { } tb)
            {
                return null;
            }

            LockedOnset first = onsets[0];
            double hzA = ta.NoteHz * first.A.RateHz / regionA.RateHz;
            double hzB = tb.NoteHz * first.B.RateHz / regionB.RateHz;
            int start = first.A.FirstTick;
            int end = Math.Min(first.A.FirstTick + first.A.Ticks, first.B.FirstTick + first.B.Ticks);
            double window = Time(end) - Time(start);
            double seconds = Math.Clamp(window, MinScoreSeconds, SampleAnalyzer.RegionSeconds);
            float[] mix = Head(NoteMix.Render(song.Song, song.Playback, [first.A, first.B], start, end, seconds), seconds);
            NoteScorer scorer = new(new NoteTarget(hzA, null, SampleAnalyzer.Spectrum(mix, null)));
            double modA = Db(Head(NoteMix.Render(song.Song, song.Playback, [first.A], start, end, seconds), seconds));
            double modB = Db(Head(NoteMix.Render(song.Song, song.Playback, [first.B], start, end, seconds), seconds));
            _ownRenders += 3;

            string name = string.Create(CultureInfo.InvariantCulture, $"Ch3 {pair.SampleA + 1}.{ra + 1}+{pair.SampleB + 1}.{rb + 1}");
            List<(FmPatch Patch, bool AOnOps12, Ch3Voice A, Ch3Voice B, NoteScore Score)> tried = [];
            foreach (bool aOnOps12 in (bool[])[true, false])
            {
                foreach (Ch3VoiceSource sourceA in (Ch3VoiceSource[])[Ch3VoiceSource.Algorithm4, Ch3VoiceSource.CutDown])
                {
                    foreach (Ch3VoiceSource sourceB in (Ch3VoiceSource[])[Ch3VoiceSource.Algorithm4, Ch3VoiceSource.CutDown])
                    {
                        Ch3Voice va = Voice(pair.SampleA, ra, ta, aOnOps12, sourceA);
                        Ch3Voice vb = Voice(pair.SampleB, rb, tb, !aOnOps12, sourceB);
                        FmPatch patch = aOnOps12 ? Combine(va, vb, name) : Combine(vb, va, name);
                        double louderA = Level(va, hzA, seconds) - modA - (Level(vb, hzB, seconds) - modB);
                        patch = Balanced(patch, aOnOps12, louderA, modA, modB);
                        float[] played = FmNoteRenderer.RenderCh3(patch, aOnOps12 ? hzA : hzB, aOnOps12 ? hzB : hzA, seconds);
                        _ownRenders++;
                        tried.Add((patch, aOnOps12, va, vb, scorer.Score(Head(played, seconds))));
                    }
                }
            }

            // MinBy keeps the first of equals: A on OP1→OP2 first, new fits before cut-down ones.
            (FmPatch Patch, bool AOnOps12, Ch3Voice A, Ch3Voice B, NoteScore Score) best = tried.MinBy(t => t.Score.MelDb);
            NoteScore algorithm4 = tried.Where(t => t.A.Source == Ch3VoiceSource.Algorithm4 && t.B.Source == Ch3VoiceSource.Algorithm4).MinBy(t => t.Score.MelDb).Score;
            NoteScore cutDown = tried.Where(t => t.A.Source == Ch3VoiceSource.CutDown && t.B.Source == Ch3VoiceSource.CutDown).MinBy(t => t.Score.MelDb).Score;
            return new Ch3Candidate(pair, ra, rb, onsets.Count, hzA, hzB, seconds, best.Patch, best.AOnOps12, best.A, best.B, best.Score, algorithm4, cutDown);
        }

        /// <summary>
        /// <paramref name="patch"/> with the voice that is <paramref name="louderA"/> dB too loud against the other (the first
        /// track's when positive) turned down by its carrier's TL, so the two sound at the song's balance.
        /// </summary>
        private static FmPatch Balanced(FmPatch patch, bool aOnOps12, double louderA, double modA, double modB)
        {
            int steps = (int)Math.Round(Math.Abs(louderA) / TlStepDb);
            if (modA < SilentDb || modB < SilentDb || steps == 0)
            {
                return patch;
            }

            // Carriers: OP2 for OP1→OP2, OP4 for OP3→OP4.
            int carrier = (louderA > 0) == aOnOps12 ? 1 : 3;
            FmOperator[] ops = [.. patch.Operators];
            ops[carrier] = ops[carrier] with { Tl = Math.Min(127, ops[carrier].Tl + steps) };
            return patch with { Operators = ops };
        }

        /// <summary>The level of <paramref name="voice"/> alone at <paramref name="hz"/> over <paramref name="seconds"/>, in dB.</summary>
        private double Level(Ch3Voice voice, double hz, double seconds)
        {
            if (!_levels.TryGetValue((voice, hz, seconds), out double db))
            {
                db = Db(FmNoteRenderer.Render(voice.Patch, null, hz, seconds));
                _levels[(voice, hz, seconds)] = db;
                _ownRenders++;
            }

            return db;
        }

        /// <summary>The voice of <paramref name="sample"/>'s region <paramref name="region"/> on one half, from <paramref name="source"/>; fitted once.</summary>
        private Ch3Voice Voice(int sample, int region, NoteTarget target, bool onOps12, Ch3VoiceSource source)
        {
            if (_voices.TryGetValue((sample, region, onOps12, source), out Ch3Voice? known))
            {
                return known;
            }

            FmEvaluator evaluator = Evaluator(sample, region, target);
            FmRegionFit fit = RegionFit(sample, region, target);
            string name = string.Create(CultureInfo.InvariantCulture, $"Ch3 {sample + 1}.{region + 1}");
            IEnumerable<FmPatch> starts = source == Ch3VoiceSource.Algorithm4
                ? Structures(evaluator, fit.Library.Patch with { Name = name }, onOps12)
                : [CutDowns(fit.NoLfo.Patch with { Name = name }, onOps12).MinBy(p => evaluator.Search(p, null))!]; // Every algorithm has a carrier.

            Ch3Voice voice = starts
                .Select(p => FmLocalSearch.Refine(evaluator, new FmVoiceSetting(p, null), false, options.SsgEg, options.MaxRenders, onOps12 ? Ops12 : Ops34).Patch)
                .Select(p => new Ch3Voice(source, onOps12, p, evaluator.Judge(p, null)))
                .MinBy(v => v.Score.MelDb)!; // At least one start: the structures always include multiples 1 and 1.
            _voices[(sample, region, onOps12, source)] = voice;
            return voice;
        }

        /// <summary>
        /// The <see cref="StructuresPerVoice"/> best 2-operator structures on one half by the quick score: the carrier on
        /// the target's strong harmonics, the modulator on a small multiple, no common factor, envelopes from <paramref name="seed"/>.
        /// </summary>
        private IEnumerable<FmPatch> Structures(FmEvaluator evaluator, FmPatch seed, bool onOps12)
        {
            int[] carrierMults = FmExhaustiveSearch.CarrierMults(evaluator.Target);
            IReadOnlyList<int> modulatorMults = FmExhaustiveSearch.ModulatorMults;
            List<(FmPatch Patch, int Prior)> structures = [];
            for (int c = 0; c < carrierMults.Length; c++)
            {
                for (int m = 0; m < modulatorMults.Count; m++)
                {
                    int cm = carrierMults[c];
                    int mm = modulatorMults[m];
                    if (FmExhaustiveSearch.Gcd(cm, mm) != 1)
                    {
                        continue;
                    }

                    FmStructure structure = onOps12
                        ? new FmStructure(4, seed.Feedback, mm, cm, 1, 1, FmStructure.DefaultModulatorTl)
                        : new FmStructure(4, 0, 1, 1, mm, cm, FmStructure.DefaultModulatorTl);
                    structures.Add((Half(structure.Patch(seed, seed.Name), onOps12), c + m));
                }
            }

            int begin = evaluator.Renders;
            List<(FmPatch Patch, double Score)> scored = [];
            foreach ((FmPatch patch, _) in structures.OrderBy(s => s.Prior))
            {
                if (options.MaxRenders is { } max && evaluator.Renders - begin >= max)
                {
                    break;
                }

                scored.Add((patch, evaluator.Quick(patch, null)));
            }

            return scored.OrderBy(s => s.Score).Take(StructuresPerVoice).Select(s => s.Patch);
        }

        private FmEvaluator Evaluator(int sample, int region, NoteTarget target)
        {
            if (!_evaluators.TryGetValue((sample, region), out FmEvaluator? evaluator))
            {
                evaluator = new FmEvaluator(target);
                _evaluators[(sample, region)] = evaluator;
            }

            return evaluator;
        }

        /// <summary>The T8.2 fit of the region: from <c>fmFits</c> when given, otherwise fitted here once.</summary>
        private FmRegionFit RegionFit(int sample, int region, NoteTarget target)
        {
            if (_fits.TryGetValue((sample, region), out FmRegionFit? fit))
            {
                return fit;
            }

            fit = fmFits?.FirstOrDefault(f => f.Sample == sample)?.Regions.FirstOrDefault(r => r.Region == region);
            if (fit is null)
            {
                fit = FmFitter.FitRegion(region, target, options, string.Create(CultureInfo.InvariantCulture, $"Fit {sample + 1}.{region + 1}"));
                _ownRenders += fit.Renders;
            }

            _fits[(sample, region)] = fit;
            return fit;
        }

        /// <summary>When tick <paramref name="t"/> starts; the song's end for the tick after its last.</summary>
        private double Time(int t) => t < song.Playback.Ticks.Count ? song.Playback.Ticks[t].TimeSeconds : song.Playback.DurationSeconds;
    }
}
