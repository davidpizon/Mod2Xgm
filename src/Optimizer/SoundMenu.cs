using System.Globalization;
using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;
using Mod2Xgm.Output;

namespace Mod2Xgm.Optimizer;

/// <summary>What one pitch region of a sample can sound like, with how far each sound is from it (log-mel distance in dB).</summary>
/// <param name="LowRateHz">The lowest playback rate in the region.</param>
/// <param name="HighRateHz">The highest playback rate in the region.</param>
/// <param name="Patch">The fitted FM patch without the LFO (the plan has no LFO yet), or <c>null</c> when FM wasn't fitted.</param>
/// <param name="FmDb">How far <paramref name="Patch"/> is.</param>
/// <param name="ToneDb">How far a PSG tone is, or <c>null</c> when the note is below the tone channels or the PSG wasn't fitted.</param>
/// <param name="NoiseDb">How far the PSG's hiss at the plan's fixed rate is, or <c>null</c> when the PSG wasn't fitted.</param>
/// <param name="Second">A second FM voice that sounds together with <paramref name="Patch"/>, for a chord (docs/adr/0089-fm-layer.md), or <c>null</c>.</param>
/// <param name="FirstLevel">The factor on <paramref name="Patch"/>'s level when <paramref name="Second"/> sounds with it, as the pair's share of its level.</param>
/// <param name="SpreadCents">How far apart the two voices play, half either side of the note.</param>
/// <param name="Features">How <paramref name="Patch"/> compares with the sample, measured several ways, for its FM confidence
/// (docs/adr/0096-fm-confidence.md); <c>null</c> without a patch or a pitch to render it at.</param>
/// <param name="Chord">The chord's notes as FM voices of their own (docs/adr/0114-fm-chords.md), or <c>null</c> when the region
/// isn't a chord, chords weren't asked for, or the voices together are further from it than <paramref name="Patch"/> alone.</param>
internal sealed record RegionSounds(double LowRateHz, double HighRateHz, FmPatch? Patch, double? FmDb, double? ToneDb, double? NoiseDb, FmPatch? Second = null, double FirstLevel = 1, double SpreadCents = 0, FmRegionFeatures? Features = null, ChordVoices? Chord = null);

/// <summary>One note of a chord sample, played by an FM voice of its own (docs/adr/0114-fm-chords.md).</summary>
/// <param name="Harmonic">Which harmonic of the root the analysis measures the note is (the root is 1); 0 for a note read
/// from a loop with no one cycle, which isn't a harmonic of anything (docs/adr/0135-loop-chords.md).</param>
/// <param name="Level">The factor on the voice's level, as its share of the chord: 1 for the loudest note.</param>
internal sealed record ChordTone(int Harmonic, double Level)
{
    /// <summary>How far the note is above the root the sample's FM note plays at, in cents: the harmonic's interval, unless set.</summary>
    public double Cents { get; init; } = Harmonic > 0 ? 1200 * Math.Log2(Harmonic) : 0;
}

/// <summary>A chord sample's notes played as FM voices of one patch, each at its own pitch (docs/adr/0114-fm-chords.md).</summary>
/// <param name="Patch">The patch every voice plays; its <see cref="FmPatch.HeardRms"/> is the whole chord's level, so each voice plays as its share.</param>
/// <param name="Tones">The notes, low to high.</param>
/// <param name="MelDb">How far the voices together are from the sample (log-mel distance in dB).</param>
internal sealed record ChordVoices(FmPatch Patch, IReadOnlyList<ChordTone> Tones, double MelDb);

/// <summary>What the allocator can play one sample with.</summary>
/// <param name="Sample">Index into <c>Song.Samples</c>.</param>
/// <param name="Percussive">How much it hits (<see cref="SampleAnalysis.Percussive"/>), 0–1.</param>
/// <param name="Regions">Its pitch regions, low to high.</param>
/// <param name="RecordingDb">For each rate the song starts it at, how far the stored copy is; empty when PCM wasn't fitted.</param>
/// <param name="SteadyPitch">False when it has no pitch a tuned voice could hold (<see cref="SoundMenu.HasSteadyPitch"/>): such a voice plays it as a wrong note (David, by ear).</param>
internal sealed record SampleSounds(int Sample, double Percussive, IReadOnlyList<RegionSounds> Regions, IReadOnlyDictionary<double, double> RecordingDb, bool SteadyPitch = true)
{
    /// <summary>The region holding <paramref name="rateHz"/>, or the nearest by pitch; <c>null</c> when it has none.</summary>
    public RegionSounds? RegionAt(double rateHz) => RegionIndexAt(rateHz) is { } r ? Regions[r] : null;

    /// <summary>The index in <see cref="Regions"/> of <see cref="RegionAt"/>; <c>null</c> when it has none.</summary>
    public int? RegionIndexAt(double rateHz) => Regions.Count == 0
        ? null
        : Enumerable.Range(0, Regions.Count).OrderBy(i => OctavesOutside(Regions[i], rateHz)).First();

    private static double OctavesOutside(RegionSounds r, double rateHz) =>
        rateHz < r.LowRateHz ? Math.Log2(r.LowRateHz / rateHz) : rateHz > r.HighRateHz ? Math.Log2(rateHz / r.HighRateHz) : 0;
}

/// <summary>
/// The candidates of plan M8 that the greedy allocator chooses from (docs/adr/0087-greedy-allocator.md): for each sample,
/// a fitted FM patch and PSG sounds per pitch region and the plain PCM copies at every pitch played.
/// </summary>
internal static class SoundMenu
{
    /// <summary>
    /// Most notes each step of an FM fit renders in a normal conversion (David: cap the effort; the value is measured,
    /// docs/adr/0087-greedy-allocator.md). <c>--time-budget</c> (plan T9.5) replaces it.
    /// </summary>
    public const int FmRendersPerStep = 300;

    /// <summary>Most a sample's pitch may wander for a tuned voice to play it: xlr8's snare wanders ±50 cents, the instruments of the songs David judged at most ±9.</summary>
    private const double MaxSteadyCents = 25;

    /// <summary>True when <paramref name="analysis"/> shows a pitch that stays put: not a drum or noise, with a measured pitch inside <see cref="MaxSteadyCents"/>.</summary>
    internal static bool HasSteadyPitch(SampleAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return analysis.Kind is not (SampleKind.Drum or SampleKind.Noise) && analysis.Pitch is { DeviationCents: <= MaxSteadyCents };
    }

    /// <summary>How far below a sample's loudest harmonic its two lowest must be, in dB, for it to be a chord: the note the analysis finds is then the notes' common root, which doesn't sound. kc's and cancan's chords measure 34 dB or more; cancan's Banjo, the nearest thing that isn't a chord, 10 dB.</summary>
    private const double ChordGapDb = 20;

    /// <summary>How far apart a chord's two voices play, in cents: what kc's chords measure between the two peaks of a partial, so the voices beat as the sample does (docs/adr/0089-fm-layer.md).</summary>
    private const double ChordSpreadCents = 22;

    /// <summary>How far a chord's two voices together sit under the sample, in dB: David preferred this on kc, where the chords overpowered the melody, and couldn't separate it from full level on cancan (docs/adr/0089-fm-layer.md).</summary>
    private const double ChordDownDb = 3;

    /// <summary>
    /// True when the sound in <paramref name="harmonicsDb"/> is several notes stored as one sample (kc's SynthFMaj is C-F-A,
    /// harmonics 3, 4 and 5 of the root the analysis measures): its loudest harmonic is the third or above, and the two
    /// lowest are well under it (docs/adr/0089-fm-layer.md).
    /// </summary>
    internal static bool IsChord(IReadOnlyList<float[]> harmonicsDb)
    {
        ArgumentNullException.ThrowIfNull(harmonicsDb);
        if (harmonicsDb.Count == 0 || harmonicsDb[0].Length < 3)
        {
            return false;
        }

        double[] db = AverageDb(harmonicsDb);
        double loudest = db.Max();
        return Array.IndexOf(db, loudest) >= 2 && db[0] <= loudest - ChordGapDb && db[1] <= loudest - ChordGapDb;
    }

    /// <summary>Each harmonic's level over the frames of <paramref name="harmonicsDb"/>, in dB: the frames' power averaged.</summary>
    private static double[] AverageDb(IReadOnlyList<float[]> harmonicsDb) =>
        [.. Enumerable.Range(0, harmonicsDb[0].Length).Select(h => 10 * Math.Log10(harmonicsDb.Average(frame => Math.Pow(10, frame[h] / 10))))];

    /// <summary>How far under the loudest harmonic a chord's note may be and still count as one of its notes, in dB (docs/adr/0114-fm-chords.md).</summary>
    private const double ChordToneDb = 15;

    /// <summary>The highest harmonic of the root a chord's note is looked for at: four octaves up.</summary>
    private const int MaxChordHarmonic = 16;

    /// <summary>Most notes a chord plays with: as many as one FM channel has operators, and more than the free channels usually hold.</summary>
    private const int MaxChordTones = 4;

    /// <summary>
    /// How much quieter than a lower note a harmonic of it must be to count as that note's overtone rather than a note of its
    /// own, in dB: an overtone falls away, a note played with it doesn't (docs/adr/0114-fm-chords.md).
    /// </summary>
    private const double OvertoneDb = 3;

    /// <summary>
    /// The notes of the chord in <paramref name="harmonicsDb"/>, as harmonics of the root the analysis measures, low to high
    /// (docs/adr/0114-fm-chords.md): each harmonic within <see cref="ChordToneDb"/> of the loudest, unless it is an octave or
    /// fifth of a lower note (a ratio of 2s and 3s) and <see cref="OvertoneDb"/> or more under it. kc's SynthFMaj, C-F-A, is
    /// harmonics 3, 4 and 5; 6, 8 and 10 are those notes' own overtones. A third or seventh (a ratio of 5 or 7) is never an
    /// overtone, so a chord that sounds its root keeps its third (EM2's MELLOW MAJ7: 1, 4, 5 and 6). Notes that are all
    /// octaves and fifths of one pitch are a bright note, not a chord. EM2's chords have a bass note or a loud second
    /// harmonic, which <see cref="IsChord"/> doesn't take, so it isn't asked: the chord's voices must still come closer to the
    /// sample than one voice does. At most <see cref="MaxChordTones"/> are kept: the loudest of each pitch first, then the
    /// loudest octaves of them. Empty when there's no chord.
    /// </summary>
    internal static IReadOnlyList<int> ChordTones(IReadOnlyList<float[]> harmonicsDb)
    {
        ArgumentNullException.ThrowIfNull(harmonicsDb);
        if (harmonicsDb.Count == 0 || harmonicsDb[0].Length == 0)
        {
            return [];
        }

        double[] db = AverageDb(harmonicsDb);
        double loudest = db.Max();
        List<int> tones = [];
        for (int h = 1; h <= Math.Min(db.Length, MaxChordHarmonic); h++)
        {
            if (db[h - 1] >= loudest - ChordToneDb && !tones.Any(t => h % t == 0 && Colour(h / t) == 1 && db[h - 1] <= db[t - 1] - OvertoneDb))
            {
                tones.Add(h);
            }
        }

        if (tones.Select(Colour).Distinct().Count() < 2)
        {
            return [];
        }

        // The loudest note of each pitch first, then octaves of them: a third outranks a louder doubled root.
        return [.. tones
            .OrderByDescending(t => db[t - 1]).ThenBy(t => t)
            .Select((t, i) => (Tone: t, First: tones.Where(u => OddPart(u) == OddPart(t)).MaxBy(u => db[u - 1]) == t, Rank: i))
            .OrderByDescending(x => x.First).ThenBy(x => x.Rank)
            .Take(MaxChordTones)
            .Select(x => x.Tone)
            .Order()];
    }

    /// <summary><paramref name="n"/> without its factors of 2: harmonics with the same odd part are octaves of one pitch.</summary>
    private static int OddPart(int n)
    {
        while (n % 2 == 0)
        {
            n /= 2;
        }

        return n;
    }

    /// <summary>
    /// <paramref name="n"/> without its factors of 2 and 3. A harmonic of a note at a ratio whose colour is 1 is an octave or
    /// fifth of it, as a single note's own harmonics are; a ratio of colour 5 or 7 is a third or seventh, a chord's note.
    /// </summary>
    private static int Colour(int n)
    {
        n = OddPart(n);
        while (n % 3 == 0)
        {
            n /= 3;
        }

        return n;
    }

    /// <summary>
    /// What one note of a chord aims at: <paramref name="chord"/>'s harmonics that are <paramref name="tone"/>'s own, with
    /// <paramref name="tone"/> times the root as the fundamental (docs/adr/0114-fm-chords.md). A harmonic two notes share
    /// counts for both, so the patch fitted to it is a little bright; the voices together are judged against the chord.
    /// </summary>
    internal static NoteTarget ToneTarget(NoteTarget chord, int tone)
    {
        ArgumentNullException.ThrowIfNull(chord);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tone);
        if (chord is not { FundamentalHz: { } root, Spectrum.HarmonicsDb: { } harmonics })
        {
            throw new ArgumentException("A chord's target needs a pitch and harmonics", nameof(chord));
        }

        float[][] own = [.. harmonics.Select(frame => Enumerable.Range(1, frame.Length / tone).Select(k => frame[(k * tone) - 1]).ToArray())];
        return new NoteTarget(root * tone, root * tone, chord.Spectrum with { HarmonicsDb = own });
    }

    /// <summary>
    /// The note of <paramref name="tones"/> whose patch is fitted: the one that shares the fewest harmonics with the others,
    /// so the least of what it aims at is theirs; the lowest of equals.
    /// </summary>
    internal static int ReferenceTone(IReadOnlyList<int> tones, int harmonics)
    {
        ArgumentNullException.ThrowIfNull(tones);
        return tones
            .OrderBy(t => Enumerable.Range(1, harmonics / t).Count(k => tones.Any(u => u != t && k * t % u == 0)))
            .ThenBy(t => t)
            .First();
    }

    /// <summary>
    /// The notes of <paramref name="tones"/> rendered with <paramref name="patch"/> over the root <paramref name="rootHz"/>,
    /// each at its level, mixed: what the chord's voices sound like together.
    /// </summary>
    internal static float[] RenderChord(FmPatch patch, double rootHz, IReadOnlyList<ChordTone> tones)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(tones);
        float[] mix = [];
        foreach (ChordTone tone in tones)
        {
            float[] voice = FmNoteRenderer.Render(patch, null, rootHz * tone.Harmonic, SampleAnalyzer.RegionSeconds);
            for (int i = 0; i < voice.Length; i++)
            {
                voice[i] *= (float)tone.Level;
            }

            mix = mix.Length == 0 ? voice : NoteMix.Add(mix, voice);
        }

        return mix;
    }

    /// <summary>
    /// <paramref name="region"/>'s chord as FM voices of one patch, one per note (docs/adr/0114-fm-chords.md): the patch is
    /// fitted to <see cref="ReferenceTone"/>'s harmonics, each of the fitter's tries is judged by how close the voices together
    /// come to the sample, and the closest is kept. <c>null</c> when the region isn't a chord, or the voices together are no
    /// closer than <paramref name="singleDb"/>, the one-voice fit's distance.
    /// </summary>
    private static ChordVoices? Chord(PitchRegion region, int r, double singleDb, bool loops, FmFitOptions options, CandidateCache? cache, string name)
    {
        if (NoteTarget.Of(region) is not { FundamentalHz: { } root, Spectrum.HarmonicsDb: { Count: > 0 } harmonics } target
            || ChordTones(harmonics) is not { Count: > 0 } notes)
        {
            return null;
        }

        double[] db = AverageDb(harmonics);
        double loudest = notes.Max(t => db[t - 1]);
        ChordTone[] tones = [.. notes.Select(t => new ChordTone(t, Math.Pow(10, (db[t - 1] - loudest) / 20)))];
        NoteTarget aim = ToneTarget(target, ReferenceTone(notes, harmonics[0].Length));
        FmRegionFit fit = cache is null
            ? FmFitter.FitRegion(r, aim, options, name)
            : cache.FmRegion(aim, options, r, name, () => FmFitter.FitRegion(r, aim, options, name));
        NoteScorer scorer = new(target);
        ChordVoices? best = null;
        foreach (FmFit tried in new[] { fit.Library, fit.Exhaustive, fit.Genetic }.OfType<FmFit>().Where(f => f.LfoRate is null))
        {
            FmPatch full = AtFullLevel(tried.Patch);
            float[] mix = RenderChord(full, root, tones);
            double heard = VoiceLevel.Heard(mix, SampleAnalyzer.AnalysisRateHz, loops);
            double melDb = scorer.Score(mix).MelDb;
            if (heard > 0 && (best is null || melDb < best.MelDb))
            {
                best = new ChordVoices(full with { Name = name, HeardRms = heard }, tones, melDb);
            }
        }

        return best is not null && best.MelDb < singleDb ? best : null;
    }

    /// <summary>
    /// A chord's second FM voice: a copy of <paramref name="first"/>, the two <see cref="ChordSpreadCents"/> apart and together
    /// <see cref="ChordDownDb"/> under the sample (docs/adr/0089-fm-layer.md). Its <see cref="FmPatch.HeardRms"/> is the pair's level,
    /// so <see cref="Mapping.PlanVoicing.FmLevel"/> plays it as its share of the pair; the first voice's share comes back as
    /// <c>FirstLevel</c>. None when the region isn't a chord.
    /// </summary>
    internal static (FmPatch? Second, double FirstLevel, double SpreadCents) SecondVoice(PitchRegion region, FmPatch first, bool loops, string name)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(first);
        if (NoteTarget.Of(region) is not { Spectrum.HarmonicsDb: { } harmonics } target || !IsChord(harmonics) || first.HeardRms is not { } alone)
        {
            return (null, 1, 0);
        }

        double down = Math.Pow(2, -ChordSpreadCents / 2400);
        float[] pair = NoteMix.Add(FmNoteRenderer.Render(first, null, target.NoteHz * down, SampleAnalyzer.RegionSeconds), FmNoteRenderer.Render(first, null, target.NoteHz / down, SampleAnalyzer.RegionSeconds));
        double together = VoiceLevel.Heard(pair, SampleAnalyzer.AnalysisRateHz, loops);
        if (together <= 0)
        {
            return (null, 1, 0);
        }

        double quieter = Math.Pow(10, -ChordDownDb / 20);
        return (first with { Name = name, HeardRms = together / quieter }, Math.Min(1, alone / together) * quieter, ChordSpreadCents);
    }

    /// <summary>The TL that mutes an operator (docs/genesis-audio.md): such a carrier stays muted.</summary>
    private const int MutedTl = 127;

    /// <summary>Makes the menu of every analyzed sample: only the sounds its <c>--require-*</c> list allows are fitted.</summary>
    /// <param name="song">The analyzed song.</param>
    /// <param name="target">The output the PCM copies are for.</param>
    /// <param name="options">The user's pins and bans.</param>
    /// <param name="fm">How FM is fitted.</param>
    /// <param name="cache">Where earlier candidates are kept, or <c>null</c> to make them all.</param>
    /// <param name="work">Counts samples done.</param>
    /// <param name="chords">True to fit a chord sample's notes as FM voices of their own (docs/adr/0114-fm-chords.md).</param>
    public static IReadOnlyList<SampleSounds> Make(SongAnalysis song, PcmTarget target, MappingOptions options, FmFitOptions fm, CandidateCache? cache, IWorkCounter work, bool chords = false)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fm);
        ArgumentNullException.ThrowIfNull(work);
        SampleSounds[] made = new SampleSounds[song.Samples.Count];
        work.SetTotal(made.Length);
        Parallel.For(0, made.Length, i =>
        {
            made[i] = Sample(song, song.Samples[i], target, options, fm, cache, chords);
            work.Advance();
        });
        return made;
    }

    /// <summary>
    /// <paramref name="patch"/> with its loudest carrier at TL 0 and the others as far below it as they were. A carrier's TL
    /// only scales the output, so the sound doesn't change, but the voice level can only turn a patch down
    /// (<see cref="PlanVoicing.FmLevel"/>): a fit left quiet would stay quiet, up to 17 dB under its sample
    /// (docs/adr/0087-greedy-allocator.md).
    /// </summary>
    internal static FmPatch AtFullLevel(FmPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        IReadOnlyList<int> carriers = VoiceLevel.Carriers(patch.Algorithm);
        int[] heard = [.. carriers.Select(c => patch.Operators[c].Tl).Where(tl => tl < MutedTl)];
        if (heard.Length == 0 || heard.Min() == 0)
        {
            return patch;
        }

        int lift = heard.Min();
        return patch with
        {
            Operators = [.. patch.Operators.Select((op, i) => carriers.Contains(i) && op.Tl < MutedTl ? op with { Tl = op.Tl - lift } : op)],
        };
    }

    /// <summary>
    /// <paramref name="patch"/> with the level a render of it at <paramref name="hz"/> measures. The level worked out from the
    /// registers takes a slow decay as already over, which left kc's horn and chords 6 dB too loud by David's ear
    /// (docs/adr/0087-greedy-allocator.md). A silent render, or no pitch to render at, leaves that level in use.
    /// </summary>
    internal static FmPatch Measured(FmPatch patch, double? hz, bool loops)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return Measured(patch, Render(patch, hz), loops);
    }

    /// <summary><paramref name="patch"/> with the level <paramref name="render"/> of it measures, as <see cref="Measured(FmPatch, double?, bool)"/>.</summary>
    private static FmPatch Measured(FmPatch patch, float[]? render, bool loops)
    {
        if (render is null)
        {
            return patch;
        }

        double rms = VoiceLevel.Heard(render, SampleAnalyzer.AnalysisRateHz, loops);
        return rms > 0 ? patch with { HeardRms = rms } : patch;
    }

    /// <summary><paramref name="patch"/> played at <paramref name="hz"/> for a region's analysis; <c>null</c> without a pitch.</summary>
    private static float[]? Render(FmPatch patch, double? hz) =>
        hz is { } noteHz ? FmNoteRenderer.Render(patch, null, noteHz, SampleAnalyzer.RegionSeconds) : null;

    private static SampleSounds Sample(SongAnalysis song, SampleAnalysis analysis, PcmTarget target, MappingOptions options, FmFitOptions fmOptions, CandidateCache? cache, bool chords)
    {
        int sample = analysis.Sample;
        bool wantsFm = !options.RequireDac.Contains(sample) && !options.RequirePsg.Contains(sample);
        bool wantsPsg = !options.RequireDac.Contains(sample) && !options.RequireFm.Contains(sample);
        bool wantsPcm = options.UseDac && !options.RequireFm.Contains(sample) && !options.RequirePsg.Contains(sample);
        FmSampleFit? fm = wantsFm ? FmFitter.Fit(analysis, fmOptions, cache) : null;
        PsgSampleFit? psg = wantsPsg ? cache?.Psg(song, analysis) ?? PsgFitter.Fit(analysis, song.Voices) : null;
        PcmSampleFit? pcm = wantsPcm ? cache?.Pcm(song, analysis, target) ?? PcmFitter.Fit(song, analysis, target) : null;

        // The plan's noise is white at a rate set by the sample's brightness, so that is the only noise try it can play.
        Model.Sample played = song.Song.Samples[sample];
        int noiseRate = 2 - PlanVoicing.NoiseNoteOf(played);
        List<RegionSounds> regions = [];
        for (int r = 0; r < analysis.Regions.Count; r++)
        {
            PitchRegion region = analysis.Regions[r];
            FmFit? fit = fm?.Regions.FirstOrDefault(f => f.Region == r)?.NoLfo;
            PsgRegionFit? tries = psg?.Regions.FirstOrDefault(f => f.Region == r);
            PsgCandidate? noise = tries?.Candidates.FirstOrDefault(c => c.Setting is { Sound: PsgSound.WhiteNoise } s && s.NoiseRate == noiseRate);
            FmPatch? patch = null;
            FmRegionFeatures? features = null;
            if (fit is not null)
            {
                // One render sets the patch's level and measures its features (docs/adr/0096-fm-confidence.md).
                FmPatch full = AtFullLevel(fit.Patch);
                float[]? render = Render(full, NoteTarget.Of(region)?.NoteHz);
                patch = Measured(full, render, played.Loop is not null);
                features = render is null ? null : FmFeatureExtractor.Measure(region.Spectrum, SampleAnalyzer.Spectrum(render, null), fit.Score);
            }

            (FmPatch? second, double firstLevel, double spread) = patch is null ? (null, 1, 0) : SecondVoice(region, patch, played.Loop is not null, string.Create(CultureInfo.InvariantCulture, $"Fit {sample + 1}.{r + 1} second"));
            ChordVoices? chord = chords && fit is not null
                ? Chord(region, r, fit.Score.MelDb, played.Loop is not null, fmOptions, cache, string.Create(CultureInfo.InvariantCulture, $"Fit {sample + 1}.{r + 1} chord"))
                : null;
            regions.Add(new RegionSounds(region.LowRateHz, region.HighRateHz, patch, fit?.Score.MelDb, tries?.Tone?.Score.MelDb, noise?.Score.MelDb, second, firstLevel, spread, features, chord));
        }

        Dictionary<double, double> recordings = [];
        foreach (PcmPitch pitch in pcm?.Full?.Pitches ?? [])
        {
            recordings[pitch.RateHz] = pitch.Score.MelDb;
        }

        return new SampleSounds(sample, analysis.Percussive, regions, recordings, HasSteadyPitch(analysis));
    }
}
