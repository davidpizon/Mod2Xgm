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

    /// <summary>One byte every 254 Z80 cycles.</summary>
    [Fact]
    public void BeatRateHz_WithZ80Clock_IsClockOver254()
    {
        // Act
        double rate = XgmPcmDriver.BeatRateHz(Ym3438.NtscClockHz);

        // Assert
        Assert.Equal(7_670_453 * 7 / 15.0 / 254, rate, 9);
    }

    /// <summary>The beat rate is about 14 093 Hz on NTSC and 13 964 Hz on PAL.</summary>
    [Theory]
    [InlineData(Ym3438.NtscClockHz, 14_093)]
    [InlineData(Ym3438.PalClockHz, 13_964)]
    public void BeatRateHz_WithClock_RoundsToKnownRate(double clockHz, double expected)
    {
        // Act
        double rate = XgmPcmDriver.BeatRateHz(clockHz);

        // Assert
        Assert.Equal(expected, rate, 0);
    }

    /// <summary>A fresh driver sends 0 (<c>$80</c>) with the DAC off, on every channel's behalf.</summary>
    [Fact]
    public void Generate_OnFreshDriver_IsSilentWithDacOff()
    {
        // Arrange
        XgmPcmDriver sut = new(buffered: false);

        // Act
        DacCapture capture = DacCapture.Of(sut, 5000);

        // Assert
        Assert.All(capture.Data, b => Assert.Equal(0x80, b));
        Assert.All(capture.Enable, b => Assert.Equal(0, b));
        Assert.Equal(4, sut.Channels);
        Assert.All(Enumerable.Range(0, 4), ch => Assert.False(sut.IsPlaying(ch)));
    }

    /// <summary>A new byte every 3 or 4 YM3438 samples, exactly 1008 of them in 3810 samples, the first at once.</summary>
    [Fact]
    public void Generate_ForBeats_Are3810MasterClocksApart()
    {
        // Arrange
        XgmPcmDriver sut = new(buffered: false);
        sut.Play(0, DacCapture.Ramp(2048), 0);

        // Act
        DacCapture capture = DacCapture.Of(sut, 3810);

        // Assert
        Assert.Equal(1, capture.Value(0));
        List<int> changes = capture.Changes();
        Assert.Equal(1007, changes.Count);
        Assert.Equal(Enumerable.Range(1, 1007).Select(BeatSample), changes);
        Assert.All(changes.Zip(changes.Skip(1), (a, b) => b - a), gap => Assert.InRange(gap, 3, 4));
    }

    /// <summary>A play lands on the first beat after it: the samples before keep the old byte.</summary>
    [Fact]
    public void Play_BetweenBeats_StartsOnNextBeat()
    {
        // Arrange
        XgmPcmDriver sut = new(buffered: false);
        DacCapture.Of(sut, 2); // beat 0 at sample 0; beat 1 at sample 4

        // Act
        sut.Play(0, [50], 0);

        // Assert
        DacCapture capture = DacCapture.Of(sut, 3);
        Assert.Equal([0x80, 0x80, 0x80 + 50], capture.Data);
    }

    /// <summary>The channels are added in order, each sum stuck at +127: 100 + 100 − 100 is 27, not 100.</summary>
    [Fact]
    public void Play_WithMixPastTop_SticksInChannelOrder()
    {
        // Arrange
        XgmPcmDriver sut = new(buffered: false);
        sut.Play(0, [100], 0);
        sut.Play(1, [100], 0);

        // Act
        sut.Play(2, [-100], 0);

        // Assert
        Assert.Equal(27, DacCapture.Of(sut, 1).Value(0));
    }

    /// <summary>A sum past the bottom sticks at −128: −100 − 100 is −128.</summary>
    [Fact]
    public void Play_WithMixPastBottom_SticksAtMinimum()
    {
        // Arrange
        XgmPcmDriver sut = new(buffered: false);
        sut.Play(0, [-100], 0);

        // Act
        sut.Play(3, [-100], 0);

        // Assert
        Assert.Equal(-128, DacCapture.Of(sut, 1).Value(0));
    }

    /// <summary>Channel 0 being silent doesn't change the order: 127 + 127 sticks at 127, and −1 then makes 126.</summary>
    [Fact]
    public void Play_WithMixOnLaterChannels_SticksInChannelOrder()
    {
        // Arrange
        XgmPcmDriver sut = new(buffered: false);
        sut.Play(1, [127], 0);
        sut.Play(2, [127], 0);

        // Act
        sut.Play(3, [-1], 0);

        // Assert
        Assert.Equal(126, DacCapture.Of(sut, 1).Value(0));
    }

    /// <summary>A sample plays its bytes, then its 256-byte padding of silence.</summary>
    [Fact]
    public void Play_WithSample_PlaysIntoPadding()
    {
        // Arrange
        XgmPcmDriver sut = new(buffered: false);

        // Act
        sut.Play(2, [10, 20, 30], 8);

        // Assert
        DacCapture capture = DacCapture.Of(sut, BeatSample(255));
        Assert.Equal(30, capture.Value(BeatSample(2)));
        Assert.Equal(0, capture.Value(BeatSample(3)));
    }

    /// <summary>Until the end of its padding the channel stays busy at the sample's priority.</summary>
    [Fact]
    public void Generate_BeforeEndOfPadding_KeepsChannelBusy()
    {
        // Arrange
        XgmPcmDriver sut = new(buffered: false);
        sut.Play(2, [10, 20, 30], 8);

        // Act
        DacCapture.Of(sut, BeatSample(255));

        // Assert
        Assert.True(sut.IsPlaying(2));
        Assert.Equal(8, sut.PriorityOf(2));
    }

    /// <summary>On beat 255, the padding's last byte, the channel comes free at priority 0.</summary>
    [Fact]
    public void Generate_AtEndOfPadding_FreesChannel()
    {
        // Arrange
        XgmPcmDriver sut = new(buffered: false);
        sut.Play(2, [10, 20, 30], 8);
        DacCapture.Of(sut, BeatSample(255));

        // Act
        DacCapture.Of(sut, 1);

        // Assert
        Assert.False(sut.IsPlaying(2));
        Assert.Equal(0, sut.PriorityOf(2));
    }

    /// <summary>A play or stop replaces the sound on its channel only at the same or a higher priority.</summary>
    [Fact]
    public void Play_WithBusyChannel_LetsPriorityDecide()
    {
        // Act
        XgmPcmDriver sut = new(buffered: false);

        // Assert
        Assert.True(sut.Play(0, [1], 8));
        Assert.False(sut.Play(0, [2], 4));
        Assert.False(sut.Stop(0, 4));
        Assert.Equal(1, DacCapture.Of(sut, 1).Value(0));
        Assert.True(sut.Play(0, [3], 8));
        Assert.Equal(3, DacCapture.Of(sut, 4).Value(3)); // beat 1, on sample 4
        Assert.True(sut.Stop(0, 12));
        Assert.False(sut.IsPlaying(0));
        Assert.Equal(0, sut.PriorityOf(0));
        Assert.True(sut.Play(0, [4], 0));
    }

    /// <summary>A stop silences the channel from the next beat; the DAC holds its byte until then.</summary>
    [Fact]
    public void Stop_WithChannelPlaying_SilencesIt()
    {
        // Arrange
        XgmPcmDriver sut = new(buffered: false);
        sut.Play(1, [-5, -5, -5, -5], 0);
        DacCapture.Of(sut, 1);

        // Act
        sut.Stop(1, 0);

        // Assert
        DacCapture capture = DacCapture.Of(sut, 20);
        Assert.All(capture.Data[..3], b => Assert.Equal(0x80 - 5, b)); // held until beat 1, on sample 4
        Assert.All(capture.Data[3..], b => Assert.Equal(0x80, b));
    }

    /// <summary>
    /// The DAC turns on with the play and, checked after every 256 beats, stays on for three blocks after the block the
    /// sound ended in: off from the 1024th beat for a 256-byte sample.
    /// </summary>
    [Fact]
    public void Generate_AfterLastSound_KeepsDacOnThreeBlocks()
    {
        // Arrange
        XgmPcmDriver sut = new(buffered: false);
        sut.Play(0, DacCapture.Ramp(256), 0);

        // Act
        DacCapture capture = DacCapture.Of(sut, BeatSample(1100));

        // Assert
        Assert.Equal(0x80, capture.Enable[0]);
        Assert.Equal(BeatSample(1023), capture.FirstOff());
        Assert.All(capture.Enable[BeatSample(1023)..], b => Assert.Equal(0, b));
    }

    /// <summary>While a sound plays the DAC stays on, however long it is.</summary>
    [Fact]
    public void Generate_WhileSoundPlays_KeepsDacOn()
    {
        // Arrange
        XgmPcmDriver sut = new(buffered: false);
        sut.Play(3, DacCapture.Ramp(2560), 0);

        // Act
        DacCapture capture = DacCapture.Of(sut, BeatSample(2560));

        // Assert
        Assert.Equal(-1, capture.FirstOff());
    }

    /// <summary>A channel outside 0 to 3 is refused.</summary>
    [Theory]
    [InlineData(4)]
    [InlineData(-1)]
    public void Play_WithBadChannel_Throws(int channel)
    {
        // Arrange
        XgmPcmDriver sut = new(buffered: false);

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => sut.Play(channel, [1], 0));
    }

    /// <summary>A priority outside 0 to 15 is refused by a play.</summary>
    [Theory]
    [InlineData(16)]
    [InlineData(-1)]
    public void Play_WithBadPriority_Throws(int priority)
    {
        // Arrange
        XgmPcmDriver sut = new(buffered: false);

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => sut.Play(0, [1], priority));
    }

    /// <summary>A priority outside 0 to 15 is refused by a stop.</summary>
    [Theory]
    [InlineData(16)]
    [InlineData(-1)]
    public void Stop_WithBadPriority_Throws(int priority)
    {
        // Arrange
        XgmPcmDriver sut = new(buffered: false);

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => sut.Stop(0, priority));
    }

    /// <summary>An empty sample is refused.</summary>
    [Fact]
    public void Play_WithEmptySample_Throws()
    {
        // Arrange
        XgmPcmDriver sut = new(buffered: false);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => sut.Play(0, [], 0));
    }

    /// <summary>A null sample is refused.</summary>
    [Fact]
    public void Play_WithNullSample_Throws()
    {
        // Arrange
        XgmPcmDriver sut = new(buffered: false);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => sut.Play(0, null!, 0)); // null on purpose, past the annotation
    }

    /// <summary>Both registers need the same length.</summary>
    [Fact]
    public void Generate_WithDifferentLengths_Throws()
    {
        // Arrange
        XgmPcmDriver sut = new(buffered: false);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => sut.Generate(new byte[2], new byte[1]));
    }

    /// <summary>Generating nothing is allowed.</summary>
    [Fact]
    public void Generate_WithEmptyBuffers_DoesNotThrow()
    {
        // Arrange
        XgmPcmDriver sut = new(buffered: false);

        // Act
        Exception? thrown = Record.Exception(() => sut.Generate([], []));

        // Assert
        Assert.Null(thrown);
    }

    /// <summary>The bytes drive the YM3438's DAC: channel 6's output is the byte minus <c>$80</c>, as a 9-bit value.</summary>
    [Fact]
    public void Generate_WithYm3438_PlaysBytesThroughIt()
    {
        // Arrange
        XgmPcmDriver sut = new(buffered: false);
        sut.Play(0, [-128, -64, 0, 64, 127], 0);
        DacCapture capture = DacCapture.Of(sut, BeatSample(5));
        int[] expected = [.. Enumerable.Range(0, capture.Data.Length).Select(i => capture.Value(i) * 2 * 128 / 6)];
        Ym3438 chip = new();
        chip.WriteRegister(1, 0xb6, 0xc0); // channel 6 to both speakers
        int[] left = new int[capture.Data.Length];
        int[] right = new int[capture.Data.Length];

        // Act
        for (int i = 0; i < capture.Data.Length; i++)
        {
            chip.WriteRegister(0, 0x2b, capture.Enable[i]);
            chip.WriteRegister(0, 0x2a, capture.Data[i]);
            chip.Generate(left.AsSpan(i, 1), right.AsSpan(i, 1));
        }

        // Assert
        Assert.Equal(expected, left);
        Assert.Equal(expected, right);
    }

    /// <summary>A driver buffers its plays by default.</summary>
    [Fact]
    public void Constructor_ByDefault_IsBuffered()
    {
        // Act
        XgmPcmDriver sut = new();

        // Assert
        Assert.True(sut.Buffered);
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
    public void Play_WithBuffering_IsHeardFromFirstBlockAfterLead(int sentOnBeat, int heardFromBeat)
    {
        // Arrange
        XgmPcmDriver sut = new();
        DataUntilBeat(sut, sentOnBeat);

        // Act
        sut.Play(0, [5, 6, 7], 0);

        // Assert
        List<byte> before = DataUntilBeat(sut, heardFromBeat);
        List<byte> heard = DataUntilBeat(sut, heardFromBeat + 1);
        Assert.All(before, b => Assert.Equal(0x80, b));
        Assert.Equal(0x80 + 5, heard[^1]);
    }

    /// <summary>The DAC bytes the driver makes until it has sent <paramref name="beat"/> beats, one sample at a time.</summary>
    private static List<byte> DataUntilBeat(XgmPcmDriver driver, long beat)
    {
        List<byte> data = [];
        byte[] one = new byte[1];
        byte[] enable = new byte[1];
        while (driver.BeatsSent < beat)
        {
            driver.Generate(one, enable);
            data.Add(one[0]);
        }
        return data;
    }

    /// <summary>A buffered play or stop refuses bad arguments when it is sent, not when it is heard.</summary>
    [Fact]
    public void Play_WithBufferingAndBadArguments_ThrowsAtOnce()
    {
        // Act
        XgmPcmDriver sut = new();

        // Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => sut.Play(4, [1], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => sut.Stop(0, 16));
        Assert.Throws<ArgumentException>(() => sut.Play(0, [], 0));
    }

    /// <summary>
    /// Every add that clips costs 11 Z80 cycles and delays the beats after it (SGDK's driver, measured): four loud
    /// channels that clip on every beat send 3 × 165 master clocks more per beat, 13 % fewer beats than quiet ones.
    /// </summary>
    [Fact]
    public void Generate_WithClipping_SlowsBeat()
    {
        // Arrange
        const int samples = 100_000;
        sbyte[] loud = [.. Enumerable.Repeat((sbyte)100, 32_768)];
        sbyte[] quiet = [.. Enumerable.Repeat((sbyte)10, 32_768)];
        long Beats(sbyte[] sample)
        {
            XgmPcmDriver sut = new(buffered: false);
            for (int c = 0; c < 4; c++)
            {
                sut.Play(c, sample, 0);
            }
            DacCapture.Of(sut, samples);
            return sut.BeatsSent;
        }
        long quietBeats = Beats(quiet);

        // Act
        long loudBeats = Beats(loud);

        // Assert
        Assert.Equal(samples * 1008.0 / 3810, quietBeats, 1.0);
        Assert.Equal(samples * 1008.0 / (3810 + (3 * 165)), loudBeats, 2.0);
    }
}
