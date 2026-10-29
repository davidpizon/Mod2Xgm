using Mod2Xgm.Mapping;
using Mod2Xgm.Model;

namespace Mod2Xgm.Tests.Mapping;

/// <summary>Tests for <see cref="SamplePitch"/>: the cycle length of looped and one-shot tones, and no pitch for noise or silence.</summary>
public sealed class SamplePitchTests
{
    private static float[] Sine(int frames, double period, double phase = 0) =>
        [.. Enumerable.Range(0, frames).Select(i => (float)Math.Sin((2 * Math.PI * i / period) + phase))];

    /// <summary>A one-cycle loop is a whole tone: its cycle is the loop's length, however short.</summary>
    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(100)]
    [InlineData(512)]
    public void ALoopOfOneCycleHasTheLoopsLength(int length)
    {
        Sample sample = new("wave", Sine(length, length), new SampleLoop(0, length));

        double? period = SamplePitch.Measure(sample).PeriodFrames;

        Assert.NotNull(period);
        Assert.Equal(length, period.Value, 0.05);
    }

    /// <summary>A loop holding several cycles is measured by the cycle, not the loop.</summary>
    [Fact]
    public void ALoopOfSeveralCyclesIsMeasuredByTheCycle()
    {
        Sample sample = new("wave", Sine(256, 64), new SampleLoop(0, 256));

        Assert.Equal(64, SamplePitch.Measure(sample).PeriodFrames!.Value, 0.05);
    }

    /// <summary>A one-shot tone is measured after its attack, with sub-frame precision.</summary>
    [Fact]
    public void AOneShotToneIsMeasured()
    {
        Sample sample = new("bass", Sine(8000, 75.5), null);

        Assert.Equal(75.5, SamplePitch.Measure(sample).PeriodFrames!.Value, 0.1);
    }

    /// <summary>A rich wave (a saw) is measured at its fundamental, not a harmonic.</summary>
    [Fact]
    public void ASawIsMeasuredAtItsFundamental()
    {
        float[] saw = [.. Enumerable.Range(0, 48).Select(i => (float)((i / 24.0) - 1))];
        Sample sample = new("saw", saw, new SampleLoop(0, 48));

        Assert.Equal(48, SamplePitch.Measure(sample).PeriodFrames!.Value, 0.1);
    }

    /// <summary>Noise, silence and samples too short to measure have no pitch.</summary>
    [Fact]
    public void NoiseSilenceAndShortSamplesHaveNoPitch()
    {
        float[] noise = [.. new Random(3).GetItems([-1f, -0.5f, 0.5f, 1f], 8000)];

        Assert.Null(SamplePitch.Measure(new Sample("noise", noise, null)).PeriodFrames);
        Assert.Null(SamplePitch.Measure(new Sample("silence", new float[8000], null)).PeriodFrames);
        Assert.Null(SamplePitch.Measure(new Sample("click", Sine(500, 20), null)).PeriodFrames);
        Assert.Null(SamplePitch.Measure(new Sample("quiet loop", new float[64], new SampleLoop(0, 64))).PeriodFrames);
    }

    /// <summary>The frequency at a playback rate is the rate over the cycle length; without a pitch there is none.</summary>
    [Fact]
    public void FrequencyIsRateOverCycle()
    {
        Assert.Equal(259.0, new SamplePitch(32).FrequencyAt(8288)!.Value, 0.01);
        Assert.Null(new SamplePitch(null).FrequencyAt(8288));
    }
}
