using Mod2Xgm.Render.Fm;
using Mod2Xgm.Render.Pcm;

namespace Mod2Xgm.Tests.Render.Pcm;

/// <summary>
/// The XGM (v1) PCM model against what SGDK's <c>drv_xgm.s80</c> does: the expected values come from reading the driver
/// (docs/adr/0058-pcm-driver-models.md), not from a capture.
/// </summary>
public sealed class XgmPcmDriverTests
{
    /// <summary>The YM3438 sample beat <paramref name="k"/> lands on: beats are 3810 master clocks apart, samples 1008, the first on sample 0.</summary>
    private static int BeatSample(int k) => (int)Math.Ceiling(k * 3810 / 1008.0);

    /// <summary>One byte every 254 Z80 cycles: about 14 093 Hz on NTSC and 13 964 Hz on PAL.</summary>
    [Fact]
    public void BeatRateIsTheZ80ClockOver254()
    {
        Assert.Equal(7_670_453 * 7 / 15.0 / 254, XgmPcmDriver.BeatRateHz(Ym3438.NtscClockHz), 9);
        Assert.Equal(14_093, XgmPcmDriver.BeatRateHz(Ym3438.NtscClockHz), 0);
        Assert.Equal(13_964, XgmPcmDriver.BeatRateHz(Ym3438.PalClockHz), 0);
    }

    /// <summary>A fresh driver sends 0 (<c>$80</c>) with the DAC off, on every channel's behalf.</summary>
    [Fact]
    public void AFreshDriverIsSilentWithTheDacOff()
    {
        XgmPcmDriver driver = new(buffered: false);
        DacCapture capture = DacCapture.Of(driver, 5000);
        Assert.All(capture.Data, b => Assert.Equal(0x80, b));
        Assert.All(capture.Enable, b => Assert.Equal(0, b));
        Assert.Equal(4, driver.Channels);
        Assert.All(Enumerable.Range(0, 4), ch => Assert.False(driver.IsPlaying(ch)));
    }

    /// <summary>A new byte every 3 or 4 YM3438 samples, exactly 1008 of them in 3810 samples, the first at once.</summary>
    [Fact]
    public void BeatsAre3810MasterClocksApart()
    {
        XgmPcmDriver driver = new(buffered: false);
        driver.Play(0, DacCapture.Ramp(2048), 0);
        DacCapture capture = DacCapture.Of(driver, 3810);
        Assert.Equal(1, capture.Value(0));
        List<int> changes = capture.Changes();
        Assert.Equal(1007, changes.Count);
        Assert.Equal(Enumerable.Range(1, 1007).Select(BeatSample), changes);
        Assert.All(changes.Zip(changes.Skip(1), (a, b) => b - a), gap => Assert.InRange(gap, 3, 4));
    }

    /// <summary>A play lands on the first beat after it: the samples before keep the old byte.</summary>
    [Fact]
    public void APlayStartsOnTheNextBeat()
    {
        XgmPcmDriver driver = new(buffered: false);
        DacCapture.Of(driver, 2); // beat 0 at sample 0; beat 1 at sample 4
        driver.Play(0, [50], 0);
        DacCapture capture = DacCapture.Of(driver, 3);
        Assert.Equal([0x80, 0x80, 0x80 + 50], capture.Data);
    }

    /// <summary>
    /// The channels are added in order, each sum stuck at +127 / −128: 100 + 100 − 100 is 27, not 100, and −100 − 100
    /// is −128.
    /// </summary>
    [Fact]
    public void MixingSticksAtTheEndsInChannelOrder()
    {
        XgmPcmDriver driver = new(buffered: false);
        driver.Play(0, [100], 0);
        driver.Play(1, [100], 0);
        driver.Play(2, [-100], 0);
        Assert.Equal(27, DacCapture.Of(driver, 1).Value(0));

        XgmPcmDriver low = new(buffered: false);
        low.Play(0, [-100], 0);
        low.Play(3, [-100], 0);
        Assert.Equal(-128, DacCapture.Of(low, 1).Value(0));

        XgmPcmDriver high = new(buffered: false);
        high.Play(1, [127], 0);
        high.Play(2, [127], 0);
        high.Play(3, [-1], 0);
        Assert.Equal(126, DacCapture.Of(high, 1).Value(0));
    }

    /// <summary>A sample plays to the end of its 256-byte padding (silence), then the channel is free at priority 0.</summary>
    [Fact]
    public void ASamplePlaysToTheEndOfItsPadding()
    {
        XgmPcmDriver driver = new(buffered: false);
        driver.Play(2, [10, 20, 30], 8);
        DacCapture first = DacCapture.Of(driver, BeatSample(255));
        Assert.Equal(30, first.Value(BeatSample(2)));
        Assert.Equal(0, first.Value(BeatSample(3)));
        Assert.True(driver.IsPlaying(2));
        Assert.Equal(8, driver.PriorityOf(2));

        DacCapture.Of(driver, 1); // beat 255, the padding's last byte
        Assert.False(driver.IsPlaying(2));
        Assert.Equal(0, driver.PriorityOf(2));
    }

    /// <summary>A play or stop replaces the sound on its channel only at the same or a higher priority.</summary>
    [Fact]
    public void PriorityDecidesWhoGetsTheChannel()
    {
        XgmPcmDriver driver = new(buffered: false);
        Assert.True(driver.Play(0, [1], 8));
        Assert.False(driver.Play(0, [2], 4));
        Assert.False(driver.Stop(0, 4));
        Assert.Equal(1, DacCapture.Of(driver, 1).Value(0));

        Assert.True(driver.Play(0, [3], 8));
        Assert.Equal(3, DacCapture.Of(driver, 4).Value(3)); // beat 1, on sample 4
        Assert.True(driver.Stop(0, 12));
        Assert.False(driver.IsPlaying(0));
        Assert.Equal(0, driver.PriorityOf(0));
        Assert.True(driver.Play(0, [4], 0));
    }

    /// <summary>A stop silences the channel from the next beat; the DAC holds its byte until then.</summary>
    [Fact]
    public void AStopSilencesTheChannel()
    {
        XgmPcmDriver driver = new(buffered: false);
        driver.Play(1, [-5, -5, -5, -5], 0);
        Assert.Equal(-5, DacCapture.Of(driver, 1).Value(0));
        driver.Stop(1, 0);
        DacCapture capture = DacCapture.Of(driver, 20);
        Assert.All(capture.Data[..3], b => Assert.Equal(0x80 - 5, b)); // held until beat 1, on sample 4
        Assert.All(capture.Data[3..], b => Assert.Equal(0x80, b));
    }

    /// <summary>
    /// The DAC turns on with the play and, checked after every 256 beats, stays on for three blocks after the block the
    /// sound ended in: off from the 1024th beat for a 256-byte sample.
    /// </summary>
    [Fact]
    public void TheDacStaysOnThreeBlocksAfterTheLastSound()
    {
        XgmPcmDriver driver = new(buffered: false);
        driver.Play(0, DacCapture.Ramp(256), 0);
        DacCapture capture = DacCapture.Of(driver, BeatSample(1100));
        Assert.Equal(0x80, capture.Enable[0]);
        Assert.Equal(BeatSample(1023), capture.FirstOff());
        Assert.All(capture.Enable[BeatSample(1023)..], b => Assert.Equal(0, b));
    }

    /// <summary>While a sound plays the DAC stays on, however long it is.</summary>
    [Fact]
    public void TheDacStaysOnWhileASoundPlays()
    {
        XgmPcmDriver driver = new(buffered: false);
        driver.Play(3, DacCapture.Ramp(2560), 0);
        DacCapture capture = DacCapture.Of(driver, BeatSample(2560));
        Assert.Equal(-1, capture.FirstOff());
    }

    /// <summary>Bad channels, priorities and samples are refused, and both registers need the same length.</summary>
    [Fact]
    public void BadArgumentsAreRefused()
    {
        XgmPcmDriver driver = new(buffered: false);
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.Play(4, [1], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.Play(-1, [1], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.Play(0, [1], 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.Play(0, [1], -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.Stop(0, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.Stop(0, -1));
        Assert.Throws<ArgumentException>(() => driver.Play(0, [], 0));
        Assert.Throws<ArgumentNullException>(() => driver.Play(0, null!, 0)); // null on purpose, past the annotation
        Assert.Throws<ArgumentException>(() => driver.Generate(new byte[2], new byte[1]));
        driver.Generate([], []);
    }

    /// <summary>The bytes drive the YM3438's DAC: channel 6's output is the byte minus <c>$80</c>, as a 9-bit value.</summary>
    [Fact]
    public void TheBytesPlayThroughTheYm3438()
    {
        XgmPcmDriver driver = new(buffered: false);
        driver.Play(0, [-128, -64, 0, 64, 127], 0);
        DacCapture capture = DacCapture.Of(driver, BeatSample(5));

        Ym3438 chip = new();
        chip.WriteRegister(1, 0xb6, 0xc0); // channel 6 to both speakers
        int[] left = new int[1];
        int[] right = new int[1];
        for (int i = 0; i < capture.Data.Length; i++)
        {
            chip.WriteRegister(0, 0x2b, capture.Enable[i]);
            chip.WriteRegister(0, 0x2a, capture.Data[i]);
            chip.Generate(left, right);
            int expected = capture.Value(i) * 2 * 128 / 6;
            Assert.Equal(expected, left[0]);
            Assert.Equal(expected, right[0]);
        }
    }

    /// <summary>
    /// Buffered (the default), a play is heard from the first 256-beat block that starts at least
    /// <see cref="XgmPcmDriver.StartLeadBeats"/> beats later, as SGDK's driver (with xgmtool's 3-frame shift) plays it:
    /// sent on beat 0, heard from beat 256; sent on beat 200, heard from beat 512.
    /// </summary>
    [Theory]
    [InlineData(0, 256)]
    [InlineData(180, 256)]
    [InlineData(200, 512)]
    public void ABufferedPlayIsHeardFromTheFirstBlockAfterTheLead(int sentOnBeat, int heardFromBeat)
    {
        XgmPcmDriver driver = new();
        Assert.True(driver.Buffered);
        Span<byte> one = stackalloc byte[1];
        Span<byte> enable = stackalloc byte[1];
        while (driver.BeatsSent < sentOnBeat)
        {
            driver.Generate(one, enable);
        }

        driver.Play(0, [5, 6, 7], 0);
        while (driver.BeatsSent < heardFromBeat)
        {
            driver.Generate(one, enable);
            Assert.Equal(0x80, one[0]);
        }

        while (driver.BeatsSent == heardFromBeat)
        {
            driver.Generate(one, enable);
        }

        Assert.Equal(0x80 + 5, one[0]);
    }

    /// <summary>A buffered play or stop refuses bad arguments when it is sent, not when it is heard.</summary>
    [Fact]
    public void ABufferedCommandChecksItsArgumentsAtOnce()
    {
        XgmPcmDriver driver = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.Play(4, [1], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.Stop(0, 16));
        Assert.Throws<ArgumentException>(() => driver.Play(0, [], 0));
    }

    /// <summary>
    /// Every add that clips costs 11 Z80 cycles and delays the beats after it (SGDK's driver, measured): four loud
    /// channels that clip on every beat send 3 × 165 master clocks more per beat, 13 % fewer beats than quiet ones.
    /// </summary>
    [Fact]
    public void ClippingSlowsTheBeat()
    {
        const int samples = 100_000;
        sbyte[] loud = [.. Enumerable.Repeat((sbyte)100, 32_768)];
        sbyte[] quiet = [.. Enumerable.Repeat((sbyte)10, 32_768)];
        long Beats(sbyte[] sample)
        {
            XgmPcmDriver driver = new(buffered: false);
            for (int c = 0; c < 4; c++)
            {
                driver.Play(c, sample, 0);
            }

            DacCapture.Of(driver, samples);
            return driver.BeatsSent;
        }

        long quietBeats = Beats(quiet);
        long loudBeats = Beats(loud);
        Assert.Equal(samples * 1008.0 / 3810, quietBeats, 1.0);
        Assert.Equal(samples * 1008.0 / (3810 + (3 * 165)), loudBeats, 2.0);
    }
}
