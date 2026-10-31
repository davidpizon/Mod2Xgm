using Mod2Xgm.Render.Psg;

namespace Mod2Xgm.Tests.Render.Psg;

/// <summary>The SN76489 model's interface and the simple facts of the Sega PSG that the probes take for granted.</summary>
public sealed class Sn76489Tests
{
    /// <summary>MAME's levels for volumes 0–15: 8191, then 2 dB less per step, truncated; 15 is off.</summary>
    private static readonly int[] MameLevels = [8191, 6506, 5168, 4105, 3260, 2590, 2057, 1634, 1298, 1031, 819, 650, 516, 410, 326, 0];

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
    [Fact]
    public void SampleRateIsTheClockOver16()
    {
        Assert.Equal(3_579_545 / 16.0, Sn76489.SampleRateHz(Sn76489.NtscClockHz), 9);
        Assert.Equal(3_546_893 / 16.0, Sn76489.SampleRateHz(Sn76489.PalClockHz), 9);
    }

    /// <summary>A fresh chip is silent: every channel starts at volume 15.</summary>
    [Fact]
    public void AFreshChipIsSilent() => Assert.All(Play(new Sn76489(), 5000), s => Assert.Equal(0, s));

    /// <summary>Generating nothing is allowed and changes nothing.</summary>
    [Fact]
    public void GeneratingNoSamplesIsAllowed()
    {
        Sn76489 chip = With(0x8e, 0x0f, 0x90);
        chip.Generate([]);
        Assert.Equal(Play(With(0x8e, 0x0f, 0x90), 600), Play(chip, 600));
    }

    /// <summary>Each volume plays MAME's level, negated: a high channel pulls the output down; the low half of the wave is 0.</summary>
    [Fact]
    public void VolumesPlayMamesLevelsNegated()
    {
        for (int volume = 0; volume < 16; volume++)
        {
            int[] output = Play(With(0x80 | 0x0e, 0x0f, 0x90 | volume), 1000);
            Assert.Equal(-MameLevels[volume], output.Min());
            Assert.Equal(0, output.Max());
        }
    }

    /// <summary>All four channels high at volume 0 reach the full scale.</summary>
    [Fact]
    public void FourLoudChannelsReachFullScale()
    {
        // Period 0 flips every sample, so the three tones are high together on every other sample; the noise is
        // high once its first 1 bit comes round (white noise, rate 0).
        int[] output = Play(With(0x80, 0x00, 0xa0, 0x00, 0xc0, 0x00, 0x90, 0xb0, 0xd0, 0xe4, 0xf0), 4096);
        Assert.Equal(-Sn76489.FullScale, output.Min());
        Assert.Equal(Sn76489.FullScale, 4 * MameLevels[0]);
    }

    /// <summary>A tone flips every <c>period</c> samples, so it sounds at clock / (32 × period).</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(254)]
    [InlineData(0x3ff)]
    public void AToneFlipsEveryPeriod(int period)
    {
        int[] output = Play(With(0x80 | (period & 0xf), period >> 4, 0x90), 8 * 1024);
        List<int> edges = Edges(output);
        Assert.All(edges.Zip(edges.Skip(1)), pair => Assert.Equal(period, pair.Second - pair.First));
    }

    /// <summary>On Sega's chip a period of 0 stays 0 and flips every sample like period 1 (TI's acts as <c>$400</c>).</summary>
    [Fact]
    public void PeriodZeroFlipsEverySample()
    {
        int[] output = Play(With(0x80, 0x00, 0x90), 100);
        Assert.All(Edges(output).Zip(Edges(output).Skip(1)), pair => Assert.Equal(1, pair.Second - pair.First));
    }

    /// <summary>A data byte after a period latch sets the high 6 bits; a latch byte alone changes only the low 4.</summary>
    [Fact]
    public void PeriodBytesSetTheirOwnBits()
    {
        // $0FE, then a data byte $08 → $08E, then a latch byte with low bits 3 → $083.
        Assert.Equal(Play(With(0x83, 0x08, 0x90), 4000), Play(With(0x8e, 0x0f, 0x08, 0x83, 0x90), 4000));
    }

    /// <summary>A data byte after a volume latch sets that volume.</summary>
    [Fact]
    public void ADataByteAfterAVolumeLatchSetsTheVolume() =>
        Assert.Equal(-MameLevels[6], Play(With(0x8e, 0x0f, 0x90, 0x06), 1000).Min());

    /// <summary>The latch starts on register 3, tone 2's volume, so a data byte before any latch byte sets it (MAME, from hardware tests).</summary>
    [Fact]
    public void ThePowerOnLatchIsTone2sVolume() =>
        Assert.Equal(-MameLevels[0], Play(With(0x00, 0xae, 0x0f), 1000).Min());

    /// <summary>Periodic noise is one high bit every 16 shifts; at rate 0 it shifts every 32 samples.</summary>
    [Fact]
    public void PeriodicNoiseIsOneBitIn16()
    {
        int[] output = Play(With(0xe0, 0xf0), 32 * 16 * 8);
        List<int> edges = Edges(output);
        List<int> rises = [.. edges.Where(i => output[i] != 0)];
        Assert.True(rises.Count >= 7);
        Assert.All(rises.Zip(rises.Skip(1)), pair => Assert.Equal(32 * 16, pair.Second - pair.First));
        Assert.All(edges.Where(i => output[i] == 0), fall => Assert.Contains(fall - 32, rises));
    }

    /// <summary>The fixed noise rates shift every 32, 64 and 128 samples (clock / 512, / 1024, / 2048).</summary>
    [Theory]
    [InlineData(0, 32)]
    [InlineData(1, 64)]
    [InlineData(2, 128)]
    public void NoiseRatesShiftAtTheirDivider(int rate, int samplesPerShift)
    {
        int[] output = Play(With(0xe0 | rate, 0xf0), samplesPerShift * 16 * 3);
        List<int> rises = [.. Edges(output).Where(i => output[i] != 0)];
        Assert.All(rises.Zip(rises.Skip(1)), pair => Assert.Equal(samplesPerShift * 16, pair.Second - pair.First));
    }

    /// <summary>At rate 3 the noise follows tone 3: one shift per two of its flips, whether tone 3's period or the rate is written first.</summary>
    [Theory]
    [InlineData(0xc5, 0x00, 0xe3, 0xf0)]
    [InlineData(0xe3, 0xf0, 0xc5, 0x00)]
    public void Rate3FollowsTone3(int first, int second, int third, int fourth)
    {
        int[] output = Play(With(first, second, third, fourth), 10 * 16 * 3);
        List<int> rises = [.. Edges(output).Where(i => output[i] != 0)];
        Assert.All(rises.Zip(rises.Skip(1)), pair => Assert.Equal(10 * 16, pair.Second - pair.First));
    }

    /// <summary>
    /// Writing the noise register, even unchanged, restarts the noise from the beginning of its sequence (the rate
    /// counter keeps running, so only the first bit's length can differ).
    /// </summary>
    [Fact]
    public void ANoiseWriteRestartsTheNoise()
    {
        Sn76489 chip = With(0xe4, 0xf0);
        Play(chip, 12_345);
        chip.Write(0xe4);
        Assert.Equal(Gaps(Play(With(0xe4, 0xf0), 40_000)), Gaps(Play(chip, 40_000)));

        static List<int> Gaps(int[] output)
        {
            List<int> edges = Edges(output);
            return [.. edges.Zip(edges.Skip(1)).Select(pair => pair.Second - pair.First)];
        }
    }

    /// <summary>A data byte after the noise latch sets the noise register (here: white noise at rate 1).</summary>
    [Fact]
    public void ADataByteAfterTheNoiseLatchSetsTheNoise() =>
        Assert.Equal(Play(With(0xe5, 0xf0), 20_000)[1..], Play(With(0xf0, 0xe0, 0x05), 20_000)[1..]);

    /// <summary>White noise differs from periodic noise: its feedback taps bits 0 and 3.</summary>
    [Fact]
    public void WhiteNoiseIsNotPeriodic()
    {
        int[] white = Play(With(0xe4, 0xf0), 32 * 16 * 8);
        int[] periodic = Play(With(0xe0, 0xf0), 32 * 16 * 8);
        Assert.NotEqual(periodic, white);
        Assert.True(Edges(white).Count > Edges(periodic).Count);
    }

    /// <summary>Reset brings back the power-on state: silent, and the latch on tone 2's volume.</summary>
    [Fact]
    public void ResetRestoresThePowerOnState()
    {
        Sn76489 chip = With(0x8e, 0x0f, 0x90, 0xe4, 0xf0);
        Play(chip, 777);
        chip.Reset();
        Assert.All(Play(chip, 2000), s => Assert.Equal(0, s));
        chip.Write(0x00);
        chip.Write(0xae);
        chip.Write(0x0f);
        Assert.Equal(Play(With(0x00, 0xae, 0x0f), 2000), Play(chip, 2000));
    }
}
