using System.Globalization;
using Mod2Xgm.Cli;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;

namespace Mod2Xgm.Dmf;

/// <summary>
/// Works out how a DMF must be clocked so it lasts as long as the MOD it came from (plan T5.4, ADR 0043). A MOD has
/// two dials, speed (ticks per row) and tempo (BPM, so ticks per second); a DMF has one tick rate for the whole song
/// (custom Hz) and two tick times (ticks per row) that alternate from row to row (docs/formats/dmf.md#4-module-information).
/// <para>The tick rate is the song's most-used one, so most rows keep their MOD tick count and per-tick effects keep their
/// speed. Every other speed/tempo pair is played with the tick times that give the same row length, in half-ticks (the
/// two tick times differ by one). Where whole-Hz rounding would make the song too long or short, the tick rate is
/// raised to a multiple of the song's, which makes the tick times finer; the plan says which multiple, for the effect
/// translation (plan T5.5) to scale per-tick effects.</para>
/// </summary>
internal static class DmfTimingPlanner
{
    /// <summary>DMF effect <c>09xx</c>: tick time 1 (docs/formats/dmf.md#effects).</summary>
    internal const int SpeedEffect1 = 0x09;

    /// <summary>DMF effect <c>0Fxx</c>: tick time 2 (docs/formats/dmf.md#effects).</summary>
    internal const int SpeedEffect2 = 0x0F;

    /// <summary>Highest custom Hz: it is stored as 3 decimal digits (docs/formats/dmf.md#4-module-information).</summary>
    private const int MaxCustomHz = 999;

    /// <summary>Highest tick time: one byte (docs/formats/dmf.md#4-module-information).</summary>
    private const int MaxTickTime = 255;

    /// <summary>Most digits the custom Hz field holds.</summary>
    private const int CustomHzDigits = 3;

    /// <summary>Ticks per second at one BPM: BPM × 2 / 5 (docs/genesis-audio.md#timing).</summary>
    private const double TicksPerBpm = 2.0 / 5.0;

    /// <summary>
    /// The song may be this much too long or short on average (1 %, the plan's bar for T5.4) before the tick rate is
    /// raised: a higher tick rate makes tick times finer but changes how fast per-tick effects run.
    /// </summary>
    private const double Tolerance = 0.01;

    /// <summary>Length errors under this (0.1 %) aren't worth a conversion note.</summary>
    private const double NegligibleError = 0.001;

    /// <summary>PAL and NTSC frame rates, which DefleMask plays at when custom Hz is off (docs/genesis-audio.md#timing).</summary>
    private const int PalHz = 50;

    private const int NtscHz = 60;

    /// <summary>Plans the clock of the DMF made from <paramref name="plan"/>.</summary>
    /// <param name="plan">The mapped song; its flow effects (<see cref="SetSpeed"/>, <see cref="SetTempo"/>) are read top to bottom.</param>
    /// <param name="sourceTickRateScale">The source Amiga's tick rate relative to PAL: 1 for PAL, 6/5 for NTSC (<c>--input-region</c>).</param>
    /// <param name="outputRegion">The Genesis the DMF is made for (<c>--output-region</c>): when the song's tick rate is its frame rate, custom Hz stays off.</param>
    /// <param name="notes">Collects conversion notes.</param>
    public static DmfTimingPlan Plan(GenesisPlan plan, double sourceTickRateScale, Region outputRegion, ConversionNotes notes)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceTickRateScale);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(plan.Timing.Speed);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(plan.Timing.Bpm);

        Walk walk = Walk.Through(plan);
        State start = new(plan.Timing.Speed, plan.Timing.Bpm);
        State dominant = walk.Seconds.Count == 0 ? start : walk.Seconds.MaxBy(pair => pair.Value).Key;
        double dominantRate = TickRate(dominant, sourceTickRateScale);

        Choice choice = Choose(walk, dominantRate, sourceTickRateScale);
        Dictionary<State, TickTimes> times = [];
        foreach (State state in walk.Seconds.Keys.Append(start).Append(walk.End))
        {
            times[state] = TickTimesFor(state, choice.Hz, sourceTickRateScale);
        }

        int frameHz = outputRegion == Region.Ntsc ? NtscHz : PalHz;
        bool custom = choice.Hz != frameHz;
        DmfTiming timing = new(
            TimeBase: 0,
            times[start].First,
            times[start].Second,
            outputRegion == Region.Ntsc,
            custom,
            custom ? CustomHzBytes(choice.Hz) : [],
            custom ? choice.Hz : null);

        TickTimes?[][] changes = Changes(plan, walk, times, start);
        Report(walk, choice, dominantRate, notes);
        return new DmfTimingPlan(timing, choice.Factor, changes, choice.LengthError);
    }

    /// <summary>The tick rate of a speed/tempo pair, in ticks per second at the source Amiga.</summary>
    private static double TickRate(State state, double scale) => state.Bpm * TicksPerBpm * scale;

    /// <summary>
    /// The two tick times that make a row of <paramref name="state"/> as long as it was in the MOD at tick rate
    /// <paramref name="hz"/>. Their sum is the ticks in two rows, so it is whole while one row can be half a tick off.
    /// </summary>
    private static TickTimes TickTimesFor(State state, int hz, double scale)
    {
        int sum = SumFor(state, hz, scale, out _);
        return new TickTimes((sum + 1) / 2, sum / 2);
    }

    /// <summary>Tick time 1 + tick time 2 for <paramref name="state"/> at <paramref name="hz"/>; <paramref name="exact"/> is the unrounded value.</summary>
    private static int SumFor(State state, int hz, double scale, out double exact)
    {
        exact = 2.0 * hz * state.Speed / TickRate(state, scale);
        return (int)Math.Clamp(Math.Round(exact, MidpointRounding.AwayFromZero), 2, 2 * MaxTickTime);
    }

    /// <summary>
    /// Picks the tick rate: the song's most-used rate (times 1, 2, 3 … until the average length error is within
    /// <see cref="Tolerance"/>), or the least-wrong one when none is.
    /// </summary>
    private static Choice Choose(Walk walk, double dominantRate, double scale)
    {
        double total = walk.Seconds.Values.Sum();
        Choice? best = null;
        for (int factor = 1; ; factor++)
        {
            int hz = (int)Math.Max(1, Math.Round(factor * dominantRate, MidpointRounding.AwayFromZero));
            if (hz > MaxCustomHz)
            {
                break;
            }

            double cost = 0;
            double signed = 0;
            foreach ((State state, double seconds) in walk.Seconds)
            {
                int sum = SumFor(state, hz, scale, out double exact);
                double error = sum / exact - 1;
                cost += seconds * Math.Abs(error);
                signed += seconds * error;
            }

            Choice candidate = total == 0 ? new Choice(hz, factor, 0, 0) : new Choice(hz, factor, cost / total, signed / total);
            if (best is null || candidate.Cost < best.Cost)
            {
                best = candidate;
            }

            if (candidate.Cost <= Tolerance)
            {
                break;
            }
        }

        return best ?? throw new ArgumentOutOfRangeException(nameof(dominantRate), dominantRate, "The song's tick rate is over the highest custom Hz.");
    }

    /// <summary>
    /// The tick-time pair to set on each row, or <c>null</c> where the previous one still holds. The song's first pair is
    /// in <see cref="DmfTiming"/>. The loop target restates its pair when the song's end would leave a different one.
    /// </summary>
    private static TickTimes?[][] Changes(GenesisPlan plan, Walk walk, Dictionary<State, TickTimes> times, State start)
    {
        TickTimes current = times[start];
        TickTimes?[][] changes = [.. plan.Patterns.Select(rows => new TickTimes?[rows.Count])];
        TickTimes afterTargetRow = current;
        int target = plan.LoopOrder ?? 0;
        for (int p = 0; p < plan.Patterns.Count; p++)
        {
            for (int r = 0; r < plan.Patterns[p].Count; r++)
            {
                TickTimes next = times[walk.After[p][r]];
                if (next != current)
                {
                    changes[p][r] = next;
                    current = next;
                }

                if (p == target && r == 0)
                {
                    afterTargetRow = next;
                }
            }
        }

        if (plan.Patterns.Count > 0 && plan.Patterns[target].Count > 0 && changes[target][0] is null && current != afterTargetRow)
        {
            changes[target][0] = afterTargetRow;
        }

        return changes;
    }

    /// <summary>Says what the tick rate and tick times cost, when it is worth saying.</summary>
    private static void Report(Walk walk, Choice choice, double dominantRate, ConversionNotes notes)
    {
        if (Math.Abs(choice.Hz - (choice.Factor * dominantRate)) > 1e-9)
        {
            notes.Add(
                "The song's tick rate is {Rate} Hz; DMF holds whole Hz only, so it is written as {Hz} Hz",
                Math.Round(choice.Factor * dominantRate, 2),
                choice.Hz);
        }

        if (choice.Factor > 1)
        {
            notes.Add(
                "Whole Hz alone would leave the song's rows off by too much, so the DMF ticks {Factor} times as fast as the MOD and tick times are {Factor} times as long",
                choice.Factor);
        }

        int tempos = walk.Seconds.Keys.Select(s => s.Bpm).Distinct().Count();
        if (tempos > 1)
        {
            notes.Add(
                "The song changes tempo between {Count} values; DMF has one tick rate, so rows at the other tempos get tick times that make them as long as the MOD's",
                tempos);
        }

        if (walk.Seconds.Count > 1 && walk.HasJumps)
        {
            notes.Add("The song changes speed or tempo and also jumps or loops; DMF timing follows the rows top to bottom, so a change that depends on the way through the jumps may be off");
        }

        if (Math.Abs(choice.LengthError) >= NegligibleError)
        {
            notes.Add(
                "The DMF's rows add up to {Percent:0.##} % {Direction} than the MOD's",
                Math.Abs(choice.LengthError) * 100,
                choice.LengthError > 0 ? "longer" : "shorter");
        }

        if (choice.Cost > Tolerance)
        {
            notes.Add("Timing can't be made closer than {Percent:0.##} % on average within DMF's limits", choice.Cost * 100);
        }
    }

    /// <summary>The custom Hz field: decimal digits, left-aligned, padded with 0 (docs/formats/dmf.md#4-module-information).</summary>
    private static byte[] CustomHzBytes(int hz)
    {
        byte[] bytes = new byte[CustomHzDigits];
        string digits = hz.ToString(CultureInfo.InvariantCulture);
        for (int i = 0; i < digits.Length; i++)
        {
            bytes[i] = (byte)digits[i];
        }

        return bytes;
    }

    /// <summary>Speed and tempo at some point of the song.</summary>
    private readonly record struct State(int Speed, double Bpm);

    /// <summary>The tick rate picked, its factor over the song's own, and how well it fits.</summary>
    /// <param name="Hz">Custom Hz.</param>
    /// <param name="Factor">Multiple of the song's most-used tick rate that <paramref name="Hz"/> rounds.</param>
    /// <param name="Cost">Average of each row's length error without its sign, weighted by the seconds it lasts.</param>
    /// <param name="LengthError">Average length error with its sign: positive means the DMF is longer.</param>
    private sealed record Choice(int Hz, int Factor, double Cost, double LengthError);

    /// <summary>One pass over the plan's rows, top to bottom: the state after each row and the time spent in each state.</summary>
    private sealed class Walk
    {
        private Walk(State[][] after, Dictionary<State, double> seconds, State end, bool hasJumps)
        {
            After = after;
            Seconds = seconds;
            End = end;
            HasJumps = hasJumps;
        }

        /// <summary>The state after each row's flow effects, by pattern and row.</summary>
        public State[][] After { get; }

        /// <summary>Seconds each state lasts at the MOD's own timing, up to the first <see cref="StopSong"/>.</summary>
        public Dictionary<State, double> Seconds { get; }

        /// <summary>The state after the last row.</summary>
        public State End { get; }

        /// <summary>True when any row jumps, breaks or loops.</summary>
        public bool HasJumps { get; }

        public static Walk Through(GenesisPlan plan)
        {
            State state = new(plan.Timing.Speed, plan.Timing.Bpm);
            State[][] after = new State[plan.Patterns.Count][];
            Dictionary<State, double> seconds = [];
            bool stopped = false;
            bool hasJumps = false;
            for (int p = 0; p < plan.Patterns.Count; p++)
            {
                after[p] = new State[plan.Patterns[p].Count];
                for (int r = 0; r < plan.Patterns[p].Count; r++)
                {
                    // The row that stops the song still plays; the rows after it don't.
                    bool counted = !stopped;
                    foreach (Effect effect in plan.Patterns[p][r].Flow)
                    {
                        switch (effect)
                        {
                            case SetSpeed { TicksPerRow: > 0 } speed:
                                state = state with { Speed = speed.TicksPerRow };
                                break;
                            case SetTempo { Bpm: > 0 } tempo:
                                state = state with { Bpm = tempo.Bpm };
                                break;
                            case StopSong:
                                stopped = true;
                                break;
                            case PositionJump or PatternBreak or PatternLoop or PatternLoopStart:
                                hasJumps = true;
                                break;
                        }
                    }

                    after[p][r] = state;
                    if (counted)
                    {
                        seconds[state] = seconds.GetValueOrDefault(state) + (state.Speed / (state.Bpm * TicksPerBpm));
                    }
                }
            }

            return new Walk(after, seconds, state, hasJumps);
        }
    }
}

/// <summary>
/// The clock of a DMF (plan T5.4): its timing fields, and where the speed changes go.
/// </summary>
/// <param name="Timing">The module's timing: tick times for the first row, custom Hz or the region's rate.</param>
/// <param name="TicksPerSourceTick">How many DMF ticks one MOD tick lasts: 1 when the tick rate is the song's own, more when it was raised to fit. Effects that act once per tick (slides, vibrato, arpeggio, retrigger) must be scaled by this.</param>
/// <param name="Changes">For each pattern and row of the plan, the tick times to set on that row, or <c>null</c> to leave them.</param>
/// <param name="LengthError">How much longer (positive) or shorter the DMF is than the MOD on average over the song, as a fraction.</param>
internal sealed record DmfTimingPlan(DmfTiming Timing, int TicksPerSourceTick, IReadOnlyList<IReadOnlyList<TickTimes?>> Changes, double LengthError);

/// <summary>A pair of DMF tick times: ticks in even rows and in odd rows.</summary>
/// <param name="First">Tick time 1: set by effect <c>09xx</c>.</param>
/// <param name="Second">Tick time 2: set by effect <c>0Fxx</c>.</param>
internal readonly record struct TickTimes(int First, int Second)
{
    /// <summary>The two effects that set these tick times, ready for an effect column (<c>09xx</c>, <c>0Fxx</c>).</summary>
    public DmfEffect[] Effects() => [new(DmfTimingPlanner.SpeedEffect1, First), new(DmfTimingPlanner.SpeedEffect2, Second)];
}
