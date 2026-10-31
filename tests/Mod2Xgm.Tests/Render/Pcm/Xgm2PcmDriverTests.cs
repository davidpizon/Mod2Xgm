using Mod2Xgm.Render.Fm;
using Mod2Xgm.Render.Pcm;

namespace Mod2Xgm.Tests.Render.Pcm;

/// <summary>
/// The XGM2 PCM model against what SGDK's <c>drv_xgm2.s80</c> does: the expected values come from reading the driver
/// (docs/adr/0058-pcm-driver-models.md), not from a capture.
/// </summary>
public sealed class Xgm2PcmDriverTests
{
    /// <summary>Timer A at 1020: one byte every 4 YM3438 samples, about 13 317 Hz on NTSC and 13 195 Hz on PAL.</summary>
    [Fact]
    public void BeatRateIsTheChipsRateOver4()
    {
        Assert.Equal(Ym3438.SampleRateHz(Ym3438.NtscClockHz) / 4, Xgm2PcmDriver.BeatRateHz(Ym3438.NtscClockHz), 9);
        Assert.Equal(13_317, Xgm2PcmDriver.BeatRateHz(Ym3438.NtscClockHz), 0);
        Assert.Equal(13_195, Xgm2PcmDriver.BeatRateHz(Ym3438.PalClockHz), 0);
    }

    /// <summary>A fresh driver has three silent channels and the DAC off.</summary>
    [Fact]
    public void AFreshDriverIsSilentWithTheDacOff()
    {
        Xgm2PcmDriver driver = new();
        DacCapture capture = DacCapture.Of(driver, 3000);
        Assert.All(capture.Data, b => Assert.Equal(0x80, b));
        Assert.All(capture.Enable, b => Assert.Equal(0, b));
        Assert.Equal(3, driver.Channels);
    }

    /// <summary>At full speed a new byte comes on every 4th sample, the first at once.</summary>
    [Fact]
    public void FullSpeedPlaysAByteEvery4Samples()
    {
        Xgm2PcmDriver driver = new();
        driver.Play(0, DacCapture.Ramp(512), Xgm2PcmDriver.LowPriority, halfSpeed: false);
        DacCapture capture = DacCapture.Of(driver, 2000);
        Assert.Equal(1, capture.Value(0));
        Assert.Equal(Enumerable.Range(1, 499).Select(k => k * 4), capture.Changes());
    }

    /// <summary>At half speed every byte plays on two beats (6.65 kHz), so a 256-byte sample lasts 512 beats.</summary>
    [Fact]
    public void HalfSpeedPlaysEveryByteTwice()
    {
        Xgm2PcmDriver driver = new();
        driver.Play(1, DacCapture.Ramp(256), Xgm2PcmDriver.LowPriority, halfSpeed: true);
        DacCapture capture = DacCapture.Of(driver, 511 * 4);
        Assert.Equal(Enumerable.Range(1, 255).Select(k => k * 8), capture.Changes());
        Assert.True(driver.IsPlaying(1));
        DacCapture.Of(driver, 1); // beat 511: the last byte's second beat
        Assert.False(driver.IsPlaying(1));
    }

    /// <summary>A half-speed channel mixes with a full-speed one, each at its own pace.</summary>
    [Fact]
    public void HalfAndFullSpeedMix()
    {
        Xgm2PcmDriver driver = new();
        driver.Play(0, [10, 20], Xgm2PcmDriver.LowPriority, halfSpeed: true);
        driver.Play(2, [1, 2, 3, 4], Xgm2PcmDriver.LowPriority, halfSpeed: false);
        DacCapture capture = DacCapture.Of(driver, 16);
        Assert.Equal([11, 12, 23, 24], Enumerable.Range(0, 4).Select(k => capture.Value(k * 4)));
    }

    /// <summary>The three channels are added in order, each sum stuck at +127 / −128.</summary>
    [Fact]
    public void MixingSticksAtTheEndsInChannelOrder()
    {
        Xgm2PcmDriver driver = new();
        driver.Play(0, [-100, 100], Xgm2PcmDriver.LowPriority, halfSpeed: false);
        driver.Play(1, [-100, 100], Xgm2PcmDriver.LowPriority, halfSpeed: false);
        driver.Play(2, [100, -100], Xgm2PcmDriver.LowPriority, halfSpeed: false);
        DacCapture capture = DacCapture.Of(driver, 5);
        Assert.Equal(-28, capture.Value(0));
        Assert.Equal(27, capture.Value(4));
    }

    /// <summary>A low-priority music play can't take a channel from a high-priority one; a stop always works.</summary>
    [Fact]
    public void PriorityDecidesWhoGetsTheChannel()
    {
        Xgm2PcmDriver driver = new();
        Assert.True(driver.Play(0, [1], Xgm2PcmDriver.HighPriority, halfSpeed: false));
        Assert.False(driver.Play(0, [2], Xgm2PcmDriver.LowPriority, halfSpeed: false));
        Assert.Equal(Xgm2PcmDriver.HighPriority, driver.PriorityOf(0));
        Assert.True(driver.Play(0, [3], Xgm2PcmDriver.HighPriority, halfSpeed: false));
        Assert.Equal(3, DacCapture.Of(driver, 1).Value(0));

        driver.Stop(0);
        Assert.False(driver.IsPlaying(0));
        Assert.Equal(0, driver.PriorityOf(0));
        Assert.All(DacCapture.Of(driver, 8).Data.Skip(3), b => Assert.Equal(0x80, b));
        Assert.True(driver.Play(0, [4], Xgm2PcmDriver.LowPriority, halfSpeed: false));
    }

    /// <summary>
    /// The DAC turns on with the play and, checked after every 64 beats, stays on for three blocks after the last block
    /// with sound: off from beat 511 for a 256-byte sample (blocks 0–3 sound, 4–6 hold).
    /// </summary>
    [Fact]
    public void TheDacStaysOnThreeBlocksAfterTheLastSound()
    {
        Xgm2PcmDriver driver = new();
        driver.Play(2, DacCapture.Ramp(256), Xgm2PcmDriver.LowPriority, halfSpeed: false);
        DacCapture capture = DacCapture.Of(driver, 3000);
        Assert.Equal(0x80, capture.Enable[0]);
        Assert.Equal(511 * 4, capture.FirstOff());
        Assert.All(capture.Enable[(511 * 4)..], b => Assert.Equal(0, b));
    }

    /// <summary>A play turns the DAC back on at once after it went off.</summary>
    [Fact]
    public void APlayTurnsTheDacBackOn()
    {
        Xgm2PcmDriver driver = new();
        driver.Play(0, [1], Xgm2PcmDriver.LowPriority, halfSpeed: false);
        DacCapture.Of(driver, 3000);
        driver.Play(0, [1], Xgm2PcmDriver.LowPriority, halfSpeed: false);
        Assert.Equal(0x80, DacCapture.Of(driver, 1).Enable[0]);
    }

    /// <summary>Bad channels, priorities and samples are refused.</summary>
    [Fact]
    public void BadArgumentsAreRefused()
    {
        Xgm2PcmDriver driver = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.Play(3, [1], 0, halfSpeed: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.Play(0, [1], 16, halfSpeed: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.Stop(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.IsPlaying(-1));
        Assert.Throws<ArgumentException>(() => driver.Play(0, [], 0, halfSpeed: true));
        Assert.Throws<ArgumentException>(() => driver.Generate(new byte[1], new byte[2]));
    }
}
