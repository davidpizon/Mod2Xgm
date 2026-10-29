using Mod2Xgm.Cli;
using Mod2Xgm.Dmf;
using Mod2Xgm.Mapping;
using Mod2Xgm.Mod;
using Mod2Xgm.Model;
using Mod2Xgm.Tests.Mod;
using Serilog.Core;

namespace Mod2Xgm.Tests.Dmf;

/// <summary>
/// Tests for <see cref="DmfTimingPlanner"/> (plan T5.4, ADR 0043): the tick rate, the tick times, the speed changes
/// along the song, and that the DMF lasts as long as the tick player says the MOD does (within 1 %).
/// </summary>
public sealed class DmfTimingPlannerTests
{
    private const int RowsPerPattern = 64;

    /// <summary>A plan of empty rows with the given flow effects: <paramref name="flows"/> maps (pattern, row) to what happens there.</summary>
    private static GenesisPlan Plan(Timing timing, int patterns, Dictionary<(int Pattern, int Row), Effect[]>? flows = null, int? loopOrder = null)
    {
        flows ??= [];
        IReadOnlyList<PlanRow>[] built =
        [
            .. Enumerable.Range(0, patterns).Select(p => (IReadOnlyList<PlanRow>)[.. Enumerable.Range(0, RowsPerPattern).Select(r =>
                new PlanRow([.. Enumerable.Repeat(PlanCell.Empty, GenesisPlan.ChannelCount)], flows.GetValueOrDefault((p, r), [])))]),
        ];
        return new GenesisPlan("test", built, loopOrder, timing, [], []);
    }

    private static Dictionary<(int, int), Effect[]> Flow(params ((int Pattern, int Row) At, Effect[] Effects)[] entries) =>
        entries.ToDictionary(e => e.At, e => e.Effects);

    private static (DmfTimingPlan Result, List<string> Notes) Run(GenesisPlan plan, double scale = 1.0, Region output = Region.Pal)
    {
        CollectingSink sink = new();
        ConversionNotes notes = new(sink.CreateLogger());
        DmfTimingPlan result = DmfTimingPlanner.Plan(plan, scale, output, notes);
        return (result, [.. notes.Messages]);
    }

    /// <summary>The fields of a timing as plain values: <see cref="DmfTiming"/> holds its Hz digits in a list, which records compare by reference.</summary>
    private static (int TimeBase, int First, int Second, bool Ntsc, bool Custom, string Digits, int? Hz) Fields(DmfTiming t) =>
        (t.TimeBase, t.TickTime1, t.TickTime2, t.Ntsc, t.UsingCustomHz, string.Concat(t.CustomHzBytes.Select(b => (char)b)), t.CustomHz);

    /// <summary>The seconds the DMF takes to play the plan's rows once, top to bottom, as DefleMask clocks them.</summary>
    private static double DmfSeconds(GenesisPlan plan, DmfTimingPlan timing)
    {
        TickTimes current = new(timing.Timing.TickTime1, timing.Timing.TickTime2);
        double ticks = 0;
        for (int p = 0; p < plan.Patterns.Count; p++)
        {
            for (int r = 0; r < plan.Patterns[p].Count; r++)
            {
                if (timing.Changes[p][r] is { } change)
                {
                    current = change;
                }

                ticks += r % 2 == 0 ? current.First : current.Second;
            }
        }

        return ticks / timing.Timing.TickRateHz;
    }

    /// <summary>At 125 BPM and speed 6 a PAL song is DefleMask's own 50 Hz with six ticks a row: no custom Hz, nothing to note.</summary>
    [Fact]
    public void ThePalDefaultNeedsNoCustomHz()
    {
        (DmfTimingPlan result, List<string> notes) = Run(Plan(new Timing(6, 125), 2));

        Assert.Equal((0, 6, 6, false, false, string.Empty, (int?)null), Fields(result.Timing));
        Assert.Equal(1, result.TicksPerSourceTick);
        Assert.All(result.Changes.SelectMany(c => c), change => Assert.Null(change));
        Assert.Empty(notes);
    }

    /// <summary>An NTSC Amiga ticks at 60 Hz at 125 BPM: that is an NTSC Genesis's frame rate, but a PAL Genesis needs custom Hz.</summary>
    [Fact]
    public void TheTickRateFollowsTheInputRegionAgainstTheOutputRegion()
    {
        (DmfTimingPlan ntscToNtsc, _) = Run(Plan(new Timing(6, 125), 1), 6.0 / 5.0, Region.Ntsc);
        (DmfTimingPlan ntscToPal, _) = Run(Plan(new Timing(6, 125), 1), 6.0 / 5.0, Region.Pal);
        (DmfTimingPlan palToNtsc, _) = Run(Plan(new Timing(6, 125), 1), 1.0, Region.Ntsc);

        Assert.Equal((0, 6, 6, true, false, string.Empty, (int?)null), Fields(ntscToNtsc.Timing));
        Assert.Equal((0, 6, 6, false, true, "60\0", (int?)60), Fields(ntscToPal.Timing));
        Assert.Equal((0, 6, 6, true, true, "50\0", (int?)50), Fields(palToNtsc.Timing));
    }

    /// <summary>A tempo that makes a whole tick rate (130 BPM = 52 Hz) is written as custom Hz, ASCII digits padded with 0.</summary>
    [Fact]
    public void AWholeTickRateBecomesCustomHz()
    {
        (DmfTimingPlan result, List<string> notes) = Run(Plan(new Timing(6, 130), 1));

        Assert.Equal((0, 6, 6, false, true, "52\0", (int?)52), Fields(result.Timing));
        Assert.Equal(1, result.TicksPerSourceTick);
        Assert.Empty(notes);
    }

    /// <summary>The custom Hz field holds three digits: 250 BPM is 100 Hz, written without padding.</summary>
    [Fact]
    public void ThreeDigitRatesFillTheField()
    {
        (DmfTimingPlan result, _) = Run(Plan(new Timing(6, 250), 1));

        Assert.Equal("100"u8.ToArray(), result.Timing.CustomHzBytes);
        Assert.Equal(100, result.Timing.CustomHz);
    }

    /// <summary>123 BPM is 49.2 Hz: 49 Hz is 0.4 % off, within the 1 % bar, so it's kept and the rounding is noted.</summary>
    [Fact]
    public void ARateThatIsNotWholeIsRoundedAndSaid()
    {
        (DmfTimingPlan result, List<string> notes) = Run(Plan(new Timing(6, 123), 1));

        Assert.Equal(49, result.Timing.CustomHz);
        Assert.Equal(1, result.TicksPerSourceTick);
        Assert.Equal(0.0041, result.LengthError, 4);
        Assert.Contains(notes, n => n == "The song's tick rate is 49.2 Hz; DMF holds whole Hz only, so it is written as 49 Hz");
        Assert.Contains(notes, n => n == "The DMF's rows add up to 0.41 % longer than the MOD's");
    }

    /// <summary>33 BPM is 13.2 Hz: 13 Hz is 1.5 % off, so the DMF ticks 3 times as fast (40 Hz, 0.99 % off) and the tick times triple.</summary>
    [Fact]
    public void ASlowRateThatRoundsBadlyIsRaisedToAMultiple()
    {
        (DmfTimingPlan result, List<string> notes) = Run(Plan(new Timing(6, 33), 1));

        Assert.Equal(40, result.Timing.CustomHz);
        Assert.Equal(3, result.TicksPerSourceTick);
        Assert.Equal((18, 18), (result.Timing.TickTime1, result.Timing.TickTime2));
        Assert.Contains(notes, n => n.Contains("ticks 3 times as fast", StringComparison.Ordinal));
    }

    /// <summary>A tempo that gives a whole number of ticks per row at the song's tick rate (125 → 150 BPM at 50 Hz is 5 ticks) is a plain tick time change on that row.</summary>
    [Fact]
    public void ATempoChangeBecomesTickTimesOnItsRow()
    {
        GenesisPlan plan = Plan(new Timing(6, 125), 1, Flow(((0, 32), [new SetTempo(150)])));

        (DmfTimingPlan result, List<string> notes) = Run(plan);

        Assert.Equal(50, result.Timing.TickRateHz);
        Assert.False(result.Timing.UsingCustomHz);
        for (int r = 0; r < RowsPerPattern; r++)
        {
            // The song ends in the new tempo and DefleMask loops to order 0, so row 0 restates the first pair.
            TickTimes? expected = r switch { 0 => new TickTimes(6, 6), 32 => new TickTimes(5, 5), _ => null };
            Assert.Equal(expected, result.Changes[0][r]);
        }

        Assert.Contains(notes, n => n.StartsWith("The song changes tempo between 2 values", StringComparison.Ordinal));
        Assert.InRange(DmfSeconds(plan, result) / ((32 * 6 / 50.0) + (32 * 6 / 60.0)), 0.99, 1.01);
    }

    /// <summary>When a row takes a fraction of a tick more, the two tick times differ by one: 135 BPM is 54 Hz, so six MOD ticks are 5.56 DMF ticks, played as 6 and 5.</summary>
    [Fact]
    public void AHalfTickIsDoneWithTwoTickTimes()
    {
        (DmfTimingPlan result, _) = Run(Plan(new Timing(6, 125), 1, Flow(((0, 60), [new SetTempo(135)]))));

        Assert.Equal(new TickTimes(6, 5), result.Changes[0][60]);
        Assert.Equal(new TickTimes(6, 6), new TickTimes(result.Timing.TickTime1, result.Timing.TickTime2));
    }

    /// <summary>A speed change sets both tick times to the new speed.</summary>
    [Fact]
    public void ASpeedChangeSetsBothTickTimes()
    {
        (DmfTimingPlan result, _) = Run(Plan(new Timing(6, 125), 1, Flow(((0, 20), [new SetSpeed(3)]))));

        Assert.Equal(new TickTimes(3, 3), result.Changes[0][20]);
        Assert.Equal(new TickTimes(6, 6), result.Changes[0][0]); // the loop back to order 0 restates the first pair
        Assert.Equal(2, result.Changes.SelectMany(c => c).Count(c => c is not null));
        Assert.Equal(TickTimesEffects(3, 3), result.Changes[0][20]!.Value.Effects());
    }

    /// <summary>Both effect columns: <c>09xx</c> sets tick time 1 and <c>0Fxx</c> tick time 2.</summary>
    private static DmfEffect[] TickTimesEffects(int first, int second) => [new(0x09, first), new(0x0F, second)];

    /// <summary>Speed and tempo on the same row are both applied before the row's tick times are worked out.</summary>
    [Fact]
    public void SpeedAndTempoOnOneRowCountTogether()
    {
        (DmfTimingPlan result, _) = Run(Plan(new Timing(6, 125), 1, Flow(((0, 60), [new SetSpeed(4), new SetTempo(150)]))));

        // 4 ticks at 60 Hz, at the song's 50 Hz clock: 2 × 50 × 4 / 60 = 6.67 → 7 in two rows.
        Assert.Equal(new TickTimes(4, 3), result.Changes[0][60]);
    }

    /// <summary>The tick rate is the one the song spends most time at, not the one it starts with.</summary>
    [Fact]
    public void TheMostUsedTempoSetsTheTickRate()
    {
        GenesisPlan plan = Plan(new Timing(6, 125), 1, Flow(((0, 10), [new SetTempo(150)])));

        (DmfTimingPlan result, _) = Run(plan);

        Assert.Equal(60, result.Timing.CustomHz);
        Assert.Equal(new TickTimes(7, 7), new TickTimes(result.Timing.TickTime1, result.Timing.TickTime2));
        Assert.Equal(new TickTimes(6, 6), result.Changes[0][10]);
        Assert.InRange(DmfSeconds(plan, result) / ((10 * 6 / 50.0) + (54 * 6 / 60.0)), 0.99, 1.01);
    }

    /// <summary>Rows after a stop (<c>F00</c>) never play, so they don't pick the tick rate.</summary>
    [Fact]
    public void RowsAfterAStopDoNotCount()
    {
        GenesisPlan plan = Plan(new Timing(6, 125), 1, Flow(((0, 10), [new StopSong()]), ((0, 11), [new SetTempo(150)])));

        (DmfTimingPlan result, _) = Run(plan);

        Assert.Equal(50, result.Timing.TickRateHz);
        Assert.False(result.Timing.UsingCustomHz);
    }

    /// <summary>
    /// The song loops back to order 0 with a different speed than it started with; the DMF would start the loop in the
    /// speed the song ended in, so row 0 sets the first-time pair again.
    /// </summary>
    [Fact]
    public void TheLoopTargetRestatesTheTickTimes()
    {
        GenesisPlan plan = Plan(new Timing(6, 125), 1, Flow(((0, 10), [new SetSpeed(3)])), loopOrder: 0);

        (DmfTimingPlan result, _) = Run(plan);

        Assert.Equal(new TickTimes(6, 6), result.Changes[0][0]);
        Assert.Equal(new TickTimes(3, 3), result.Changes[0][10]);
    }

    /// <summary>A song that ends where it began needs no restatement at the loop target.</summary>
    [Fact]
    public void ALoopThatEndsInTheStartingTimingAddsNothing()
    {
        GenesisPlan plan = Plan(new Timing(6, 125), 1, Flow(((0, 10), [new SetSpeed(3)]), ((0, 20), [new SetSpeed(6)])), loopOrder: 0);

        (DmfTimingPlan result, _) = Run(plan);

        Assert.Null(result.Changes[0][0]);
    }

    /// <summary>A song with changes that also jumps may take a different way through them than top to bottom, which the notes say.</summary>
    [Fact]
    public void JumpsWithTimingChangesAreNoted()
    {
        GenesisPlan plan = Plan(new Timing(6, 125), 2, Flow(((0, 10), [new SetSpeed(3)]), ((1, 5), [new PositionJump(0)])));

        (_, List<string> notes) = Run(plan);

        Assert.Contains(notes, n => n.Contains("jumps or loops", StringComparison.Ordinal));
    }

    /// <summary>A song that never changes timing says nothing about jumps.</summary>
    [Fact]
    public void JumpsAloneAreNotNoted()
    {
        (_, List<string> notes) = Run(Plan(new Timing(6, 125), 2, Flow(((1, 5), [new PositionJump(0)]))));

        Assert.Empty(notes);
    }

    /// <summary>A plan with no rows still gets the timing of its starting speed and tempo.</summary>
    [Fact]
    public void AnEmptyPlanKeepsTheStartingTiming()
    {
        (DmfTimingPlan result, _) = Run(Plan(new Timing(5, 130), 0));

        Assert.Equal(52, result.Timing.CustomHz);
        Assert.Equal(5, result.Timing.TickTime1);
        Assert.Empty(result.Changes);
    }

    /// <summary>A starting speed or tempo that isn't positive is a bug in whoever built the plan.</summary>
    [Fact]
    public void BadStartingTimingIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(Plan(new Timing(0, 125), 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(Plan(new Timing(6, 0), 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(Plan(new Timing(6, 125), 1), scale: 0));
    }

    /// <summary>
    /// The plan's acceptance bar: the DMF lasts as long as the tick player says the MOD does, within 1 %. Songs are
    /// one pattern with the given <c>Fxx</c> commands at (row, parameter), played once through.
    /// </summary>
    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 0, 0x96 })]
    [InlineData(new[] { 0, 0x7B })]
    [InlineData(new[] { 0, 0x21 })]
    [InlineData(new[] { 0, 0xFF })]
    [InlineData(new[] { 20, 0x03 })]
    [InlineData(new[] { 32, 0x96 })]
    [InlineData(new[] { 16, 0x87 })]
    [InlineData(new[] { 8, 0x96, 24, 0x7D, 40, 0x03, 50, 0xB4 })]
    [InlineData(new[] { 0, 0x28, 10, 0x08, 30, 0x64 })]
    public void TheDmfLastsAsLongAsTheTickPlayersMod(int[] rowsAndParameters)
    {
        ModBuilder builder = new ModBuilder().WithSample(1, "lead", 1024);
        for (int i = 0; i < rowsAndParameters.Length; i += 2)
        {
            builder.WithCell(0, rowsAndParameters[i], 3, 0, 0, 0xF, rowsAndParameters[i + 1]);
        }

        ModModule module = ModReader.Read(builder.Build(), new ConversionNotes(Logger.None));
        Song song = ModToSong.Convert(module, AmigaPitch.PalClockHz, new ConversionNotes(Logger.None));
        GenesisPlan plan = new SimpleMapper().Map(song, MappingOptions.Default, new ConversionNotes(Logger.None));
        Playback playback = ModTickPlayer.Play(module, AmigaPitch.PalClockHz, 1.0, new ConversionNotes(Logger.None));

        DmfTimingPlan timing = DmfTimingPlanner.Plan(plan, 1.0, Region.Pal, new ConversionNotes(Logger.None));

        Assert.Equal(playback.DurationSeconds, DmfSeconds(plan, timing), playback.DurationSeconds * 0.01);
    }
}
