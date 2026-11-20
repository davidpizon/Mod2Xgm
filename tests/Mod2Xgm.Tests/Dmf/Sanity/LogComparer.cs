using System.Globalization;

namespace Mod2Xgm.Tests.Dmf.Sanity;

/// <summary>How many frames and items of one category differ.</summary>
/// <param name="Category">The category (<see cref="ChipState.Category"/>).</param>
/// <param name="Frames">Frames with at least one difference in it.</param>
/// <param name="Items">Differences in it, over all frames.</param>
internal sealed record CategoryCount(string Category, int Frames, int Items);

/// <summary>One difference, as an example for the report.</summary>
/// <param name="Frame">The frame (tick) it is in.</param>
/// <param name="Key">What differs.</param>
/// <param name="Reference">The reference's value or sequence there ("—" when it has none).</param>
/// <param name="Candidate">The candidate's.</param>
internal sealed record FrameDifference(int Frame, string Key, string Reference, string Candidate);

/// <summary>What one register-level check found.</summary>
/// <param name="FramesCompared">Frames compared: the reference's length.</param>
/// <param name="CandidateFrames">The candidate's length.</param>
/// <param name="ReferenceLoop">The reference's loop frame.</param>
/// <param name="CandidateLoop">The candidate's loop frame.</param>
/// <param name="FramesDiffering">Frames with at least one difference.</param>
/// <param name="FirstDifference">The first frame that differs, or <c>null</c>.</param>
/// <param name="Categories">Differences by category, most frames first.</param>
/// <param name="Examples">The first few differences of each category.</param>
internal sealed record LogComparison(
    int FramesCompared,
    int CandidateFrames,
    int? ReferenceLoop,
    int? CandidateLoop,
    int FramesDiffering,
    int? FirstDifference,
    IReadOnlyList<CategoryCount> Categories,
    IReadOnlyList<FrameDifference> Examples)
{
    /// <summary>True when nothing differs, lengths and loops included.</summary>
    public bool Same => FramesDiffering == 0 && FramesCompared == CandidateFrames && ReferenceLoop == CandidateLoop;
}

/// <summary>
/// The register-level checks of the DMF sanity check (docs/testing.md#dmf-sanity-check), each comparing a candidate log
/// (an XGM2 file decoded, or the DMF player's log) with DefleMask's VGM export, frame by frame.
/// </summary>
internal static class LogComparer
{
    /// <summary>Examples kept per category.</summary>
    private const int ExamplesPerCategory = 3;

    /// <summary>
    /// Check 1, "same writes per frame": what each frame changes — each register's last value when it differs from the
    /// frame's start, and in order every key change, noise write and PCM play or stop. A write that changes nothing and
    /// the order of writes within a frame don't count; both logs are compared over the reference's frames.
    /// </summary>
    public static LogComparison CompareWrites(ChipLog reference, ChipLog candidate)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        ChipState a = new();
        ChipState b = new();
        Tally tally = new();
        for (int f = 0; f < reference.Frames.Count; f++)
        {
            SortedDictionary<string, string> left = FrameChanges(a, reference.Frames[f]);
            SortedDictionary<string, string> right = FrameChanges(b, f < candidate.Frames.Count ? candidate.Frames[f] : []);
            foreach (string key in left.Keys.Union(right.Keys, StringComparer.Ordinal))
            {
                string l = left.GetValueOrDefault(key, "—");
                string r = right.GetValueOrDefault(key, "—");
                if (l != r)
                {
                    tally.Add(f, key, l, r);
                }
            }

            tally.EndFrame();
        }

        return tally.Result(reference, candidate);
    }

    /// <summary>
    /// Check 2, "same chip state per frame": every register, key, PSG and PCM value as each frame leaves it. Only where
    /// the state ends up counts, not how many writes got it there.
    /// </summary>
    public static LogComparison CompareState(ChipLog reference, ChipLog candidate)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        ChipState a = new();
        ChipState b = new();
        HashSet<string> differing = new(StringComparer.Ordinal);
        List<ChipChange> changes = [];
        Tally tally = new();
        for (int f = 0; f < reference.Frames.Count; f++)
        {
            HashSet<string> touched = new(StringComparer.Ordinal);
            foreach (ChipWrite w in reference.Frames[f])
            {
                changes.Clear();
                a.Apply(w, changes);
                touched.UnionWith(changes.Select(c => c.Key));
            }

            foreach (ChipWrite w in f < candidate.Frames.Count ? candidate.Frames[f] : [])
            {
                changes.Clear();
                b.Apply(w, changes);
                touched.UnionWith(changes.Select(c => c.Key));
            }

            foreach (string key in touched)
            {
                if (a.Values.GetValueOrDefault(key, -1) == b.Values.GetValueOrDefault(key, -1))
                {
                    differing.Remove(key);
                }
                else
                {
                    differing.Add(key);
                }
            }

            foreach (string key in differing)
            {
                tally.Add(f, key, Show(a.Values.GetValueOrDefault(key, -1)), Show(b.Values.GetValueOrDefault(key, -1)));
            }

            tally.EndFrame();
        }

        return tally.Result(reference, candidate);
    }

    /// <summary>
    /// <paramref name="log"/> with every PCM play and stop moved <paramref name="frames"/> later: an XGM2 file sends them
    /// ahead of the frame they are heard on (docs/adr/0119-xgm2-pcm-lead.md), so they are compared where they are heard.
    /// </summary>
    public static ChipLog ShiftPcm(ChipLog log, int frames)
    {
        ArgumentNullException.ThrowIfNull(log);
        List<List<ChipWrite>> shifted = [.. log.Frames.Select(f => f.Where(w => w.Target is ChipTarget.Fm or ChipTarget.Psg).ToList())];
        for (int f = 0; f < log.Frames.Count; f++)
        {
            foreach (ChipWrite w in log.Frames[f].Where(w => w.Target is ChipTarget.PcmPlay or ChipTarget.PcmStop))
            {
                int to = f + frames;
                while (shifted.Count <= to)
                {
                    shifted.Add([]);
                }

                shifted[to].Add(w);
            }
        }

        return log with { Frames = [.. shifted.Take(Math.Max(log.Frames.Count, 1))] };
    }

    /// <summary>What <paramref name="writes"/> change, applied to <paramref name="state"/>: key → new value, or the sequence of events.</summary>
    private static SortedDictionary<string, string> FrameChanges(ChipState state, IReadOnlyList<ChipWrite> writes)
    {
        Dictionary<string, int> before = new(StringComparer.Ordinal);
        Dictionary<string, List<int>> events = new(StringComparer.Ordinal);
        List<ChipChange> changes = [];
        foreach (ChipWrite w in writes)
        {
            changes.Clear();
            state.Apply(w, changes);
            foreach (ChipChange c in changes)
            {
                if (c.IsEvent)
                {
                    if (!events.TryGetValue(c.Key, out List<int>? list))
                    {
                        list = [];
                        events[c.Key] = list;
                    }

                    list.Add(c.After);
                }
                else
                {
                    before.TryAdd(c.Key, c.Before);
                }
            }
        }

        SortedDictionary<string, string> result = new(StringComparer.Ordinal);
        foreach ((string key, int was) in before)
        {
            int now = state.Values[key];
            if (now != was)
            {
                result[key] = Show(now);
            }
        }

        foreach ((string key, List<int> list) in events)
        {
            result[key] = string.Join(" > ", list.Select(Show));
        }

        return result;
    }

    private static string Show(int value) => value < 0 ? "—" : value.ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>Counts differences by category and keeps examples.</summary>
    private sealed class Tally
    {
        private readonly Dictionary<string, (int Frames, int Items, int LastFrame)> _categories = new(StringComparer.Ordinal);
        private readonly List<FrameDifference> _examples = [];
        private int _framesDiffering;
        private int? _first;
        private bool _frameDiffers;

        public void Add(int frame, string key, string reference, string candidate)
        {
            string category = ChipState.Category(key);
            (int frames, int items, int last) = _categories.GetValueOrDefault(category, (0, 0, -1));
            if (frames < ExamplesPerCategory || items < ExamplesPerCategory)
            {
                if (_examples.Count(e => ChipState.Category(e.Key) == category) < ExamplesPerCategory)
                {
                    _examples.Add(new FrameDifference(frame, key, reference, candidate));
                }
            }

            _categories[category] = (last == frame ? frames : frames + 1, items + 1, frame);
            _frameDiffers = true;
            _first ??= frame;
        }

        public void EndFrame()
        {
            if (_frameDiffers)
            {
                _framesDiffering++;
            }

            _frameDiffers = false;
        }

        public LogComparison Result(ChipLog reference, ChipLog candidate) => new(
            reference.Frames.Count,
            candidate.Frames.Count,
            reference.LoopFrame,
            candidate.LoopFrame,
            _framesDiffering,
            _first,
            [.. _categories.OrderByDescending(c => c.Value.Frames).ThenBy(c => c.Key, StringComparer.Ordinal).Select(c => new CategoryCount(c.Key, c.Value.Frames, c.Value.Items))],
            [.. _examples.OrderBy(e => e.Frame)]);
    }
}
