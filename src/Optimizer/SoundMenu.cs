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
internal sealed record RegionSounds(double LowRateHz, double HighRateHz, FmPatch? Patch, double? FmDb, double? ToneDb, double? NoiseDb);

/// <summary>What the allocator can play one sample with.</summary>
/// <param name="Sample">Index into <c>Song.Samples</c>.</param>
/// <param name="Percussive">How much it hits (<see cref="SampleAnalysis.Percussive"/>), 0–1.</param>
/// <param name="Regions">Its pitch regions, low to high.</param>
/// <param name="RecordingDb">For each rate the song starts it at, how far the stored copy is; empty when PCM wasn't fitted.</param>
/// <param name="SteadyPitch">False when it has no pitch a tuned voice could hold (<see cref="SoundMenu.HasSteadyPitch"/>): such a voice plays it as a wrong note (David, by ear).</param>
internal sealed record SampleSounds(int Sample, double Percussive, IReadOnlyList<RegionSounds> Regions, IReadOnlyDictionary<double, double> RecordingDb, bool SteadyPitch = true)
{
    /// <summary>The region holding <paramref name="rateHz"/>, or the nearest by pitch; <c>null</c> when it has none.</summary>
    public RegionSounds? RegionAt(double rateHz) => Regions
        .OrderBy(r => rateHz < r.LowRateHz ? Math.Log2(r.LowRateHz / rateHz) : rateHz > r.HighRateHz ? Math.Log2(rateHz / r.HighRateHz) : 0)
        .FirstOrDefault();
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

    /// <summary>The TL that mutes an operator (docs/genesis-audio.md): such a carrier stays muted.</summary>
    private const int MutedTl = 127;

    /// <summary>Makes the menu of every analyzed sample: only the sounds its <c>--require-*</c> list allows are fitted.</summary>
    /// <param name="song">The analyzed song.</param>
    /// <param name="target">The output the PCM copies are for.</param>
    /// <param name="options">The user's pins and bans.</param>
    /// <param name="fm">How FM is fitted.</param>
    /// <param name="cache">Where earlier candidates are kept, or <c>null</c> to make them all.</param>
    /// <param name="work">Counts samples done.</param>
    public static IReadOnlyList<SampleSounds> Make(SongAnalysis song, PcmTarget target, MappingOptions options, FmFitOptions fm, CandidateCache? cache, IWorkCounter work)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fm);
        ArgumentNullException.ThrowIfNull(work);
        SampleSounds[] made = new SampleSounds[song.Samples.Count];
        work.SetTotal(made.Length);
        Parallel.For(0, made.Length, i =>
        {
            made[i] = Sample(song, song.Samples[i], target, options, fm, cache);
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
        if (hz is not { } noteHz)
        {
            return patch;
        }

        double rms = VoiceLevel.Heard(FmNoteRenderer.Render(patch, null, noteHz, SampleAnalyzer.RegionSeconds), SampleAnalyzer.AnalysisRateHz, loops);
        return rms > 0 ? patch with { HeardRms = rms } : patch;
    }

    private static SampleSounds Sample(SongAnalysis song, SampleAnalysis analysis, PcmTarget target, MappingOptions options, FmFitOptions fmOptions, CandidateCache? cache)
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
            regions.Add(new RegionSounds(region.LowRateHz, region.HighRateHz, fit is null ? null : Measured(AtFullLevel(fit.Patch), NoteTarget.Of(region)?.NoteHz, played.Loop is not null), fit?.Score.MelDb, tries?.Tone?.Score.MelDb, noise?.Score.MelDb));
        }

        Dictionary<double, double> recordings = [];
        foreach (PcmPitch pitch in pcm?.Full?.Pitches ?? [])
        {
            recordings[pitch.RateHz] = pitch.Score.MelDb;
        }

        return new SampleSounds(sample, analysis.Percussive, regions, recordings, HasSteadyPitch(analysis));
    }
}
