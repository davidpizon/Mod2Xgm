using System.Diagnostics;
using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;
using Mod2Xgm.Render.Psg;

namespace Mod2Xgm.Optimizer;

/// <summary>One PSG try for one pitch region, with how close it sounds and whether the song has room for it.</summary>
/// <param name="Setting">What it plays.</param>
/// <param name="Score">How close it sounds to the region, by the FM fits' ruler (<see cref="NoteScorer"/>).</param>
/// <param name="CentsOff">How far its pitch is from the region's note, in cents (the PSG's periods are whole numbers); <c>null</c> for white noise.</param>
/// <param name="Room">Whether the song has room for it; every try is kept, so plan M9 can still choose one that doesn't fit.</param>
internal sealed record PsgCandidate(PsgSetting Setting, NoteScore Score, double? CentsOff, PsgRoom Room);

/// <summary>The PSG tries for one pitch region of one sample (docs/adr/0070-psg-candidates.md).</summary>
/// <param name="Region">Index into <see cref="SampleAnalysis.Regions"/>.</param>
/// <param name="Candidates">Every try: the tone (when the note is in a tone channel's range), then the noise.</param>
/// <param name="Renders">Notes rendered for this region.</param>
/// <param name="Elapsed">How long the fit took (for reports only; never changes a result).</param>
internal sealed record PsgRegionFit(int Region, IReadOnlyList<PsgCandidate> Candidates, int Renders, TimeSpan Elapsed)
{
    /// <summary>The closest try (log-mel distance).</summary>
    public PsgCandidate Best => Candidates.MinBy(c => c.Score.MelDb)!; // A region always has its noise tries.

    /// <summary>The tone try, or <c>null</c> when the note is too low for a tone channel.</summary>
    public PsgCandidate? Tone => Candidates.FirstOrDefault(c => c.Setting.Sound == PsgSound.Tone);

    /// <summary>The closest noise try.</summary>
    public PsgCandidate BestNoise => Candidates.Where(c => c.Setting.UsesNoise).MinBy(c => c.Score.MelDb)!; // Always there.

    /// <summary>The closest try the song has room for, or <c>null</c> when it has room for none.</summary>
    public PsgCandidate? BestThatFits => Candidates.Where(c => c.Room == PsgRoom.Fits).MinBy(c => c.Score.MelDb);
}

/// <summary>The PSG tries for every pitch region of one sample.</summary>
/// <param name="Sample">Index into <c>Song.Samples</c>.</param>
/// <param name="Name">The sample's name.</param>
/// <param name="MostAtOnce">The most channels the song sounds the sample on at once.</param>
/// <param name="Regions">One per region with sound, in <see cref="SampleAnalysis.Regions"/> order.</param>
internal sealed record PsgSampleFit(int Sample, string Name, int MostAtOnce, IReadOnlyList<PsgRegionFit> Regions);

/// <summary>
/// Makes the PSG candidates of plan T8.3 (docs/adr/0070-psg-candidates.md), one set per pitch region of every sample
/// with sound: a square wave at the nearest period, or for a note too low for a tone channel the noise's buzz tuned to
/// it; and the noise in both modes at every rate, the rate that follows tone channel 3 tuned (the hiss by a search,
/// the buzz to the note). Every try's volume follows the region's loudness, a step every 1/60 s; every try is scored
/// with the FM fits' ruler and marked with whether the song has room for it.
/// </summary>
internal static class PsgFitter
{
    /// <summary>Tone periods the tuned hiss's search starts from: half an octave apart, from 1 to 1023.</summary>
    private const double CoarseOctaves = 0.5;

    /// <summary>The tuned hiss's search halves its step from a quarter octave down to this.</summary>
    private const double FineOctaves = 1.0 / 16;

    /// <summary>Tone channels on the PSG.</summary>
    private const int ToneChannels = 3;

    /// <summary>Fits every region of <paramref name="analysis"/>, marking the tries the song's <paramref name="voices"/> leave no room for; none for a silent sample.</summary>
    public static PsgSampleFit Fit(SampleAnalysis analysis, SongVoices voices)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(voices);
        int atOnce = voices.MostAtOnceOf(analysis.Sample);
        List<PsgRegionFit> regions = [];
        if (analysis.Kind != SampleKind.Silent)
        {
            for (int r = 0; r < analysis.Regions.Count; r++)
            {
                if (NoteTarget.Of(analysis.Regions[r]) is { } target)
                {
                    regions.Add(FitRegion(r, target, atOnce, voices.MostAtOnce));
                }
            }
        }

        return new PsgSampleFit(analysis.Sample, analysis.Name, atOnce, regions);
    }

    /// <summary>The tries for one region's <paramref name="target"/>, for a sample sounded on up to <paramref name="sampleAtOnce"/> channels in a song sounding up to <paramref name="songAtOnce"/>.</summary>
    internal static PsgRegionFit FitRegion(int region, NoteTarget target, int sampleAtOnce, int songAtOnce)
    {
        ArgumentNullException.ThrowIfNull(target);
        Stopwatch clock = Stopwatch.StartNew();
        Judge judge = new(target, sampleAtOnce, songAtOnce);
        int[] volumes = Volumes(target.Spectrum);
        List<PsgCandidate> candidates = [];
        int period = PsgSetting.NearestPeriod(target.NoteHz);
        if (period <= PsgSetting.MaxPeriod)
        {
            candidates.Add(judge.Of(new PsgSetting(PsgSound.Tone, 0, period, volumes)));
        }

        foreach (PsgSound sound in new[] { PsgSound.WhiteNoise, PsgSound.PeriodicNoise })
        {
            for (int rate = 0; rate < PsgSetting.Tone3Rate; rate++)
            {
                candidates.Add(judge.Of(new PsgSetting(sound, rate, 0, volumes)));
            }
        }

        candidates.Add(TunedHiss(volumes, judge));
        int buzz = Math.Min(PsgSetting.MaxPeriod, PsgSetting.NearestPeriod(target.NoteHz * PsgSetting.PeriodicShifts));
        candidates.Add(judge.Of(new PsgSetting(PsgSound.PeriodicNoise, PsgSetting.Tone3Rate, buzz, volumes)));
        return new PsgRegionFit(region, candidates, judge.Renders, clock.Elapsed);
    }

    /// <summary>
    /// Whether the song has room for <paramref name="setting"/>: the PSG has 3 tone channels and 1 noise channel for the
    /// sample's notes at once, and a try that borrows tone channel 3 also needs a Genesis channel to spare
    /// (<see cref="GenesisPlan.ChannelCount"/> in all).
    /// </summary>
    internal static PsgRoom Room(PsgSetting setting, int sampleAtOnce, int songAtOnce)
    {
        ArgumentNullException.ThrowIfNull(setting);
        int channels = setting.UsesNoise ? 1 : ToneChannels;
        if (sampleAtOnce > channels)
        {
            return PsgRoom.SampleTooOften;
        }

        return setting.Channels > 1 && songAtOnce + setting.Channels - 1 > GenesisPlan.ChannelCount ? PsgRoom.SongTooFull : PsgRoom.Fits;
    }

    /// <summary>
    /// The volume register for each 1/60 s of the region: the mean power of the spectrum's frames centred in that step
    /// (the nearest frame when none is), relative to the loudest step, at the nearest of the PSG's levels. The overall
    /// level is the mapper's to set (docs/adr/0050-voice-levels.md), so the loudest step plays at volume 0.
    /// </summary>
    internal static int[] Volumes(RegionSpectrum spectrum)
    {
        ArgumentNullException.ThrowIfNull(spectrum);
        int steps = (int)Math.Ceiling(SampleAnalyzer.RegionSeconds * PsgSetting.StepsPerSecond);
        if (spectrum.MelBandsDb.Count == 0)
        {
            return [.. Enumerable.Repeat(PsgLevels.Off, steps)];
        }

        double[] framePower = [.. spectrum.MelBandsDb.Select(frame => frame.Sum(db => Math.Pow(10, db / 10.0)))];
        double centre = SampleAnalyzer.MelFftSize / 2.0 / SampleAnalyzer.AnalysisRateHz;
        double[] power = new double[steps];
        for (int s = 0; s < steps; s++)
        {
            double from = (double)s / PsgSetting.StepsPerSecond;
            double to = (double)(s + 1) / PsgSetting.StepsPerSecond;
            double sum = 0;
            int count = 0;
            for (int t = 0; t < framePower.Length; t++)
            {
                double at = (t * spectrum.HopSeconds) + centre;
                if (at >= from && at < to)
                {
                    sum += framePower[t];
                    count++;
                }
            }

            int nearest = Math.Clamp((int)Math.Round((((from + to) / 2) - centre) / spectrum.HopSeconds), 0, framePower.Length - 1);
            power[s] = count > 0 ? sum / count : framePower[nearest];
        }

        double loudest = power.Max();
        return [.. power.Select(p => loudest <= 0 ? PsgLevels.Off : PsgLevels.Nearest(Math.Sqrt(p / loudest)))];
    }

    /// <summary>
    /// The hiss that follows tone channel 3, tuned: every period half an octave apart, then from the closest a step either
    /// way, halving the step down to a sixteenth of an octave.
    /// </summary>
    private static PsgCandidate TunedHiss(int[] volumes, Judge judge)
    {
        Dictionary<int, PsgCandidate> tried = [];
        PsgCandidate Try(int period)
        {
            period = Math.Clamp(period, 1, PsgSetting.MaxPeriod);
            if (!tried.TryGetValue(period, out PsgCandidate? known))
            {
                known = judge.Of(new PsgSetting(PsgSound.WhiteNoise, PsgSetting.Tone3Rate, period, volumes));
                tried[period] = known;
            }

            return known;
        }

        PsgCandidate best = Try(1);
        for (double octaves = CoarseOctaves; ; octaves += CoarseOctaves)
        {
            int period = (int)Math.Round(Math.Pow(2, octaves));
            if (period > PsgSetting.MaxPeriod)
            {
                best = Closer(best, Try(PsgSetting.MaxPeriod));
                break;
            }

            best = Closer(best, Try(period));
        }

        for (double step = CoarseOctaves / 2; step >= FineOctaves; step /= 2)
        {
            int at = best.Setting.Period;
            best = Closer(best, Try((int)Math.Round(at * Math.Pow(2, -step))));
            best = Closer(best, Try((int)Math.Round(at * Math.Pow(2, step))));
        }

        return best;
    }

    /// <summary>The closer of two tries; the first on a tie, so the search is the same every run.</summary>
    private static PsgCandidate Closer(PsgCandidate a, PsgCandidate b) => b.Score.MelDb < a.Score.MelDb ? b : a;

    /// <summary>Renders, scores and marks the tries for one region, counting the renders.</summary>
    private sealed class Judge(NoteTarget target, int sampleAtOnce, int songAtOnce)
    {
        private readonly NoteScorer _scorer = new(target);

        /// <summary>Tries rendered so far.</summary>
        public int Renders { get; private set; }

        /// <summary><paramref name="setting"/> rendered, scored against the region and marked for room.</summary>
        public PsgCandidate Of(PsgSetting setting)
        {
            Renders++;
            NoteScore score = _scorer.Score(PsgNoteRenderer.Render(setting, SampleAnalyzer.RegionSeconds));
            double? cents = setting.PitchHz is { } hz ? 1200 * Math.Log2(hz / target.NoteHz) : null;
            return new PsgCandidate(setting, score, cents, Room(setting, sampleAtOnce, songAtOnce));
        }
    }
}
