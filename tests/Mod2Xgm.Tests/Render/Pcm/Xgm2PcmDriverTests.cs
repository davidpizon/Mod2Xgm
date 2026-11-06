using Mod2Xgm.Render.Fm;
using Mod2Xgm.Render.Pcm;

namespace Mod2Xgm.Tests.Render.Pcm;

/// <summary>
/// The XGM2 PCM model against what SGDK's <c>drv_xgm2.s80</c> does: the expected values come from reading the driver
/// (docs/adr/0058-pcm-driver-models.md), not from a capture.
/// </summary>
public sealed class Xgm2PcmDriverTests
{
    /// <summary>Timer A at 1020: one byte every 4 YM3438 samples.</summary>
    [Fact]
    public void BeatRateHz_WithChipRate_IsChipRateOver4()
    {
        // Act
        double rate = Xgm2PcmDriver.BeatRateHz(Ym3438.NtscClockHz);

        // Assert
        Assert.Equal(Ym3438.SampleRateHz(Ym3438.NtscClockHz) / 4, rate, 9);
    }

    /// <summary>The beat rate is about 13 317 Hz on NTSC and 13 195 Hz on PAL.</summary>
    [Theory]
    [InlineData(Ym3438.NtscClockHz, 13_317)]
    [InlineData(Ym3438.PalClockHz, 13_195)]
    public void BeatRateHz_WithClock_RoundsToKnownRate(double clockHz, double expected)
    {
        // Act
        double rate = Xgm2PcmDriver.BeatRateHz(clockHz);

        // Assert
        Assert.Equal(expected, rate, 0);
    }

    /// <summary>A fresh driver has three silent channels and the DAC off.</summary>
    [Fact]
    public void Generate_OnFreshDriver_IsSilentWithDacOff()
    {
        // Arrange
        Xgm2PcmDriver sut = new(buffered: false);

        // Act
        DacCapture capture = DacCapture.Of(sut, 3000);

        // Assert
        Assert.All(capture.Data, b => Assert.Equal(0x80, b));
        Assert.All(capture.Enable, b => Assert.Equal(0, b));
        Assert.Equal(3, sut.Channels);
    }

    /// <summary>At full speed a new byte comes on every 4th sample, the first at once.</summary>
    [Fact]
    public void Play_AtFullSpeed_PlaysByteEvery4Samples()
    {
        // Arrange
        Xgm2PcmDriver sut = new(buffered: false);

        // Act
        sut.Play(0, DacCapture.Ramp(512), Xgm2PcmDriver.LowPriority, halfSpeed: false);

        // Assert
        DacCapture capture = DacCapture.Of(sut, 2000);
        Assert.Equal(1, capture.Value(0));
        Assert.Equal(Enumerable.Range(1, 499).Select(k => k * 4), capture.Changes());
    }

    /// <summary>At half speed every byte plays on two beats (6.65 kHz).</summary>
    [Fact]
    public void Play_AtHalfSpeed_PlaysEveryByteTwice()
    {
        // Arrange
        Xgm2PcmDriver sut = new(buffered: false);

        // Act
        sut.Play(1, DacCapture.Ramp(256), Xgm2PcmDriver.LowPriority, halfSpeed: true);

        // Assert
        DacCapture capture = DacCapture.Of(sut, 511 * 4);
        Assert.Equal(Enumerable.Range(1, 255).Select(k => k * 8), capture.Changes());
        Assert.True(sut.IsPlaying(1));
    }

    /// <summary>At half speed a 256-byte sample lasts 512 beats: it ends on beat 511, the last byte's second beat.</summary>
    [Fact]
    public void Generate_AtHalfSpeedLastBeat_EndsSample()
    {
        // Arrange
        Xgm2PcmDriver sut = new(buffered: false);
        sut.Play(1, DacCapture.Ramp(256), Xgm2PcmDriver.LowPriority, halfSpeed: true);
        DacCapture.Of(sut, 511 * 4);

        // Act
        DacCapture.Of(sut, 1);

        // Assert
        Assert.False(sut.IsPlaying(1));
    }

    /// <summary>A half-speed channel mixes with a full-speed one, each at its own pace.</summary>
    [Fact]
    public void Play_AtHalfAndFullSpeed_Mixes()
    {
        // Arrange
        Xgm2PcmDriver sut = new(buffered: false);
        sut.Play(0, [10, 20], Xgm2PcmDriver.LowPriority, halfSpeed: true);

        // Act
        sut.Play(2, [1, 2, 3, 4], Xgm2PcmDriver.LowPriority, halfSpeed: false);

        // Assert
        DacCapture capture = DacCapture.Of(sut, 16);
        Assert.Equal([11, 12, 23, 24], Enumerable.Range(0, 4).Select(k => capture.Value(k * 4)));
    }

    /// <summary>The three channels are added in order, each sum stuck at +127 / −128.</summary>
    [Fact]
    public void Play_WithMixPastRange_SticksInChannelOrder()
    {
        // Arrange
        Xgm2PcmDriver sut = new(buffered: false);
        sut.Play(0, [-100, 100], Xgm2PcmDriver.LowPriority, halfSpeed: false);
        sut.Play(1, [-100, 100], Xgm2PcmDriver.LowPriority, halfSpeed: false);

        // Act
        sut.Play(2, [100, -100], Xgm2PcmDriver.LowPriority, halfSpeed: false);

        // Assert
        DacCapture capture = DacCapture.Of(sut, 5);
        Assert.Equal(-28, capture.Value(0));
        Assert.Equal(27, capture.Value(4));
    }

    /// <summary>A low-priority music play can't take a channel from a high-priority one.</summary>
    [Fact]
    public void Play_WithLowerPriority_LeavesChannelBusy()
    {
        // Arrange
        Xgm2PcmDriver sut = new(buffered: false);
        sut.Play(0, [1], Xgm2PcmDriver.HighPriority, halfSpeed: false);

        // Act
        bool played = sut.Play(0, [2], Xgm2PcmDriver.LowPriority, halfSpeed: false);

        // Assert
        Assert.False(played);
        Assert.Equal(Xgm2PcmDriver.HighPriority, sut.PriorityOf(0));
    }

    /// <summary>A play at the same priority takes the channel.</summary>
    [Fact]
    public void Play_WithSamePriority_TakesChannel()
    {
        // Arrange
        Xgm2PcmDriver sut = new(buffered: false);
        sut.Play(0, [1], Xgm2PcmDriver.HighPriority, halfSpeed: false);

        // Act
        bool played = sut.Play(0, [3], Xgm2PcmDriver.HighPriority, halfSpeed: false);

        // Assert
        Assert.True(played);
        Assert.Equal(3, DacCapture.Of(sut, 1).Value(0));
    }

    /// <summary>A stop always works: the channel goes silent at the next beat, at priority 0.</summary>
    [Fact]
    public void Stop_WithHighPriorityChannel_FreesIt()
    {
        // Arrange
        Xgm2PcmDriver sut = new(buffered: false);
        sut.Play(0, [3], Xgm2PcmDriver.HighPriority, halfSpeed: false);
        DacCapture.Of(sut, 1);

        // Act
        sut.Stop(0);

        // Assert
        Assert.False(sut.IsPlaying(0));
        Assert.Equal(0, sut.PriorityOf(0));
        Assert.All(DacCapture.Of(sut, 8).Data.Skip(3), b => Assert.Equal(0x80, b));
    }

    /// <summary>After a stop, even a low-priority play gets the channel.</summary>
    [Fact]
    public void Play_AfterStop_TakesChannelAtLowPriority()
    {
        // Arrange
        Xgm2PcmDriver sut = new(buffered: false);
        sut.Play(0, [3], Xgm2PcmDriver.HighPriority, halfSpeed: false);
        sut.Stop(0);

        // Act
        bool played = sut.Play(0, [4], Xgm2PcmDriver.LowPriority, halfSpeed: false);

        // Assert
        Assert.True(played);
    }

    /// <summary>
    /// The DAC turns on with the play and, checked after every 64 beats, goes off at the end of the first block in which
    /// nothing plays, as SGDK's driver was measured to (docs/adr/0066-pcm-drivers-checked-against-sgdk.md): for a
    /// 256-byte sample, which ends with block 3, at its last beat (255).
    /// </summary>
    [Fact]
    public void Generate_AfterLastSound_TurnsDacOff()
    {
        // Arrange
        Xgm2PcmDriver sut = new(buffered: false);
        sut.Play(2, DacCapture.Ramp(256), Xgm2PcmDriver.LowPriority, halfSpeed: false);

        // Act
        DacCapture capture = DacCapture.Of(sut, 3000);

        // Assert
        Assert.Equal(0x80, capture.Enable[0]);
        Assert.Equal(255 * 4, capture.FirstOff());
        Assert.All(capture.Enable[(255 * 4)..], b => Assert.Equal(0, b));
    }

    /// <summary>A play turns the DAC back on at once after it went off.</summary>
    [Fact]
    public void Play_AfterDacOff_TurnsItBackOn()
    {
        // Arrange
        Xgm2PcmDriver sut = new(buffered: false);
        sut.Play(0, [1], Xgm2PcmDriver.LowPriority, halfSpeed: false);
        DacCapture.Of(sut, 3000);

        // Act
        sut.Play(0, [1], Xgm2PcmDriver.LowPriority, halfSpeed: false);

        // Assert
        Assert.Equal(0x80, DacCapture.Of(sut, 1).Enable[0]);
    }

    /// <summary>Bad channels, priorities and samples are refused.</summary>
    [Fact]
    public void Play_WithBadArguments_Throws()
    {
        // Act
        Xgm2PcmDriver sut = new(buffered: false);

        // Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => sut.Play(3, [1], 0, halfSpeed: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => sut.Play(0, [1], 16, halfSpeed: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => sut.Stop(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => sut.IsPlaying(-1));
        Assert.Throws<ArgumentException>(() => sut.Play(0, [], 0, halfSpeed: true));
        Assert.Throws<ArgumentException>(() => sut.Generate(new byte[1], new byte[2]));
    }

    /// <summary>
    /// Buffered (the default), a play is heard <see cref="Xgm2PcmDriver.StartDelayBeats"/> beats after it is sent, and its
    /// first <see cref="Xgm2PcmDriver.RepeatedBeats"/> beats play twice, as SGDK's driver plays every music play.
    /// </summary>
    [Fact]
    public void Play_WithBuffering_IsHeardLaterWithStartTwice()
    {
        // Arrange
        Xgm2PcmDriver sut = new();

        // Act
        sut.Play(0, DacCapture.Ramp(300), Xgm2PcmDriver.HighPriority, halfSpeed: false);

        // Assert
        DacCapture capture = DacCapture.Of(sut, 4 * 1000);
        int start = Xgm2PcmDriver.StartDelayBeats * 4;
        Assert.All(capture.Data[..start], b => Assert.Equal(0x80, b));
        int[] first = [.. Enumerable.Range(0, 64).Select(i => capture.Value(start + (i * 4)))];
        int[] again = [.. Enumerable.Range(64, 64).Select(i => capture.Value(start + (i * 4)))];
        Assert.Equal([.. DacCapture.Ramp(64).Select(v => (int)v)], first);
        Assert.Equal(first, again);
        Assert.Equal(DacCapture.Ramp(65)[64], capture.Value(start + (128 * 4)));
    }

    /// <summary>At half speed the driver's first 64-beat fill is 32 bytes of the sample, played twice each, and then again.</summary>
    [Fact]
    public void Play_WithBufferingAtHalfSpeed_RepeatsFirst32Bytes()
    {
        // Arrange
        Xgm2PcmDriver sut = new();

        // Act
        sut.Play(0, DacCapture.Ramp(300), Xgm2PcmDriver.HighPriority, halfSpeed: true);

        // Assert
        DacCapture capture = DacCapture.Of(sut, 4 * 1000);
        int start = Xgm2PcmDriver.StartDelayBeats * 4;
        Assert.Equal(capture.Value(start), capture.Value(start + (64 * 4)));
        Assert.Equal(capture.Value(start + (62 * 4)), capture.Value(start + (126 * 4)));
        Assert.Equal(DacCapture.Ramp(33)[32], capture.Value(start + (128 * 4)));
    }

    /// <summary>Buffered, the DAC goes on <see cref="Xgm2PcmDriver.DacLeadBeats"/> beats before the sound is heard, not at the play.</summary>
    [Fact]
    public void Play_WithBuffering_TurnsDacOnAhead()
    {
        // Arrange
        Xgm2PcmDriver sut = new();

        // Act
        sut.Play(0, DacCapture.Ramp(300), Xgm2PcmDriver.HighPriority, halfSpeed: false);

        // Assert
        DacCapture capture = DacCapture.Of(sut, 4 * 1000);
        int on = Array.IndexOf(capture.Enable, (byte)0x80);
        Assert.Equal((Xgm2PcmDriver.StartDelayBeats - Xgm2PcmDriver.DacLeadBeats) * 4, on);
    }
}
