using System.Diagnostics;
using System.Runtime.InteropServices;
using Mod2Xgm.Analysis;
using Mod2Xgm.Audio;
using Mod2Xgm.Dmf;
using Mod2Xgm.Model;
using Mod2Xgm.Render;
using Serilog.Core;

namespace Mod2Xgm.Optimizer;

/// <summary>How a pre-mixed sample's outlasting note goes on after the overlap (docs/adr/0072-pre-mix-candidates.md).</summary>
internal enum PreMixEnd
{
    /// <summary>No note outlasts the switch: the pre-mixed sample holds every note to its end.</summary>
    None,

    /// <summary>The outlasting note goes on as a tail, the rest of its own sample, started on the same voice at the switch.</summary>
    Tail,

    /// <summary>The outlasting note, a one-shot, is baked into the pre-mixed sample to its end: no tail and no seam, more bytes.</summary>
    Baked,
}

/// <summary>One note of a pre-mix, as its first occurrence plays it.</summary>
/// <param name="Sample">Index into <c>Song.Samples</c>.</param>
/// <param name="OffsetTicks">Ticks from the window's start to the note's.</param>
/// <param name="RateHz">The playback rate it starts at.</param>
/// <param name="StartFrame">The first frame it plays (MOD <c>9xx</c>).</param>
/// <param name="Volume">The volume it starts at, 0–1.</param>
internal sealed record PreMixPart(int Sample, int OffsetTicks, double RateHz, int StartFrame, double Volume);

/// <summary>
/// The rest of the outlasting note's sample, played on the same voice after the pre-mixed sample: at the note's volume
/// (and the mix's clip scale), so it carries on at the level the mix left it at.
/// </summary>
/// <param name="Id">Tails with the same bytes have the same id and share one slot.</param>
/// <param name="Sample">Index into <c>Song.Samples</c>.</param>
/// <param name="StartFrame">The frame it starts from, its loop played out.</param>
/// <param name="RateHz">The playback rate the note plays at when the tail starts.</param>
/// <param name="Points">Its length at the target's rate.</param>
/// <param name="Bytes">Its own bytes: SGDK's tools store every slot's bytes, so a tail never shares another sample's (docs/adr/0072-pre-mix-candidates.md).</param>
internal sealed record PreMixTail(int Id, int Sample, int StartFrame, double RateHz, int Points, int Bytes);

/// <summary>
/// One pre-mixed PCM candidate (plan T8.5a, docs/adr/0052-pre-mixed-pcm.md): notes that overlap the same way, mixed into one
/// sample so they play on one PCM voice.
/// </summary>
/// <param name="Parts">The notes, in the order they start.</param>
/// <param name="Occurrences">How often the song plays these notes this way (windows with the same bytes are one candidate).</param>
/// <param name="SavesSteal">True when, without pre-mixes, a voice would be stolen from one of these notes somewhere.</param>
/// <param name="WindowSeconds">From the first note's start to the end of the overlap.</param>
/// <param name="SwitchSeconds">Where the tail starts (<see cref="PreMixEnd.Tail"/>) or would start: the window's end on DMF's tick, the next frame on XGM and XGM2.</param>
/// <param name="End">How the outlasting note goes on.</param>
/// <param name="HeadBytes">The pre-mixed sample's bytes.</param>
/// <param name="Tail">The tail, for <see cref="PreMixEnd.Tail"/>.</param>
/// <param name="ClipDb">How far the mix was turned down so it doesn't clip; 0 when it didn't need to be.</param>
/// <param name="Score">How close the pre-mix sounds through the PCM driver model to the notes as the MOD renderer plays them, by the FM fits' ruler.</param>
/// <param name="Seam">The same around the switch, ±0.1 s, for <see cref="PreMixEnd.Tail"/> and <see cref="PreMixEnd.Baked"/>: measured and reported, never used to drop a candidate (David).</param>
internal sealed record PreMixCandidate(
    IReadOnlyList<PreMixPart> Parts,
    int Occurrences,
    bool SavesSteal,
    double WindowSeconds,
    double SwitchSeconds,
    PreMixEnd End,
    int HeadBytes,
    PreMixTail? Tail,
    double ClipDb,
    NoteScore Score,
    NoteScore? Seam)
{
    /// <summary>The bytes it costs: the pre-mixed sample's and its tail's.</summary>
    public int Bytes => HeadBytes + (Tail?.Bytes ?? 0);

    /// <summary>The slots it takes: one, and one more for a tail.</summary>
    public int Slots => Tail is null ? 1 : 2;

    /// <summary>PCM voices it frees each time it plays.</summary>
    public int VoicesFreed => Parts.Count - 1;
}

/// <summary>The pre-mix candidates of one song for one target, and how many voice steals they could remove.</summary>
/// <param name="Target">The target.</param>
/// <param name="Voices">Its PCM voices.</param>
/// <param name="PcmNotes">Notes of samples that may go into a pre-mix.</param>
/// <param name="Overlaps">Windows found: every group of 2–4 of those notes that overlap (one occurrence each).</param>
/// <param name="Steals">Notes that start while every PCM voice is busy, without pre-mixes.</param>
/// <param name="StealsWithPreMix">The same with every candidate used where it fits (each note in one pre-mix at most, larger ones first).</param>
/// <param name="Candidates">Every candidate offered.</param>
/// <param name="Renders">Windows rendered.</param>
/// <param name="Elapsed">How long it took (for reports only; never changes a result).</param>
internal sealed record PreMixSongFit(PcmTarget Target, int Voices, int PcmNotes, int Overlaps, int Steals, int StealsWithPreMix, IReadOnlyList<PreMixCandidate> Candidates, int Renders, TimeSpan Elapsed)
{
    /// <summary>Voice steals the candidates remove.</summary>
    public int StealsRemoved => Steals - StealsWithPreMix;
}

/// <summary>
/// Makes the pre-mixed PCM candidates of plan T8.5a (docs/adr/0052-pre-mixed-pcm.md, docs/adr/0072-pre-mix-candidates.md):
/// finds every group of 2–4 notes that overlap from the note events, mixes each way the song plays them with the MOD
/// renderer at the target's rate, quantized once, and scores it through the target's PCM driver model. A note that
/// outlasts the overlap goes on as a tail, and a one-shot one is also offered baked to its end. Windows with the same
/// bytes are one candidate, offered when the song plays it twice or more, or once where it saves a voice steal.
/// </summary>
internal static class PreMixer
{
    /// <summary>A pre-mix holds at most this many notes: XGM's PCM voices, the most any target has (David).</summary>
    private const int MaxNotes = 4;

    /// <summary>A window is offered when the song plays it at least this often, or once where it saves a voice steal.</summary>
    private const int MinOccurrences = 2;

    /// <summary>XGM and XGM2 act on the music's commands once a frame, 60 times a second on NTSC (docs/formats/xgm.md).</summary>
    private const double FrameHz = 60;

    /// <summary>The seam is scored over this long either side of the switch.</summary>
    private const double SeamSeconds = 0.1;

    /// <summary>Scores look at least this much of a window: about two of the scorer's spectrum frames.</summary>
    private const double MinScoreSeconds = 0.1;

    /// <summary>A looped tail plays at most this long (as a PCM copy, docs/adr/0071-pcm-candidates.md).</summary>
    private const double MaxTailSeconds = 10;

    /// <summary>XGM and XGM2 store samples in 256-byte blocks (docs/formats/xgm.md); a pre-mixed sample's padding holds what follows.</summary>
    private const int XgmAlignment = 256;

    /// <summary>Clipping notes go nowhere: a candidate is only measured here.</summary>
    private static readonly ConversionNotes Silent = new(Logger.None);

    /// <summary>The candidates of <paramref name="song"/> for every target, in <see cref="PcmTargets.All"/> order.</summary>
    /// <param name="song">The song as analyzed.</param>
    /// <param name="keepOut">Samples that never go into a pre-mix (<c>--require-fm</c>, <c>--require-psg</c>); ignored samples aren't analyzed.</param>
    public static IReadOnlyList<PreMixSongFit> FitSong(SongAnalysis song, IReadOnlySet<int>? keepOut = null) =>
        [.. PcmTargets.All.Select(t => Fit(song, t, keepOut))];

    /// <summary>The candidates of <paramref name="song"/> for <paramref name="target"/>.</summary>
    /// <param name="song">The song as analyzed.</param>
    /// <param name="target">The target.</param>
    /// <param name="keepOut">Samples that never go into a pre-mix.</param>
    public static PreMixSongFit Fit(SongAnalysis song, PcmTarget target, IReadOnlySet<int>? keepOut = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        Stopwatch clock = Stopwatch.StartNew();
        HashSet<int> eligible = [.. song.Samples.Where(a => a.Kind != SampleKind.Silent && keepOut?.Contains(a.Sample) != true).Select(a => a.Sample)];
        SongNote[] notes = [.. song.Notes.Where(n => eligible.Contains(n.Sample))];
        int voices = PcmTargets.Voices(target);
        bool[] stolen = new bool[notes.Length];
        int steals = Steals([.. notes.Select((n, i) => new Unit(n.FirstTick, End(n), [i]))], voices, stolen);
        List<Window> windows = Windows(notes);

        Run run = new(song, notes, target);
        Dictionary<(PreMixEnd, Fingerprint), List<Window>> groups = [];
        foreach (Window w in windows)
        {
            foreach ((PreMixEnd end, Fingerprint key) in run.Keys(w))
            {
                if (!groups.TryGetValue((end, key), out List<Window>? same))
                {
                    same = [];
                    groups[(end, key)] = same;
                }

                same.Add(w);
            }
        }

        // Windows with the same notes, effects and timing have the same bytes; those are rendered once, then merged by bytes.
        Dictionary<Fingerprint, (PreMixCandidate Candidate, List<Window> Windows)> merged = [];
        List<Fingerprint> order = [];
        foreach (((PreMixEnd end, _), List<Window> same) in groups)
        {
            bool saves = same.Any(w => w.Notes.Any(n => stolen[n]));
            if (same.Count < MinOccurrences && !saves)
            {
                continue;
            }

            (PreMixCandidate candidate, Fingerprint bytes) = run.Make(same, end, saves);
            if (merged.TryGetValue(bytes, out (PreMixCandidate Candidate, List<Window> Windows) known))
            {
                known.Windows.AddRange(same);
                merged[bytes] = (known.Candidate with { Occurrences = known.Candidate.Occurrences + same.Count, SavesSteal = known.Candidate.SavesSteal || saves }, known.Windows);
            }
            else
            {
                merged[bytes] = (candidate, [.. same]);
                order.Add(bytes);
            }
        }

        List<PreMixCandidate> candidates = [.. order.Select(k => merged[k].Candidate)];
        IEnumerable<Window> usable = order.Where(k => merged[k].Candidate.End != PreMixEnd.Baked).SelectMany(k => merged[k].Windows);
        int after = Steals(Units(notes, usable), voices, null);
        return new PreMixSongFit(target, voices, notes.Length, windows.Count, steals, after, candidates, run.Renders, clock.Elapsed);
    }

    /// <summary>The tick after a note's last.</summary>
    private static int End(SongNote note) => note.FirstTick + note.Ticks;

    /// <summary>
    /// Every group of 2–<see cref="MaxNotes"/> notes that overlap: linked by overlaps, with one stretch where two or more
    /// of them sound (a gap there makes two windows, not one).
    /// </summary>
    private static List<Window> Windows(SongNote[] notes)
    {
        List<int>[] near = [.. notes.Select(_ => new List<int>())];
        List<int> active = [];
        for (int i = 0; i < notes.Length; i++)
        {
            active.RemoveAll(j => End(notes[j]) <= notes[i].FirstTick);
            foreach (int j in active)
            {
                near[i].Add(j);
                near[j].Add(i);
            }

            active.Add(i);
        }

        List<Window> found = [];
        HashSet<(int, int, int, int)> seen = [];
        void Grow(int[] group)
        {
            foreach (int next in group.SelectMany(m => near[m]).Distinct().Where(n => Array.IndexOf(group, n) < 0).Order())
            {
                int[] bigger = [.. group.Append(next).Order()];
                if (!seen.Add(GroupKey(bigger)))
                {
                    continue;
                }

                if (Window.Of(notes, bigger) is { } w)
                {
                    found.Add(w);
                }

                if (bigger.Length < MaxNotes)
                {
                    Grow(bigger);
                }
            }
        }

        for (int i = 0; i < notes.Length; i++)
        {
            Grow([i]);
        }

        return [.. found.OrderBy(w => w.StartTick).ThenBy(w => GroupKey(w.Notes))];
    }

    /// <summary>A group of up to <see cref="MaxNotes"/> note indices (ascending) as one value, −1 for the places it doesn't fill.</summary>
    private static (int, int, int, int) GroupKey(int[] group) =>
        (group[0], group.Length > 1 ? group[1] : -1, group.Length > 2 ? group[2] : -1, group.Length > 3 ? group[3] : -1);

    /// <summary>The units the song's voices play with the pre-mixes of <paramref name="windows"/>: each note in at most one, larger ones first.</summary>
    private static List<Unit> Units(SongNote[] notes, IEnumerable<Window> windows)
    {
        bool[] used = new bool[notes.Length];
        List<Unit> units = [];
        foreach (Window w in windows.Distinct().OrderByDescending(w => w.Notes.Length).ThenBy(w => w.StartTick).ThenBy(w => GroupKey(w.Notes)))
        {
            if (w.Notes.Any(n => used[n]))
            {
                continue;
            }

            foreach (int n in w.Notes)
            {
                used[n] = true;
            }

            units.Add(new Unit(w.StartTick, w.Notes.Max(n => End(notes[n])), w.Notes));
        }

        units.AddRange(Enumerable.Range(0, notes.Length).Where(n => !used[n]).Select(n => new Unit(notes[n].FirstTick, End(notes[n]), [n])));
        return units;
    }

    /// <summary>
    /// Plays <paramref name="units"/> on <paramref name="voices"/> voices in the order they start: a unit that finds every
    /// voice busy is stolen (it would move to FM/PSG or be cut). Returns its notes counted; marks them in <paramref name="stolen"/>.
    /// </summary>
    private static int Steals(List<Unit> units, int voices, bool[]? stolen)
    {
        PriorityQueue<int, int> busy = new();
        int count = 0;
        foreach (Unit u in units.OrderBy(u => u.Start).ThenBy(u => u.Notes[0]))
        {
            while (busy.TryPeek(out _, out int end) && end <= u.Start)
            {
                busy.Dequeue();
            }

            if (busy.Count < voices)
            {
                busy.Enqueue(0, u.End);
                continue;
            }

            count += u.Notes.Length;
            foreach (int n in u.Notes)
            {
                stolen?[n] = true;
            }
        }

        return count;
    }

    /// <summary>A voice's worth of sound: one note, or a pre-mix's notes, from the tick it starts to the tick after its last.</summary>
    private sealed record Unit(int Start, int End, int[] Notes);

    /// <summary>One occurrence of overlapping notes.</summary>
    /// <param name="Notes">Indices of the notes, in the order they start.</param>
    /// <param name="StartTick">The first note's first tick.</param>
    /// <param name="EndTick">The tick after the last on which two or more of them sound.</param>
    /// <param name="Outlast">The note still sounding at <paramref name="EndTick"/>, if any.</param>
    private sealed record Window(int[] Notes, int StartTick, int EndTick, int? Outlast)
    {
        /// <summary>The window of <paramref name="group"/> (ascending indices), or <c>null</c> when two or more of them sound in two separate stretches.</summary>
        public static Window? Of(SongNote[] notes, int[] group)
        {
            List<(int Tick, int Change)> changes = [];
            foreach (int n in group)
            {
                changes.Add((notes[n].FirstTick, 1));
                changes.Add((End(notes[n]), -1));
            }

            int sounding = 0;
            int runs = 0;
            int endTick = 0;
            foreach ((int tick, int change) in changes.OrderBy(c => c.Tick).ThenBy(c => c.Change))
            {
                int before = sounding;
                sounding += change;
                if (before < 2 && sounding >= 2)
                {
                    runs++;
                }
                else if (before >= 2 && sounding < 2)
                {
                    endTick = tick;
                }
            }

            if (runs != 1)
            {
                return null;
            }

            int[] outlast = [.. group.Where(n => End(notes[n]) > endTick)];
            return new Window(group, notes[group[0]].FirstTick, endTick, outlast.Length == 1 ? outlast[0] : null);
        }
    }

    /// <summary>One song's pre-mixes for one target: keys, renders and scores.</summary>
    private sealed class Run(SongAnalysis song, SongNote[] notes, PcmTarget target)
    {
        private readonly Dictionary<Fingerprint, int> _tailIds = [];
        private readonly Playback _playback = song.Playback;

        /// <summary>Windows rendered so far.</summary>
        public int Renders { get; private set; }

        /// <summary>
        /// The candidates <paramref name="w"/> takes part in, each with a key: windows with the same key play the same
        /// notes the same way, so they make the same bytes.
        /// </summary>
        /// <remarks>
        /// A key is a SHA-256 fingerprint of what the notes play, not the text of it: a long window's text runs to hundreds
        /// of kilobytes, and every window's keys are kept until the song is done.
        /// </remarks>
        public IEnumerable<(PreMixEnd End, Fingerprint Key)> Keys(Window w)
        {
            double windowSeconds = Seconds(w.StartTick, w.EndTick);
            double switchSeconds = Switch(windowSeconds);
            using Hasher key = new();
            AppendDurations(key, w.StartTick, w.EndTick);
            foreach (int n in w.Notes)
            {
                AppendNote(key, w, n, w.EndTick);
            }

            if (w.Outlast is not { } o || Time(End(notes[o])) - Time(w.StartTick) <= switchSeconds || RanOut(o, Time(w.StartTick) + switchSeconds))
            {
                if (w.Outlast is { } shortOne)
                {
                    AppendFrom(key, shortOne, w.EndTick, End(notes[shortOne]));
                }

                yield return (PreMixEnd.None, key.Finish());
                yield break;
            }

            Fingerprint head = key.Finish();
            using Hasher tail = new(head);
            AppendFrom(tail, o, w.EndTick, TickAt(Time(w.StartTick) + switchSeconds + PadSeconds(w)) + 1);
            yield return (PreMixEnd.Tail, tail.Finish());
            if (SampleOf(o).Loop is null)
            {
                using Hasher baked = new(head);
                AppendFrom(baked, o, w.EndTick, End(notes[o]));
                yield return (PreMixEnd.Baked, baked.Finish());
            }
        }

        /// <summary>
        /// The candidate the windows <paramref name="same"/> (one key) make, and its bytes as text for merging: mixed by
        /// the MOD renderer, turned down if it clips, at the target's rate, then scored through the driver model.
        /// </summary>
        public (PreMixCandidate Candidate, Fingerprint Bytes) Make(List<Window> same, PreMixEnd end, bool saves)
        {
            Window w = same[0];
            double windowSeconds = Seconds(w.StartTick, w.EndTick);
            double switchSeconds = end == PreMixEnd.None ? windowSeconds : Switch(windowSeconds);
            double playRate = target == PcmTarget.Dmf
                ? DmfSongBuilder.DacRateHz(w.Notes.Max(n => notes[n].RateHz))
                : PcmTargets.PlayRateHz(target, 0, false);
            int lastTick = end == PreMixEnd.Tail || w.Outlast is null ? w.EndTick : End(notes[w.Outlast.Value]);
            double storedSeconds = end == PreMixEnd.Tail ? switchSeconds + PadSeconds(w) : Seconds(w.StartTick, lastTick);
            double scoreSeconds = Math.Clamp(storedSeconds, MinScoreSeconds, SampleAnalyzer.RegionSeconds);
            double renderSeconds = end == PreMixEnd.None ? scoreSeconds : Math.Max(scoreSeconds, switchSeconds + SeamSeconds);
            int renderTick = Math.Max(lastTick, TickAt(Time(w.StartTick) + Math.Max(storedSeconds, renderSeconds)) + 1);
            float[] ideal = Mix(w, renderTick, Math.Max(storedSeconds, renderSeconds));

            int storedEnd = Math.Min(ideal.Length, (int)Math.Ceiling(storedSeconds * SampleAnalyzer.AnalysisRateHz));
            float peak = 0;
            for (int i = 0; i < storedEnd; i++)
            {
                peak = Math.Max(peak, Math.Abs(ideal[i]));
            }

            float scale = peak > 1 ? 1 / peak : 1;
            double clipDb = peak > 1 ? 20 * Math.Log10(peak) : 0;
            for (int i = 0; i < ideal.Length; i++)
            {
                ideal[i] *= scale;
            }

            float[] stored = SampleProcessor.Resample(ideal[..storedEnd], SampleAnalyzer.AnalysisRateHz, playRate, ResamplingMode.Smooth);
            if (end == PreMixEnd.Tail)
            {
                int points = (int)Math.Ceiling(switchSeconds * playRate);
                stored = stored[..Math.Min(stored.Length, target == PcmTarget.Dmf ? points : PcmTargets.Bytes(target, points))];
            }

            PreMixTail? tail = null;
            float[] tailPoints = [];
            double tailRate = playRate;
            if (end == PreMixEnd.Tail)
            {
                (tail, tailPoints, tailRate) = Tail(same, w.Outlast!.Value, switchSeconds, scale); // A tail always has an outlasting note.
            }

            Renders++;
            NoteScorer whole = Scorer(Head(ideal, 0, scoreSeconds), notes[w.Notes[0]].RateHz);
            float[] played = end == PreMixEnd.Tail
                ? PcmNoteRenderer.Render(target, stored, playRate, renderSeconds, tailPoints, tailRate, switchSeconds)
                : PcmNoteRenderer.Render(target, stored, playRate, false, renderSeconds);
            NoteScore score = whole.Score(Head(played, 0, scoreSeconds));
            NoteScore? seam = null;
            if (end != PreMixEnd.None)
            {
                double from = Math.Max(0, switchSeconds - SeamSeconds);
                seam = Scorer(Head(ideal, from, 2 * SeamSeconds), notes[w.Notes[0]].RateHz).Score(Head(played, from, 2 * SeamSeconds));
            }

            PreMixPart[] parts = [.. w.Notes.Select(n => new PreMixPart(notes[n].Sample, notes[n].FirstTick - w.StartTick, notes[n].RateHz, notes[n].StartFrame, notes[n].Volume))];
            PreMixCandidate candidate = new(parts, same.Count, saves, windowSeconds, switchSeconds, end, PcmTargets.Bytes(target, stored.Length), tail, clipDb, score, seam);
            using Hasher both = new(BytesKey(stored));
            both.Add(tail is null ? default : BytesKey(tailPoints));
            Fingerprint bytes = both.Finish();
            return (candidate, bytes);
        }

        /// <summary>
        /// The tail of the outlasting note <paramref name="o"/>: its sample from where the note is at the switch, at its rate
        /// and volume then (and the mix's <paramref name="scale"/>), as long as it sounds; a looped one as long as the longest
        /// of <paramref name="same"/>, at most <see cref="MaxTailSeconds"/>.
        /// </summary>
        private (PreMixTail Tail, float[] Points, double PlayRate) Tail(List<Window> same, int o, double switchSeconds, float scale)
        {
            Window w = same[0];
            SongNote note = notes[o];
            Sample sample = SampleOf(o);
            (int start, ChannelTick now) = FrameAt(o, Time(w.StartTick) + switchSeconds);
            double rate = now.Pitch.PlaybackRateHz;
            double left = same.Max(s => Time(End(notes[s.Outlast!.Value])) - Time(s.StartTick) - switchSeconds); // Every window of a tail key has one.
            int frames = sample.Loop is null
                ? Math.Max(0, sample.Length - start)
                : (int)Math.Ceiling(Math.Min(left, MaxTailSeconds) * rate) + 1;
            float[] data = SampleProcessor.Unroll(sample, start + frames);
            data = data.Length > start ? data[start..] : [];
            float gain = (float)now.Volume * scale;
            for (int i = 0; i < data.Length; i++)
            {
                data[i] *= gain;
            }

            double playRate = PcmTargets.PlayRateHz(target, rate, false);
            float[] points = data.Length == 0 ? [] : SampleProcessor.Resample(data, rate, playRate, ResamplingMode.Smooth);
            Fingerprint key = BytesKey(points);
            if (!_tailIds.TryGetValue(key, out int id))
            {
                id = _tailIds.Count;
                _tailIds[key] = id;
            }

            return (new PreMixTail(id, note.Sample, start, rate, points.Length, PcmTargets.Bytes(target, points.Length)), points, playRate);
        }

        /// <summary>The frame note <paramref name="n"/> is at <paramref name="at"/> seconds into the song (its loop played out), and its tick then.</summary>
        private (int Frame, ChannelTick Now) FrameAt(int n, double at)
        {
            SongNote note = notes[n];
            double frame = note.StartFrame;
            int t = note.FirstTick;
            for (; t < End(note) && Time(t + 1) <= at; t++)
            {
                frame += Channel(t, note).Pitch.PlaybackRateHz * _playback.Ticks[t].DurationSeconds;
            }

            int last = Math.Min(t, End(note) - 1);
            ChannelTick now = Channel(last, note);
            frame += now.Pitch.PlaybackRateHz * Math.Max(0, at - Time(last));
            return ((int)Math.Round(frame), now);
        }

        /// <summary>True when the one-shot note <paramref name="n"/> has run out of frames by <paramref name="at"/> (it still sounds, but silently).</summary>
        private bool RanOut(int n, double at) => SampleOf(n) is { Loop: null } s && FrameAt(n, at).Frame >= s.Length - 1;

        /// <summary>
        /// The notes of <paramref name="w"/> as the MOD renderer plays them, in mono, from the window's start to the tick
        /// before <paramref name="endTick"/>, scaled so one note at full volume is full scale; at least <paramref name="seconds"/> long.
        /// </summary>
        private float[] Mix(Window w, int endTick, double seconds)
        {
            SampleBlock silence = new(null, 0, 0);
            double start = Time(w.StartTick);
            List<PlaybackTick> ticks = [];
            for (int t = w.StartTick; t < Math.Min(endTick, _playback.Ticks.Count); t++)
            {
                PlaybackTick tick = _playback.Ticks[t];
                ChannelTick[] channels = new ChannelTick[w.Notes.Length];
                for (int k = 0; k < w.Notes.Length; k++)
                {
                    SongNote note = notes[w.Notes[k]];
                    ChannelTick channel = tick.Channels[note.Channel];
                    channels[k] = t >= note.FirstTick && t < End(note)
                        ? channel with { Pan = 0 }
                        : new ChannelTick(false, channel.Pitch, 0, 0, null, silence);
                }

                ticks.Add(tick with { TimeSeconds = tick.TimeSeconds - start, Channels = channels });
            }

            float[] left = ModRenderer.Render(song.Song, new Playback(ticks, null, PlaybackEnd.Stops), new ModRenderOptions(SampleAnalyzer.AnalysisRateHz, 0)).Left;
            float[] mix = new float[Math.Max(left.Length, (int)Math.Ceiling(seconds * SampleAnalyzer.AnalysisRateHz))];
            for (int i = 0; i < left.Length; i++)
            {
                mix[i] = (float)(left[i] / ModRenderer.MonoChannelLevel);
            }

            return mix;
        }

        /// <summary>Where the tail starts: on DMF the window's end (DefleMask plays on the song's ticks), on XGM and XGM2 the next frame.</summary>
        private double Switch(double windowSeconds) =>
            target == PcmTarget.Dmf ? windowSeconds : Math.Ceiling((windowSeconds * FrameHz) - 1e-9) / FrameHz;

        /// <summary>How long a pre-mixed sample's 256-byte padding can last on XGM and XGM2, at most: what it holds of the outlasting note.</summary>
        private double PadSeconds(Window w) =>
            target == PcmTarget.Dmf ? 0 : XgmAlignment / PcmTargets.PlayRateHz(target, notes[w.Notes[0]].RateHz, false);

        /// <summary>Tick lengths from <paramref name="from"/> to before <paramref name="to"/>.</summary>
        private void AppendDurations(Hasher key, int from, int to)
        {
            key.Add(to - from);
            for (int t = from; t < to; t++)
            {
                key.Add(_playback.Ticks[t].DurationSeconds);
            }
        }

        /// <summary>A note's sample, offset, start and its pitch and volume on every tick of the window.</summary>
        private void AppendNote(Hasher key, Window w, int n, int to)
        {
            SongNote note = notes[n];
            key.Add(note.Sample);
            key.Add(note.FirstTick - w.StartTick);
            key.Add(note.StartFrame);
            AppendFrom(key, n, note.FirstTick, Math.Min(End(note), to));
        }

        /// <summary>A note's pitch, volume and tick length on every tick from <paramref name="from"/> to before <paramref name="to"/> (within the note), after their count.</summary>
        private void AppendFrom(Hasher key, int n, int from, int to)
        {
            SongNote note = notes[n];
            int end = Math.Min(to, End(note));
            key.Add(Math.Max(0, end - from));
            for (int t = from; t < end; t++)
            {
                ChannelTick channel = Channel(t, note);
                key.Add(channel.Pitch.PlaybackRateHz);
                key.Add(channel.Volume);
                key.Add(_playback.Ticks[t].DurationSeconds);
            }
        }

        private ChannelTick Channel(int tick, SongNote note) => _playback.Ticks[tick].Channels[note.Channel];

        /// <summary>The note's sample, without the data past its loop that never plays.</summary>
        private Sample SampleOf(int n) => SampleProcessor.CutAfterLoop(song.Song.Samples[notes[n].Sample]);

        /// <summary>When tick <paramref name="t"/> starts; the song's end for the tick after its last.</summary>
        private double Time(int t) => t < _playback.Ticks.Count ? _playback.Ticks[t].TimeSeconds : _playback.DurationSeconds;

        private double Seconds(int from, int to) => Time(to) - Time(from);

        /// <summary>The tick playing at <paramref name="seconds"/> from the song's start (the last for a time past its end).</summary>
        private int TickAt(double seconds)
        {
            int t = 0;
            int high = _playback.Ticks.Count - 1;
            while (t < high)
            {
                int mid = (t + high + 1) / 2;
                if (_playback.Ticks[mid].TimeSeconds <= seconds)
                {
                    t = mid;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return t;
        }

        /// <summary>A scorer aiming at <paramref name="sound"/>: a mix has no one pitch, so only its mel bands are compared.</summary>
        private static NoteScorer Scorer(float[] sound, double rateHz) => new(new NoteTarget(rateHz, null, SampleAnalyzer.Spectrum(sound, null)));

        /// <summary>A fingerprint of the target's bytes for <paramref name="points"/>: 16-bit for DMF, 8-bit for XGM and XGM2.</summary>
        private Fingerprint BytesKey(float[] points) => Fingerprint.Of(target == PcmTarget.Dmf
            ? MemoryMarshal.AsBytes(SampleProcessor.ToSigned16(points, string.Empty, Silent).AsSpan())
            : MemoryMarshal.AsBytes(SampleProcessor.ToSigned8(points, string.Empty, Silent).AsSpan()));

        /// <summary><paramref name="seconds"/> of <paramref name="sound"/> (at the analysis rate) from <paramref name="from"/>, or what there is.</summary>
        private static float[] Head(float[] sound, double from, double seconds)
        {
            int start = Math.Min(sound.Length, (int)Math.Round(from * SampleAnalyzer.AnalysisRateHz));
            int end = Math.Min(sound.Length, start + (int)Math.Round(seconds * SampleAnalyzer.AnalysisRateHz));
            return sound[start..end];
        }
    }
}
