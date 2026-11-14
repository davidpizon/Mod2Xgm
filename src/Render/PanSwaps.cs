namespace Mod2Xgm.Render;

/// <summary>
/// One moment an instrument changes the side it is heard on while its side in the MOD stays the same (plan T9.4r,
/// docs/adr/0108-pan-swap-count.md). Silence between the two sides doesn't hide it: the MOD kept its side across it.
/// </summary>
/// <param name="Sample">The song sample (0-based).</param>
/// <param name="Seconds">When the new side starts, in seconds of the render.</param>
/// <param name="Original">The side the MOD plays it on, before and after: −1 left, 0 both, +1 right.</param>
/// <param name="From">The side it was heard on before.</param>
/// <param name="To">The side it is heard on after.</param>
/// <param name="Cause">What puts it off its own side: the cause on the side that isn't <paramref name="Original"/> (after, when both aren't).</param>
/// <param name="LastsSeconds">How long it stays on <paramref name="To"/>, silences between its notes included.</param>
/// <param name="GapSeconds">How long the instrument was silent just before the change; 0 when it changed while playing.</param>
/// <param name="Volume">The mean MOD volume (0–1) while it plays on the side that isn't <paramref name="Original"/>.</param>
/// <param name="ChannelChanged">True when the note after the change comes from another MOD channel (on the same side).</param>
internal sealed record PanSwap(int Sample, double Seconds, int Original, int From, int To, PanCause Cause, double LastsSeconds, double GapSeconds, double Volume, bool ChannelChanged)
{
    /// <summary>How far it moves: 1 between one side and both, 2 from one side to the other.</summary>
    public int Distance => Math.Abs(To - From);

    /// <summary>Which way it goes from the MOD's side.</summary>
    public PanSwapKind Kind => From == Original ? PanSwapKind.Away : To == Original ? PanSwapKind.Back : PanSwapKind.Across;
}

/// <summary>Which way a <see cref="PanSwap"/> goes from the MOD's side.</summary>
internal enum PanSwapKind
{
    /// <summary>From the MOD's side to another.</summary>
    Away,

    /// <summary>From another side back to the MOD's.</summary>
    Back,

    /// <summary>From one wrong side to another (both, then the far side).</summary>
    Across,
}

/// <summary>How one instrument sounds over the whole render, side-wise.</summary>
/// <param name="Sample">The song sample (0-based).</param>
/// <param name="SoundingSeconds">How long it plays, counting each tick it sounds once.</param>
/// <param name="OffSideSeconds">How much of that it is heard on a side other than its MOD channel's.</param>
/// <param name="Swaps">How many <see cref="PanSwap"/>s it makes.</param>
internal sealed record PanSampleSummary(int Sample, double SoundingSeconds, double OffSideSeconds, int Swaps)
{
    /// <summary>True when it is off its side some of the time but never swaps: steadily on the wrong side.</summary>
    public bool SteadilyOffSide => Swaps == 0 && OffSideSeconds > 0;
}

/// <summary>The swaps of one render and each instrument's summary.</summary>
/// <param name="Swaps">Every swap, by sample then time.</param>
/// <param name="Samples">One summary per sample that sounds, by sample.</param>
internal sealed record PanSwapCount(IReadOnlyList<PanSwap> Swaps, IReadOnlyList<PanSampleSummary> Samples);

/// <summary>
/// Finds the pan swaps the MOD doesn't make in a <see cref="PanLog"/> (plan T9.4r, docs/adr/0108-pan-swap-count.md): per
/// instrument (song sample), each change of the side it is heard on while its MOD side stays the same, however long the
/// silence between. A change of MOD side (the instrument moved to a channel on another side) starts over.
/// </summary>
internal static class PanSwaps
{
    /// <summary>Counts the swaps in <paramref name="log"/>.</summary>
    public static PanSwapCount Count(PanLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        List<PanSwap> swaps = [];
        List<PanSampleSummary> samples = [];
        foreach (IGrouping<int, PanTick> sample in log.Ticks.GroupBy(t => t.Sample).OrderBy(g => g.Key))
        {
            List<Run> runs = Runs(States(sample));
            int before = swaps.Count;
            for (int i = 1; i < runs.Count; i++)
            {
                if (Swap(runs[i - 1], runs[i]) is { } swap)
                {
                    swaps.Add(swap);
                }
            }

            double sounding = runs.Sum(r => r.SoundingSeconds);
            double offSide = runs.Where(r => r.Original is { } o && r.Heard != o).Sum(r => r.SoundingSeconds);
            samples.Add(new PanSampleSummary(sample.Key, sounding, offSide, swaps.Count - before));
        }

        return new PanSwapCount(swaps, samples);
    }

    /// <summary>The swap from <paramref name="previous"/> to <paramref name="next"/>, or <c>null</c> when it isn't one.</summary>
    private static PanSwap? Swap(Run previous, Run next)
    {
        if (previous.Original is not { } original || next.Original != original || previous.Heard == next.Heard)
        {
            return null;
        }

        Run off = next.Heard != original ? next : previous;
        return new PanSwap(
            next.Sample,
            next.Start,
            original,
            previous.Heard,
            next.Heard,
            off.Cause,
            next.End - next.Start,
            Math.Max(0, next.Start - previous.End),
            off.MeanVolume,
            previous.LastSource != next.FirstSource);
    }

    /// <summary>
    /// One state per tick the sample sounds: its loudest note's sides. When its notes on one tick have different MOD
    /// sides, the state has no <see cref="State.Original"/>, which breaks the chain.
    /// </summary>
    private static IEnumerable<State> States(IEnumerable<PanTick> ticks)
    {
        foreach (IGrouping<int, PanTick> tick in ticks.GroupBy(t => t.Tick))
        {
            PanTick loudest = tick.MaxBy(t => t.Volume);
            int? original = tick.All(t => t.Original == loudest.Original) ? loudest.Original : null;
            yield return new State(loudest, original);
        }
    }

    /// <summary>Joins consecutive states with the same sides into runs.</summary>
    private static List<Run> Runs(IEnumerable<State> states)
    {
        List<Run> runs = [];
        foreach (State state in states)
        {
            Run? last = runs.Count > 0 ? runs[^1] : null;
            if (last is null || last.Original != state.Original || last.Heard != state.Tick.Heard)
            {
                runs.Add(new Run(state));
            }
            else
            {
                last.Add(state);
            }
        }

        return runs;
    }

    /// <summary>The sample's loudest note on one tick, and the MOD side all its notes share (or <c>null</c>).</summary>
    private readonly record struct State(PanTick Tick, int? Original);

    /// <summary>Consecutive ticks of one sample heard on one side, with one MOD side.</summary>
    private sealed class Run
    {
        private double _volumeSum;
        private int _ticks;

        /// <summary>Starts a run at <paramref name="first"/>.</summary>
        public Run(State first)
        {
            Sample = first.Tick.Sample;
            Original = first.Original;
            Heard = first.Tick.Heard;
            Start = first.Tick.Seconds;
            FirstSource = first.Tick.Source;
            Add(first);
        }

        public int Sample { get; }

        public int? Original { get; }

        public int Heard { get; }

        public double Start { get; }

        public double End { get; private set; }

        public int FirstSource { get; }

        public int LastSource { get; private set; }

        public PanCause Cause { get; private set; }

        public double SoundingSeconds { get; private set; }

        public double MeanVolume => _volumeSum / _ticks;

        /// <summary>Adds one tick's state.</summary>
        public void Add(State state)
        {
            End = state.Tick.Seconds + state.Tick.DurationSeconds;
            LastSource = state.Tick.Source;
            SoundingSeconds += state.Tick.DurationSeconds;
            _volumeSum += state.Tick.Volume;
            _ticks++;
            if (Cause == PanCause.None)
            {
                Cause = state.Tick.Cause;
            }
        }
    }
}
