using Mod2Xgm.Render.Psg;

namespace Mod2Xgm.Tests.Render.Psg;

/// <summary>The SN76489 model's interface and the simple facts of the Sega PSG that the probes take for granted.</summary>
public sealed class Sn76489Tests
{
    /// <summary>
    /// Nuked-PSG's levels for volumes 0–15, measured from its output (× 8192, truncated) with the loudest at MAME's 8191;
    /// 15 is off (docs/adr/0067-psg-volume-levels.md).
    /// </summary>
    private static readonly int[] NukedLevels = [8191, 6324, 5095, 3973, 3129, 2375, 1875, 1425, 1081, 786, 589, 417, 278, 155, 73, 0];

    private static int[] Play(Sn76489 chip, int samples)
    {
        int[] output = new int[samples];
        chip.Generate(output);
        return output;
    }

    private static Sn76489 With(params int[] writes)
    {
        Sn76489 chip = new();
        foreach (int data in writes)
        {
            chip.Write((byte)data);
        }

        return chip;
    }

    /// <summary>The sample indexes where the output changes.</summary>
    private static List<int> Edges(int[] output) => [.. Enumerable.Range(1, output.Length - 1).Where(i => output[i] != output[i - 1])];

    /// <summary>One sample per 16 PSG clocks: about 223.7 kHz on an NTSC Genesis.</summary>
    [Theory]
    [InlineData(Sn76489.NtscClockHz, 3_579_545 / 16.0)]
    [InlineData(Sn76489.PalClockHz, 3_546_893 / 16.0)]
    public void SampleRateHz_WithClock_IsClockOver16(double clockHz, double expected)
    {
        // Act
        double rate = Sn76489.SampleRateHz(clockHz);

        // Assert
        Assert.Equal(expected, rate, 9);
    }

    /// <summary>A fresh chip is silent: every channel starts at volume 15.</summary>
    [Fact]
    public void Generate_OnFreshChip_IsSilent()
    {
        // Act & Assert
        Assert.All(Play(new Sn76489(), 5000), s => Assert.Equal(0, s));
    }

    /// <summary>Generating nothing is allowed and changes nothing.</summary>
    [Fact]
    public void Generate_WithNoSamples_DoesNothing()
    {
        // Arrange
        Sn76489 sut = With(0x8e, 0x0f, 0x90);

        // Act
        sut.Generate([]);

        // Assert
        Assert.Equal(Play(With(0x8e, 0x0f, 0x90), 600), Play(sut, 600));
    }

    /// <summary>The sixteen volumes.</summary>
    public static TheoryData<int> Volumes { get; } = new(Enumerable.Range(0, 16));

    /// <summary>Each volume plays Nuked-PSG's level, negated as MAME does: a high channel pulls the output down; the low half of the wave is 0.</summary>
    [Theory]
    [MemberData(nameof(Volumes))]
    public void Generate_WithVolume_PlaysNukedsLevelNegated(int volume)
    {
        // Arrange
        Sn76489 sut = With(0x80 | 0x0e, 0x0f, 0x90 | volume);

        // Act
        int[] output = Play(sut, 1000);

        // Assert
        Assert.Equal(-NukedLevels[volume], output.Min());
        Assert.Equal(0, output.Max());
    }

    /// <summary>All four channels high at volume 0 reach the full scale.</summary>
    [Fact]
    public void Generate_WithFourLoudChannels_ReachesFullScale()
    {
        // Act
        // Period 0 flips every sample, so the three tones are high together on every other sample; the noise is
        // high once its first 1 bit comes round (white noise, rate 0).
        int[] output = Play(With(0x80, 0x00, 0xa0, 0x00, 0xc0, 0x00, 0x90, 0xb0, 0xd0, 0xe4, 0xf0), 4096);

        // Assert
        Assert.Equal(-Sn76489.FullScale, output.Min());
        Assert.Equal(Sn76489.FullScale, 4 * NukedLevels[0]);
    }

    /// <summary>A tone flips every <c>period</c> samples, so it sounds at clock / (32 × period).</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(254)]
    [InlineData(0x3ff)]
    public void Generate_WithTone_FlipsEveryPeriod(int period)
    {
        // Arrange
        int[] output = Play(With(0x80 | (period & 0xf), period >> 4, 0x90), 8 * 1024);

        // Act
        List<int> edges = Edges(output);

        // Assert
        Assert.All(edges.Zip(edges.Skip(1)), pair => Assert.Equal(period, pair.Second - pair.First));
    }

    /// <summary>On Sega's chip a period of 0 stays 0 and flips every sample like period 1 (TI's acts as <c>$400</c>).</summary>
    [Fact]
    public void Generate_WithPeriodZero_FlipsEverySample()
    {
        // Act
        int[] output = Play(With(0x80, 0x00, 0x90), 100);

        // Assert
        Assert.All(Edges(output).Zip(Edges(output).Skip(1)), pair => Assert.Equal(1, pair.Second - pair.First));
    }

    /// <summary>A data byte after a period latch sets the high 6 bits; a latch byte alone changes only the low 4.</summary>
    [Fact]
    public void Write_WithPeriodBytes_SetsTheirOwnBits()
    {
        // Act & Assert
        // $0FE, then a data byte $08 → $08E, then a latch byte with low bits 3 → $083.
        Assert.Equal(Play(With(0x83, 0x08, 0x90), 4000), Play(With(0x8e, 0x0f, 0x08, 0x83, 0x90), 4000));
    }

    /// <summary>
    /// A shorter period written during a half-wave flips the tone at once when the count is already past it, then every
    /// new period (docs/adr/0065-sn76489-chip-differences.md, difference 2; MAME would first count down the old period's
    /// remaining 150 samples).
    /// </summary>
    [Fact]
    public void Write_WithShorterPeriod_FlipsAtOnce()
    {
        // Arrange
        // Period 200 flips at sample 200 and has counted 51 more when period 20 is written; 51 ≥ 20, so the next
        // sample shows the flip, and then every 20.
        Sn76489 sut = With(0x88, 0x0c, 0x90);
        Play(sut, 250);

        // Act
        sut.Write(0x84);
        sut.Write(0x01);

        // Assert
        Assert.Equal([1, 21, 41], Edges(Play(sut, 50)));
    }

    /// <summary>
    /// A longer period written during a half-wave stretches it: the count goes on from where it got to, up to the new
    /// period (docs/adr/0065-sn76489-chip-differences.md, difference 2; MAME would flip after the old period's 50).
    /// </summary>
    [Fact]
    public void Write_WithLongerPeriod_StretchesHalfWave()
    {
        // Arrange
        // Period 100 flips at sample 100 and has counted to 51 when period 300 is written: 249 more steps reach 300,
        // so the flip shows 250 samples later, and then every 300.
        Sn76489 sut = With(0x84, 0x06, 0x90);
        Play(sut, 150);

        // Act
        sut.Write(0x8c);
        sut.Write(0x12);

        // Assert
        Assert.Equal([250, 550], Edges(Play(sut, 600)));
    }

    /// <summary>
    /// A write to the noise register empties the shift register on the next sample, so a high noise output goes low at
    /// once and stays low until the first 1 is shifted through, 15 shifts later (docs/adr/0065-sn76489-chip-differences.md,
    /// difference 3; MAME would keep the high bit until its next shift).
    /// </summary>
    [Fact]
    public void Write_WithNoiseControl_SilencesNoiseAtOnce()
    {
        // Arrange
        int high = Array.FindIndex(Play(With(0xe4, 0xf0), 4096), s => s != 0);
        Sn76489 sut = With(0xe4, 0xf0);
        int before = Play(sut, high + 1)[high];

        // Act
        sut.Write(0xe4);

        // Assert
        Assert.True(high > 0);
        Assert.NotEqual(0, before);
        // 15 shifts of 32 samples, less up to one shift for where the rate counter stands.
        Assert.All(Play(sut, (14 * 32) - 1), s => Assert.Equal(0, s));
    }

    /// <summary>
    /// At rate 3 the noise shifts on tone 3's rising edges, in step with it, so the noise output changes only on samples
    /// where tone 3 goes high (docs/adr/0065-sn76489-chip-differences.md, difference 3; MAME counts twice tone 3's period
    /// on a counter of its own).
    /// </summary>
    [Fact]
    public void Generate_WithNoiseRate3_ShiftsOnTone3Rise()
    {
        // Arrange
        // The same writes on two chips, one with only tone 3 audible and one with only the noise; the noise runs at
        // rate 0 first and switches to tone 3 at an odd moment, where a counter of its own would be out of step.
        Sn76489 toneChip = With(0xc5, 0x00, 0xd0, 0xff, 0xe4);
        Sn76489 noiseChip = With(0xc5, 0x00, 0xdf, 0xf0, 0xe4);
        Play(toneChip, 77);
        Play(noiseChip, 77);
        toneChip.Write(0xe3);
        noiseChip.Write(0xe3);
        int[] tone3 = Play(toneChip, 4096);
        int[] noise = Play(noiseChip, 4096);
        HashSet<int> rises = [.. Edges(tone3).Where(i => tone3[i] != 0)];

        // Act
        List<int> noiseEdges = Edges(noise);

        // Assert
        Assert.NotEmpty(noiseEdges);
        Assert.All(noiseEdges, i => Assert.Contains(i, rises));
    }

    /// <summary>A data byte after a volume latch sets that volume.</summary>
    [Fact]
    public void Write_WithDataAfterVolumeLatch_SetsVolume()
    {
        // Act & Assert
        Assert.Equal(-NukedLevels[6], Play(With(0x8e, 0x0f, 0x90, 0x06), 1000).Min());
    }

    /// <summary>The latch starts on register 3, tone 2's volume, so a data byte before any latch byte sets it (MAME, from hardware tests).</summary>
    [Fact]
    public void Write_WithDataAtPowerOn_SetsTone2Volume()
    {
        // Act & Assert
        Assert.Equal(-NukedLevels[0], Play(With(0x00, 0xae, 0x0f), 1000).Min());
    }

    /// <summary>Periodic noise is one high bit every 16 shifts; at rate 0 it shifts every 32 samples.</summary>
    [Fact]
    public void Generate_WithPeriodicNoise_IsOneBitIn16()
    {
        // Arrange
        int[] output = Play(With(0xe0, 0xf0), 32 * 16 * 8);
        List<int> edges = Edges(output);

        // Act
        List<int> rises = [.. edges.Where(i => output[i] != 0)];

        // Assert
        Assert.True(rises.Count >= 7);
        Assert.All(rises.Zip(rises.Skip(1)), pair => Assert.Equal(32 * 16, pair.Second - pair.First));
        Assert.All(edges.Where(i => output[i] == 0), fall => Assert.Contains(fall - 32, rises));
    }

    /// <summary>The fixed noise rates shift every 32, 64 and 128 samples (clock / 512, / 1024, / 2048).</summary>
    [Theory]
    [InlineData(0, 32)]
    [InlineData(1, 64)]
    [InlineData(2, 128)]
    public void Generate_WithNoiseRate_ShiftsAtDivider(int rate, int samplesPerShift)
    {
        // Arrange
        int[] output = Play(With(0xe0 | rate, 0xf0), samplesPerShift * 16 * 3);

        // Act
        List<int> rises = [.. Edges(output).Where(i => output[i] != 0)];

        // Assert
        Assert.All(rises.Zip(rises.Skip(1)), pair => Assert.Equal(samplesPerShift * 16, pair.Second - pair.First));
    }

    /// <summary>At rate 3 the noise follows tone 3: one shift per two of its flips, whether tone 3's period or the rate is written first.</summary>
    [Theory]
    [InlineData(0xc5, 0x00, 0xe3, 0xf0)]
    [InlineData(0xe3, 0xf0, 0xc5, 0x00)]
    public void Generate_WithNoiseRate3_FollowsTone3(int first, int second, int third, int fourth)
    {
        // Arrange
        int[] output = Play(With(first, second, third, fourth), 10 * 16 * 3);

        // Act
        List<int> rises = [.. Edges(output).Where(i => output[i] != 0)];

        // Assert
        Assert.All(rises.Zip(rises.Skip(1)), pair => Assert.Equal(10 * 16, pair.Second - pair.First));
    }

    /// <summary>
    /// Writing the noise register, even unchanged, restarts the noise from the beginning of its sequence (the rate
    /// counter keeps running, so only the first bit's length can differ).
    /// </summary>
    [Fact]
    public void Write_WithNoiseControl_RestartsNoise()
    {
        // Arrange
        Sn76489 sut = With(0xe4, 0xf0);
        Play(sut, 12_345);

        // Act
        sut.Write(0xe4);

        // Assert
        Assert.Equal(Gaps(Play(With(0xe4, 0xf0), 40_000)), Gaps(Play(sut, 40_000)));
        static List<int> Gaps(int[] output)
        {
            List<int> edges = Edges(output);
            return [.. edges.Zip(edges.Skip(1)).Select(pair => pair.Second - pair.First)];
        }
    }

    /// <summary>A data byte after the noise latch sets the noise register (here: white noise at rate 1).</summary>
    [Fact]
    public void Write_WithDataAfterNoiseLatch_SetsNoise()
    {
        // Act & Assert
        Assert.Equal(Play(With(0xe5, 0xf0), 20_000)[1..], Play(With(0xf0, 0xe0, 0x05), 20_000)[1..]);
    }

    /// <summary>White noise differs from periodic noise: its feedback taps bits 0 and 3.</summary>
    [Fact]
    public void Generate_WithWhiteNoise_IsNotPeriodic()
    {
        // Arrange
        int[] white = Play(With(0xe4, 0xf0), 32 * 16 * 8);

        // Act
        int[] periodic = Play(With(0xe0, 0xf0), 32 * 16 * 8);

        // Assert
        Assert.NotEqual(periodic, white);
        Assert.True(Edges(white).Count > Edges(periodic).Count);
    }

    /// <summary>Reset brings back the power-on state: silent.</summary>
    [Fact]
    public void Reset_AfterWrites_Silences()
    {
        // Arrange
        Sn76489 sut = With(0x8e, 0x0f, 0x90, 0xe4, 0xf0);
        Play(sut, 777);

        // Act
        sut.Reset();

        // Assert
        Assert.All(Play(sut, 2000), s => Assert.Equal(0, s));
    }

    /// <summary>Reset puts the latch back on tone 2's volume, so writes after it act as on a fresh chip.</summary>
    [Fact]
    public void Reset_AfterWrites_RestoresLatch()
    {
        // Arrange
        Sn76489 sut = With(0x8e, 0x0f, 0x90, 0xe4, 0xf0);
        Play(sut, 777);
        int[] fresh = Play(With(0x00, 0xae, 0x0f), 2000);

        // Act
        sut.Reset();

        // Assert
        sut.Write(0x00);
        sut.Write(0xae);
        sut.Write(0x0f);
        Assert.Equal(fresh, Play(sut, 2000));
    }
}
