using Mod2Xgm.Render;

namespace Mod2Xgm.Tests.Render;

/// <summary>
/// Tests for <see cref="PanSwaps"/> (plan T9.4r, docs/adr/0108-pan-swap-count.md): a swap is a change of the side an
/// instrument is heard on while its MOD side stays the same, however long the silence between.
/// </summary>
public sealed class PanSwapsTests
{
    /// <summary>One tick: 20 ms.</summary>
    private const double TickSeconds = 0.02;

    /// <summary>A log of sample 0 on song channel 0, one entry per tick from <paramref name="sides"/>: (tick, original, heard, cause).</summary>
    private static PanLog LogOf(params (int Tick, int Original, int Heard, PanCause Cause)[] sides) => LogOf(0, sides);

    /// <summary>A log of sample 0 on song channel <paramref name="source"/>, one entry per tick from <paramref name="sides"/>.</summary>
    private static PanLog LogOf(int source, params (int Tick, int Original, int Heard, PanCause Cause)[] sides)
    {
        PanLog log = new();
        foreach ((int tick, int original, int heard, PanCause cause) in sides)
        {
            log.Add(new PanTick(tick, tick * TickSeconds, TickSeconds, 0, source, original, heard, cause, 0.5));
        }

        return log;
    }

    /// <summary>A left drum heard centred for two ticks makes two swaps: away to both speakers, then back to the left.</summary>
    [Fact]
    public void Count_WhenLeftNoteIsCentredForAWhile_FindsAwayAndBack()
    {
        // Arrange
        PanLog log = LogOf((0, -1, -1, PanCause.None), (1, -1, 0, PanCause.SharedDac), (2, -1, 0, PanCause.SharedDac), (3, -1, -1, PanCause.None));

        // Act
        PanSwapCount sut = PanSwaps.Count(log);

        // Assert
        Assert.Equal(
            [
                new PanSwap(0, 0.02, -1, -1, 0, PanCause.SharedDac, 0.04, 0, 0.5, false),
                new PanSwap(0, 0.06, -1, 0, -1, PanCause.SharedDac, 0.02, 0, 0.5, false),
            ],
            sut.Swaps.Select(s => s with { Seconds = Math.Round(s.Seconds, 9), LastsSeconds = Math.Round(s.LastsSeconds, 9) }));
        Assert.Equal([PanSwapKind.Away, PanSwapKind.Back], sut.Swaps.Select(s => s.Kind));
        Assert.Equal(1, sut.Swaps[0].Distance);
    }

    /// <summary>A silence between the two sides doesn't hide the swap, and its length is the gap (David).</summary>
    [Fact]
    public void Count_WhenSilenceSeparatesTheSides_StillFindsTheSwapWithItsGap()
    {
        // Arrange
        PanLog log = LogOf((0, -1, -1, PanCause.None), (100, -1, 0, PanCause.PreMix));

        // Act
        PanSwap sut = Assert.Single(PanSwaps.Count(log).Swaps);

        // Assert
        Assert.Equal((PanCause.PreMix, 1.98), (sut.Cause, Math.Round(sut.GapSeconds, 9)));
    }

    /// <summary>When the MOD itself moves the instrument to the other side, the Genesis following it is no swap.</summary>
    [Fact]
    public void Count_WhenTheModChangesSide_FindsNoSwap()
    {
        // Arrange
        PanLog log = LogOf((0, -1, -1, PanCause.None), (1, 1, 1, PanCause.None), (2, 1, 0, PanCause.Psg), (3, -1, 0, PanCause.Psg));

        // Act
        PanSwapCount sut = PanSwaps.Count(log);

        // Assert
        PanSwap swap = Assert.Single(sut.Swaps);
        Assert.Equal((0.04, 1, 0), (Math.Round(swap.Seconds, 9), swap.From, swap.To));
    }

    /// <summary>An instrument centred the whole song never swaps; it is listed as steadily off its side.</summary>
    [Fact]
    public void Count_WhenAlwaysCentred_ReportsItSteadilyOffSide()
    {
        // Arrange
        PanLog log = LogOf((0, -1, 0, PanCause.PreMix), (1, -1, 0, PanCause.PreMix));

        // Act
        PanSwapCount sut = PanSwaps.Count(log);

        // Assert
        Assert.Empty(sut.Swaps);
        PanSampleSummary sample = Assert.Single(sut.Samples);
        Assert.True(sample.SteadilyOffSide);
        Assert.Equal(sample.SoundingSeconds, sample.OffSideSeconds, 9);
    }

    /// <summary>From both speakers to the far side is an "across" swap of one step; left to right is two steps.</summary>
    [Fact]
    public void Count_WhenItCrossesToTheFarSide_ReportsKindAndDistance()
    {
        // Arrange
        PanLog log = LogOf((0, -1, 0, PanCause.SharedDac), (1, -1, 1, PanCause.SharedDac), (2, -1, -1, PanCause.None));

        // Act
        PanSwapCount sut = PanSwaps.Count(log);

        // Assert
        Assert.Equal([(PanSwapKind.Across, 1), (PanSwapKind.Back, 2)], sut.Swaps.Select(s => (s.Kind, s.Distance)));
    }

    /// <summary>A swap where the note after it comes from another MOD channel on the same side says so.</summary>
    [Fact]
    public void Count_WhenTheNoteMovesToAnotherChannelOnTheSameSide_MarksTheChannelChange()
    {
        // Arrange
        PanLog log = LogOf(0, (0, -1, -1, PanCause.None));
        foreach (PanTick tick in LogOf(3, (1, -1, 0, PanCause.SharedDac)).Ticks)
        {
            log.Add(tick);
        }

        // Act
        PanSwap sut = Assert.Single(PanSwaps.Count(log).Swaps);

        // Assert
        Assert.True(sut.ChannelChanged);
    }

    /// <summary>On a tick where the instrument plays on both MOD sides at once, the chain breaks: no swap across it.</summary>
    [Fact]
    public void Count_WhenNotesOfOneTickHaveDifferentModSides_BreaksTheChain()
    {
        // Arrange
        PanLog log = LogOf((0, -1, -1, PanCause.None));
        log.Add(new PanTick(1, 0.02, TickSeconds, 0, 0, -1, -1, PanCause.None, 0.5));
        log.Add(new PanTick(1, 0.02, TickSeconds, 0, 1, 1, 1, PanCause.None, 0.9));
        log.Add(new PanTick(2, 0.04, TickSeconds, 0, 0, -1, 0, PanCause.SharedDac, 0.5));

        // Act
        PanSwapCount sut = PanSwaps.Count(log);

        // Assert
        Assert.Empty(sut.Swaps);
    }

    /// <summary>Each sample is counted on its own, in sample order.</summary>
    [Fact]
    public void Count_WithTwoSamples_SummarisesEach()
    {
        // Arrange
        PanLog log = new();
        log.Add(new PanTick(0, 0, TickSeconds, 4, 0, 1, 1, PanCause.None, 1));
        log.Add(new PanTick(0, 0, TickSeconds, 1, 1, -1, -1, PanCause.None, 1));
        log.Add(new PanTick(1, 0.02, TickSeconds, 4, 0, 1, 0, PanCause.CentredBass, 1));

        // Act
        PanSwapCount sut = PanSwaps.Count(log);

        // Assert
        Assert.Equal([(1, 0), (4, 1)], sut.Samples.Select(s => (s.Sample, s.Swaps)));
    }
}
