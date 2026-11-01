using System.Diagnostics;
using System.Globalization;
using System.Text;
using Mod2Xgm.Analysis;
using Mod2Xgm.Audio;
using Mod2Xgm.Model;
using Mod2Xgm.Render;

namespace Mod2Xgm.Optimizer;

/// <summary>
/// One PCM copy of a sample: the sample played at one rate, stored at the target's rate (docs/adr/0071-pcm-candidates.md).
/// Copies with the same fields hold the same bytes, so a set of copies counts each once.
/// </summary>
/// <param name="SourceRateHz">The rate the sample is played at to make the copy: the pitch it sounds at.</param>
/// <param name="PlayRateHz">The rate the target stores and plays it at.</param>
/// <param name="HalfSpeed">XGM2 only: played at half speed.</param>
/// <param name="SourceFrames">How many of the sample's frames (its loop played out) the copy holds.</param>
/// <param name="Trimmed">True when a one-shot's quiet end was cut off (<see cref="PcmSavings.TrimTail"/>).</param>
/// <param name="Points">Its length at <see cref="PlayRateHz"/>.</param>
/// <param name="Bytes">What it costs: the target's bytes for <see cref="Points"/> (<see cref="PcmTargets.Bytes"/>).</param>
internal sealed record PcmCopy(double SourceRateHz, double PlayRateHz, bool HalfSpeed, int SourceFrames, bool Trimmed, int Points, int Bytes);

/// <summary>One pitch the song starts a sample at, and the copy that plays it.</summary>
/// <param name="RateHz">The playback rate the notes start at.</param>
/// <param name="Notes">Notes the song starts at it (0 for the one copy of a sample the song never plays).</param>
/// <param name="Copy">The copy that plays them.</param>
/// <param name="Score">How close the copy sounds to the sample at this pitch, by the FM fits' ruler (<see cref="NoteScorer"/>).</param>
/// <param name="CentsOff">How far the copy's pitch is from the notes' (a copy shared by <see cref="PcmSavings.Semitones"/>), in cents.</param>
internal sealed record PcmPitch(double RateHz, int Notes, PcmCopy Copy, NoteScore Score, double CentsOff);

/// <summary>Whether a set of copies fits the target's hard caps (docs/sample-capacity.md#limits-per-target).</summary>
internal enum PcmRoom
{
    /// <summary>The sample alone stays within the target's copies and voices.</summary>
    Fits,

    /// <summary>The song plays the sample on more channels at once than the target has PCM voices.</summary>
    SampleTooOften,

    /// <summary>The sample alone needs more copies than the target has slots.</summary>
    TooManyCopies,
}

/// <summary>One way to store a sample's copies: a combination of savings, with what it costs and how close it sounds.</summary>
/// <param name="Savings">The savings it uses.</param>
/// <param name="Pitches">Every pitch the song starts the sample at, low to high, with its copy and score.</param>
/// <param name="Room">Whether it fits the target's hard caps.</param>
internal sealed record PcmOption(PcmSavings Savings, IReadOnlyList<PcmPitch> Pitches, PcmRoom Room)
{
    /// <summary>The copies to store, each once.</summary>
    public IReadOnlyList<PcmCopy> Copies { get; } = [.. Pitches.Select(p => p.Copy).Distinct()];

    /// <summary>The bytes it costs: every copy's.</summary>
    public int Bytes => Copies.Sum(c => c.Bytes);

    /// <summary>The slots it takes: one per copy.</summary>
    public int Slots => Copies.Count;

    /// <summary>The log-mel distance of its pitches, each weighted by its notes (all alike when the song never plays the sample).</summary>
    public double MelDb => Weighted(p => p.Score.MelDb);

    /// <summary>The harmonic distance of its pitches, weighted by notes, or <c>null</c> when the sample has no pitch.</summary>
    public double? HarmonicDb => Pitches.All(p => p.Score.HarmonicDb is not null) ? Weighted(p => p.Score.HarmonicDb!.Value) : null; // Checked on the line.

    private double Weighted(Func<PcmPitch, double> value)
    {
        int notes = Pitches.Sum(p => p.Notes);
        return notes == 0 ? Pitches.Average(value) : Pitches.Sum(p => p.Notes * value(p)) / notes;
    }
}

/// <summary>
/// A note a copy can't play by itself (it starts past frame 0, or slides, wobbles or changes volume) baked into a sample
/// of its own by the MOD renderer, effects and all; only for notes the song plays the same way at least twice.
/// </summary>
/// <param name="Notes">Notes the song plays this way.</param>
/// <param name="StartFrame">The frame they start from.</param>
/// <param name="Seconds">How long the baked sample is.</param>
/// <param name="Bytes">What it costs.</param>
/// <param name="Score">How close it sounds to the notes, by the same ruler.</param>
/// <param name="PlainScore">How close the plain copy at their starting pitch, from frame 0 and without the effects, sounds to them.</param>
internal sealed record PcmBaked(int Notes, int StartFrame, double Seconds, int Bytes, NoteScore Score, NoteScore PlainScore);

/// <summary>The PCM candidates of one sample for one target.</summary>
/// <param name="Sample">Index into <c>Song.Samples</c>.</param>
/// <param name="Name">The sample's name.</param>
/// <param name="Target">The output the copies are for.</param>
/// <param name="MostAtOnce">The most channels the song sounds the sample on at once.</param>
/// <param name="Options">Every combination of the savings that apply, the plain copies first; none for a silent sample.</param>
/// <param name="Baked">The baked notes, most played first.</param>
/// <param name="BentNotes">Notes a copy can't play by itself, baked or not.</param>
/// <param name="Renders">Copies played through the driver model.</param>
/// <param name="Elapsed">How long the fit took (for reports only; never changes a result).</param>
internal sealed record PcmSampleFit(int Sample, string Name, PcmTarget Target, int MostAtOnce, IReadOnlyList<PcmOption> Options, IReadOnlyList<PcmBaked> Baked, int BentNotes, int Renders, TimeSpan Elapsed)
{
    /// <summary>The plain copies, without savings; <c>null</c> for a silent sample.</summary>
    public PcmOption? Full => Options.Count > 0 ? Options[0] : null;

    /// <summary>The option with the fewest bytes (the closer one of equals); <c>null</c> for a silent sample.</summary>
    public PcmOption? Smallest => Options.OrderBy(o => o.Bytes).ThenBy(o => o.MelDb).FirstOrDefault();
}

/// <summary>
/// Makes the PCM candidates of plan T8.4 (docs/adr/0071-pcm-candidates.md) for one target: a copy per pitch the song
/// starts each sample at, then every combination of the savings that apply to it, each with the bytes it costs and every
/// pitch scored through the target's PCM driver model with the FM fits' ruler; and baked samples for the notes with effects
/// a copy can't play, when the song plays them the same way more than once.
/// </summary>
internal static class PcmFitter
{
    /// <summary>A looped copy plays its loop out at least this long (as the DMF writer's copies, docs/adr/0044-dmf-effects-and-song-grid.md).</summary>
    private const double MinLoopSeconds = 0.5;

    /// <summary>A copy, or a baked note, plays at most this long (as the DMF writer's copies).</summary>
    private const double MaxLoopSeconds = 10;

    /// <summary>Scores look at least this much of a note: about two of the scorer's spectrum frames.</summary>
    private const double MinScoreSeconds = 0.1;

    /// <summary>A trimmed tail fades out over this long, so the cut doesn't click.</summary>
    private const double TrimFadeSeconds = 0.005;

    /// <summary>Frames past what a score needs that a copy is made with, so the resampler sees what follows.</summary>
    private const int ScoreMarginFrames = 64;

    /// <summary>ProTracker's C-2 period: the one copy of a sample the song never plays is at C-2 (docs/formats/mod.md#pitch).</summary>
    private const int C2Period = 428;

    /// <summary>A baked note is offered when the song plays it the same way at least this often.</summary>
    private const int MinBakedNotes = 2;

    /// <summary>The candidates of every analyzed sample of <paramref name="song"/> for <paramref name="target"/>.</summary>
    public static IReadOnlyList<PcmSampleFit> FitSong(SongAnalysis song, PcmTarget target)
    {
        ArgumentNullException.ThrowIfNull(song);
        return [.. song.Samples.Select(a => Fit(song, a, target))];
    }

    /// <summary>The candidates of the sample <paramref name="analysis"/> measured, for <paramref name="target"/>; none for a silent one.</summary>
    public static PcmSampleFit Fit(SongAnalysis song, SampleAnalysis analysis, PcmTarget target)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(analysis);
        Stopwatch clock = Stopwatch.StartNew();
        int atOnce = song.Voices.MostAtOnceOf(analysis.Sample);
        List<SongNote> notes = [.. song.Notes.Where(n => n.Sample == analysis.Sample)];
        int bent = notes.Count(n => n.Bent);
        if (analysis.Kind == SampleKind.Silent)
        {
            return new PcmSampleFit(analysis.Sample, analysis.Name, target, atOnce, [], [], bent, 0, clock.Elapsed);
        }

        Run run = new(SampleProcessor.CutAfterLoop(song.Song.Samples[analysis.Sample]), analysis.Pitch?.PeriodFrames, target);
        IReadOnlyList<Played> pitches = Pitches(notes, run.Sample);
        List<PcmOption> options = [];
        foreach (PcmSavings savings in Combinations(run.Applicable(pitches)))
        {
            options.Add(run.Option(savings, pitches, atOnce));
        }

        List<PcmBaked> baked = [];
        foreach (IGrouping<string, SongNote> same in notes.Where(n => n.Bent).GroupBy(n => BakeKey(song.Playback, n)).Where(g => g.Count() >= MinBakedNotes))
        {
            baked.Add(run.Bake(song, same.First(), same.Count()));
        }

        return new PcmSampleFit(analysis.Sample, analysis.Name, target, atOnce, options, [.. baked.OrderByDescending(b => b.Notes)], bent, run.Renders, clock.Elapsed);
    }

    /// <summary>A pitch the song starts the sample at: its rate, notes and longest hold.</summary>
    internal readonly record struct Played(double RateHz, int Notes, double HoldSeconds);

    /// <summary>The pitches the song starts the sample at, low to high; one at C-2 for a sample the song never plays.</summary>
    internal static IReadOnlyList<Played> Pitches(IReadOnlyList<SongNote> notes, Sample sample)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(sample);
        if (notes.Count == 0)
        {
            double c2 = AmigaPitch.PalClockHz / C2Period;
            double seconds = sample.Loop is null ? sample.Length / c2 : MinLoopSeconds;
            return [new Played(c2, 0, Math.Min(seconds, MaxLoopSeconds))];
        }

        return [.. notes
            .GroupBy(n => n.RateHz)
            .Select(g => new Played(g.Key, g.Count(), g.Max(n => n.Seconds)))
            .OrderBy(p => p.RateHz)];
    }

    /// <summary>Every combination of <paramref name="applicable"/>'s flags, the empty one first, then by how many flags, then by value.</summary>
    internal static IEnumerable<PcmSavings> Combinations(PcmSavings applicable)
    {
        int bits = (int)applicable;
        return Enumerable.Range(0, (int)PcmSavings.TrimTail * 2)
            .Where(m => (m & ~bits) == 0)
            .OrderBy(int.PopCount)
            .ThenBy(m => m)
            .Select(m => (PcmSavings)m);
    }

    /// <summary>
    /// The semitone a pitch belongs to for <see cref="PcmSavings.Semitones"/>: the nearest one to the pitch heard, from the
    /// sample's measured cycle (a sample isn't always recorded in tune with its notes), or to the rate without one.
    /// </summary>
    internal static int Semitone(double rateHz, double? periodFrames)
    {
        double hz = periodFrames is { } period ? rateHz / period : rateHz;
        return (int)Math.Round(12 * Math.Log2(hz / 440));
    }

    /// <summary>
    /// The frames of <paramref name="sample"/> (a one-shot) to keep for <see cref="PcmSavings.TrimTail"/>: up to its last
    /// point within <see cref="PcmTargets.TrimDb"/> of its peak.
    /// </summary>
    internal static int TrimmedLength(Sample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        float peak = sample.Data.Length == 0 ? 0 : sample.Data.Max(Math.Abs);
        float quiet = (float)(peak * Math.Pow(10, -PcmTargets.TrimDb / 20));
        int length = sample.Length;
        while (length > 0 && Math.Abs(sample.Data[length - 1]) < quiet)
        {
            length--;
        }

        return length;
    }

    /// <summary>How a bent note plays, tick by tick: notes with the same key sound the same, so they share one baked sample.</summary>
    private static string BakeKey(Playback playback, SongNote note)
    {
        StringBuilder key = new();
        key.Append(note.StartFrame.ToString(CultureInfo.InvariantCulture));
        foreach (PlaybackTick tick in BakedTicks(playback, note))
        {
            ChannelTick channel = tick.Channels[note.Channel];
            key.Append(CultureInfo.InvariantCulture, $"|{channel.Pitch.PlaybackRateHz:R},{channel.Volume:R},{tick.DurationSeconds:R}");
        }

        return key.ToString();
    }

    /// <summary>The ticks a baked sample is made from: the note's, up to <see cref="MaxLoopSeconds"/>.</summary>
    private static IEnumerable<PlaybackTick> BakedTicks(Playback playback, SongNote note)
    {
        double seconds = 0;
        for (int t = note.FirstTick; t < note.FirstTick + note.Ticks && seconds < MaxLoopSeconds; t++)
        {
            yield return playback.Ticks[t];
            seconds += playback.Ticks[t].DurationSeconds;
        }
    }

    /// <summary>One sample's fit for one target: makes, renders and scores its copies, remembering each result.</summary>
    private sealed class Run(Sample sample, double? periodFrames, PcmTarget target)
    {
        private readonly Dictionary<(double RateHz, double Seconds), NoteScorer> _targets = [];
        private readonly Dictionary<(PcmCopy Copy, double Seconds), float[]> _renders = [];
        private readonly int _trimmed = sample.Loop is null ? TrimmedLength(sample) : sample.Length;

        public Sample Sample => sample;

        /// <summary>Copies played through the driver model so far.</summary>
        public int Renders { get; private set; }

        /// <summary>The savings that change something for this sample and target.</summary>
        public PcmSavings Applicable(IReadOnlyList<Played> pitches)
        {
            PcmSavings applicable = PcmSavings.None;
            if (target == PcmTarget.Xgm2)
            {
                applicable |= PcmSavings.HalfSpeed;
            }

            if (pitches.GroupBy(p => Semitone(p.RateHz, periodFrames)).Any(g => g.Count() > 1))
            {
                applicable |= PcmSavings.Semitones;
            }

            double longest = pitches.Max(p => p.HoldSeconds);
            if (sample.Loop is not null && pitches.Any(p => LoopFrames(p.RateHz, p.HoldSeconds, true, longest) < LoopFrames(p.RateHz, p.HoldSeconds, false, longest)))
            {
                applicable |= PcmSavings.ShortLoops;
            }

            if (_trimmed < sample.Length)
            {
                applicable |= PcmSavings.TrimTail;
            }

            return applicable;
        }

        /// <summary>The copies <paramref name="savings"/> makes for every pitch, scored.</summary>
        public PcmOption Option(PcmSavings savings, IReadOnlyList<Played> pitches, int atOnce)
        {
            bool half = savings.HasFlag(PcmSavings.HalfSpeed);
            bool shortLoops = savings.HasFlag(PcmSavings.ShortLoops);
            bool trim = savings.HasFlag(PcmSavings.TrimTail);
            double longest = pitches.Max(p => p.HoldSeconds);
            Dictionary<double, (double RateHz, double HoldSeconds)> source = savings.HasFlag(PcmSavings.Semitones)
                ? SharedRates(pitches)
                : pitches.ToDictionary(p => p.RateHz, p => (p.RateHz, p.HoldSeconds));
            List<PcmPitch> scored = [];
            foreach (Played p in pitches)
            {
                (double rate, double hold) = source[p.RateHz];
                PcmCopy copy = Copy(rate, hold, longest, half, shortLoops, trim);
                double seconds = ScoreSeconds(p.HoldSeconds);
                NoteScore score = Scorer(p.RateHz, seconds, p.HoldSeconds).Score(Render(copy, seconds));
                scored.Add(new PcmPitch(p.RateHz, p.Notes, copy, score, 1200 * Math.Log2(rate / p.RateHz)));
            }

            PcmRoom room = atOnce > PcmTargets.Voices(target) ? PcmRoom.SampleTooOften : PcmRoom.Fits;
            PcmOption option = new(savings, scored, room);
            return option.Slots > PcmTargets.Slots(target) && room == PcmRoom.Fits ? option with { Room = PcmRoom.TooManyCopies } : option;
        }

        /// <summary>
        /// The bent note <paramref name="note"/>, played <paramref name="count"/> times the same way, baked by the MOD renderer
        /// into a sample at the target's rate, and scored with the plain copy that would otherwise play it.
        /// </summary>
        public PcmBaked Bake(SongAnalysis song, SongNote note, int count)
        {
            List<PlaybackTick> ticks = [];
            double start = song.Playback.Ticks[note.FirstTick].TimeSeconds;
            foreach (PlaybackTick tick in BakedTicks(song.Playback, note))
            {
                ChannelTick channel = tick.Channels[note.Channel] with { Pan = 0 };
                ticks.Add(tick with { TimeSeconds = tick.TimeSeconds - start, Channels = [channel] });
            }

            float[] rendered = ModRenderer.Render(song.Song, new Playback(ticks, null, PlaybackEnd.Stops), new ModRenderOptions(SampleAnalyzer.AnalysisRateHz, 0)).Left;
            double seconds = (double)rendered.Length / SampleAnalyzer.AnalysisRateHz;
            double scoreSeconds = ScoreSeconds(seconds);
            double? fundamental = periodFrames is { } period ? note.RateHz / period : null;
            NoteScorer scorer = new(new NoteTarget(fundamental ?? note.RateHz, fundamental, SampleAnalyzer.Spectrum(Head(rendered, scoreSeconds), fundamental)));

            double playRate = PcmTargets.PlayRateHz(target, note.RateHz, false);
            float[] bakedCopy = SampleProcessor.Resample(rendered, SampleAnalyzer.AnalysisRateHz, playRate, ResamplingMode.Smooth);
            Renders++;
            NoteScore score = scorer.Score(Head(PcmNoteRenderer.Render(target, bakedCopy, playRate, false, scoreSeconds), scoreSeconds));
            PcmCopy plain = Copy(note.RateHz, note.Seconds, note.Seconds, false, false, false);
            NoteScore plainScore = scorer.Score(Render(plain, scoreSeconds));
            return new PcmBaked(count, note.StartFrame, seconds, PcmTargets.Bytes(target, bakedCopy.Length), score, plainScore);
        }

        /// <summary>For <see cref="PcmSavings.Semitones"/>: each pitch's copy is its semitone's most played pitch (the lowest of equals), held as long as any.</summary>
        private Dictionary<double, (double RateHz, double HoldSeconds)> SharedRates(IReadOnlyList<Played> pitches)
        {
            Dictionary<double, (double, double)> shared = [];
            foreach (IGrouping<int, Played> group in pitches.GroupBy(p => Semitone(p.RateHz, periodFrames)))
            {
                Played most = group.OrderByDescending(p => p.Notes).ThenBy(p => p.RateHz).First();
                double hold = group.Max(p => p.HoldSeconds);
                foreach (Played p in group)
                {
                    shared[p.RateHz] = (most.RateHz, hold);
                }
            }

            return shared;
        }

        /// <summary>The copy of the sample played at <paramref name="rateHz"/>, for notes held up to <paramref name="holdSeconds"/>.</summary>
        private PcmCopy Copy(double rateHz, double holdSeconds, double longestSeconds, bool half, bool shortLoops, bool trim)
        {
            int frames = sample.Loop is null
                ? (trim ? _trimmed : sample.Length)
                : LoopFrames(rateHz, holdSeconds, shortLoops, longestSeconds);
            double playRate = PcmTargets.PlayRateHz(target, rateHz, half);
            int points = (int)Math.Ceiling(frames * playRate / rateHz);
            return new PcmCopy(rateHz, playRate, half, frames, trim && sample.Loop is null, points, PcmTargets.Bytes(target, points));
        }

        /// <summary>
        /// The frames a looped copy holds: played out as long as the song holds any note of the sample (at least
        /// <see cref="MinLoopSeconds"/>, at most <see cref="MaxLoopSeconds"/>, never shorter than the sample), or with
        /// <see cref="PcmSavings.ShortLoops"/> only as long as this pitch is held.
        /// </summary>
        private int LoopFrames(double rateHz, double holdSeconds, bool shortLoops, double longestSeconds)
        {
            int full = Math.Max(sample.Length, (int)Math.Ceiling(Math.Clamp(longestSeconds, MinLoopSeconds, MaxLoopSeconds) * rateHz) + 1);
            return shortLoops ? Math.Min(full, (int)Math.Ceiling(Math.Min(holdSeconds, MaxLoopSeconds) * rateHz) + 1) : full;
        }

        /// <summary>
        /// The scorer for the sample played at <paramref name="rateHz"/> for <paramref name="seconds"/>, untouched, and
        /// silent after <paramref name="holdSeconds"/> as the song's notes are: what every copy is measured against.
        /// </summary>
        private NoteScorer Scorer(double rateHz, double seconds, double holdSeconds)
        {
            double heard = Math.Min(seconds, holdSeconds);
            if (!_targets.TryGetValue((rateHz, heard), out NoteScorer? scorer))
            {
                float[] played = SampleProcessor.Unroll(sample, (int)Math.Ceiling(seconds * rateHz) + ScoreMarginFrames);
                float[] sound = played.Length == 0 ? [] : SampleProcessor.Resample(played, rateHz, SampleAnalyzer.AnalysisRateHz, ResamplingMode.Smooth);
                sound = Head(sound, seconds);
                int end = Math.Min(sound.Length, (int)Math.Round(heard * SampleAnalyzer.AnalysisRateHz));
                Array.Clear(sound, end, sound.Length - end);
                double? fundamental = periodFrames is { } period ? rateHz / period : null;
                scorer = new NoteScorer(new NoteTarget(fundamental ?? rateHz, fundamental, SampleAnalyzer.Spectrum(sound, fundamental)));
                _targets[(rateHz, heard)] = scorer;
            }

            return scorer;
        }

        /// <summary>The first <paramref name="seconds"/> of <paramref name="copy"/> through the driver model, at the analysis rate.</summary>
        private float[] Render(PcmCopy copy, double seconds)
        {
            if (!_renders.TryGetValue((copy, seconds), out float[]? sound))
            {
                int frames = Math.Min(copy.SourceFrames, (int)Math.Ceiling(seconds * copy.SourceRateHz) + ScoreMarginFrames);
                float[] data = SampleProcessor.Unroll(sample, frames);
                if (copy.Trimmed)
                {
                    FadeOut(data, (int)Math.Ceiling(TrimFadeSeconds * copy.SourceRateHz), copy.SourceFrames);
                }

                float[] points = data.Length == 0 ? [] : SampleProcessor.Resample(data, copy.SourceRateHz, copy.PlayRateHz, ResamplingMode.Smooth);
                Renders++;
                sound = Head(PcmNoteRenderer.Render(target, points, copy.PlayRateHz, copy.HalfSpeed, seconds), seconds);
                _renders[(copy, seconds)] = sound;
            }

            return sound;
        }

        /// <summary>Fades the last <paramref name="fade"/> of a trimmed copy's <paramref name="end"/> frames to silence; <paramref name="data"/> may stop earlier.</summary>
        private static void FadeOut(float[] data, int fade, int end)
        {
            for (int i = Math.Max(0, end - fade); i < Math.Min(end, data.Length); i++)
            {
                data[i] *= (float)(end - i) / fade;
            }
        }

        /// <summary>How much of a note held <paramref name="holdSeconds"/> the scores look at: all of it, within <see cref="MinScoreSeconds"/> and the analysis's second.</summary>
        private static double ScoreSeconds(double holdSeconds) => Math.Clamp(holdSeconds, MinScoreSeconds, SampleAnalyzer.RegionSeconds);

        /// <summary>The first <paramref name="seconds"/> of <paramref name="sound"/> (at the analysis rate), or all of it when shorter.</summary>
        private static float[] Head(float[] sound, double seconds) => sound[..Math.Min(sound.Length, (int)Math.Round(seconds * SampleAnalyzer.AnalysisRateHz))];
    }
}
