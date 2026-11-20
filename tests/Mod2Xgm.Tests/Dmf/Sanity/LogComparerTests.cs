namespace Mod2Xgm.Tests.Dmf.Sanity;

/// <summary>Tests for <see cref="LogComparer"/>: the writes and state checks, and moving PCM to the frame it is heard on.</summary>
public sealed class LogComparerTests
{
    /// <summary>A three-frame log: FM1 keyed on at frame 0 at one frequency, let go at frame 2.</summary>
    private static ChipLog Song(int frequencyLow) => new(
        [
            [ChipWrite.Fm(0, 0xA4, 0x22), ChipWrite.Fm(0, 0xA0, frequencyLow), ChipWrite.Fm(0, 0x28, 0xF0)],
            [],
            [ChipWrite.Fm(0, 0x28, 0x00)],
        ],
        LoopFrame: 0,
        FrameRateHz: 60);

    /// <summary>A log compared with itself is the same.</summary>
    [Fact]
    public void CompareState_ForSameLog_IsSame()
    {
        // Act
        LogComparison sut = LogComparer.CompareState(Song(0x69), Song(0x69));

        // Assert
        Assert.True(sut.Same);
    }

    /// <summary>A register value that differs from frame 0 on counts in every frame it stays different.</summary>
    [Fact]
    public void CompareState_ForOtherFrequency_CountsEveryFrame()
    {
        // Act
        LogComparison sut = LogComparer.CompareState(Song(0x69), Song(0x6A));

        // Assert
        Assert.Equal(3, sut.FramesDiffering);
        Assert.Equal(0, sut.FirstDifference);
        Assert.Equal([new CategoryCount("FM frequency", 3, 3)], sut.Categories);
    }

    /// <summary>The writes check counts only the frame where the write differs.</summary>
    [Fact]
    public void CompareWrites_ForOtherFrequency_CountsTheWriteFrame()
    {
        // Act
        LogComparison sut = LogComparer.CompareWrites(Song(0x69), Song(0x6A));

        // Assert
        Assert.Equal(1, sut.FramesDiffering);
        Assert.Equal(new FrameDifference(0, "FM0:A0", "69", "6A"), Assert.Single(sut.Examples));
    }

    /// <summary>A candidate that is too short differs where it has no frames, and its length is reported.</summary>
    [Fact]
    public void CompareWrites_ForShorterCandidate_ReportsMissingFrame()
    {
        // Arrange
        ChipLog shorter = Song(0x69) with { Frames = [.. Song(0x69).Frames.Take(2)] };

        // Act
        LogComparison sut = LogComparer.CompareWrites(Song(0x69), shorter);

        // Assert
        Assert.Equal(2, sut.FirstDifference);
        Assert.Equal(2, sut.CandidateFrames);
        Assert.False(sut.Same);
    }

    /// <summary>PCM plays move later by the lead; FM writes stay where they are.</summary>
    [Fact]
    public void ShiftPcm_ForPlayOnFrameZero_MovesItByTheLead()
    {
        // Arrange
        ChipWrite play = new(ChipTarget.PcmPlay, Channel: 0, Sample: 3);
        ChipLog log = new([[play, ChipWrite.Fm(0, 0x28, 0xF0)], [], [], []], null, 60);

        // Act
        ChipLog sut = LogComparer.ShiftPcm(log, 2);

        // Assert
        Assert.Equal([ChipWrite.Fm(0, 0x28, 0xF0)], sut.Frames[0]);
        Assert.Equal([play], sut.Frames[2]);
        Assert.Equal(4, sut.Frames.Count);
    }
}
