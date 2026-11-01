using Mod2Xgm.Analysis;
using Mod2Xgm.Optimizer;
using Mod2Xgm.Render.Psg;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="PsgNoteRenderer"/> and <see cref="PsgSetting"/>: PSG tries played on the SN76489 model.</summary>
public sealed class PsgNoteRendererTests
{
    /// <summary>Sixty steps at the loudest volume: a second held.</summary>
    private static readonly int[] Loud = [.. Enumerable.Repeat(0, PsgSetting.StepsPerSecond)];

    /// <summary>A tone sounds at its period's frequency, one rising zero crossing per cycle, at about ±1.</summary>
    [Fact]
    public void AToneSoundsAtItsPeriod()
    {
        PsgSetting tone = new(PsgSound.Tone, 0, 254, Loud);

        float[] sound = PsgNoteRenderer.Render(tone, 1);

        Assert.InRange(sound.Length, SampleAnalyzer.AnalysisRateHz - 1, SampleAnalyzer.AnalysisRateHz);
        Assert.Equal(PsgSetting.ToneFrequency(254), CyclesPerSecond(sound), 1.5);
        Assert.InRange(sound.Skip(4410).Max(), 0.9, 1.25);
    }

    /// <summary>Periodic noise clocked by tone channel 3 buzzes a sixteenth below it: Sega's 16-bit shift register.</summary>
    [Fact]
    public void PeriodicNoiseBuzzesASixteenthBelowItsClock()
    {
        PsgSetting buzz = new(PsgSound.PeriodicNoise, PsgSetting.Tone3Rate, 40, Loud);

        float[] sound = PsgNoteRenderer.Render(buzz, 1);

        Assert.Equal(buzz.PitchHz!.Value, CyclesPerSecond(sound), 1.5);
        Assert.Equal(PsgSetting.ToneFrequency(40) / 16, buzz.PitchHz.Value, 1e-9);
    }

    /// <summary>At a fixed rate, periodic noise buzzes at a sixteenth of the shift rate, clock / 512 at rate 0.</summary>
    [Fact]
    public void FixedRatePeriodicNoiseBuzzesAtItsRate()
    {
        PsgSetting buzz = new(PsgSound.PeriodicNoise, 0, 0, Loud);

        Assert.Equal(Sn76489.NtscClockHz / 512 / 16, buzz.PitchHz!.Value, 1e-9);
        Assert.Equal(buzz.PitchHz.Value, CyclesPerSecond(PsgNoteRenderer.Render(buzz, 1)), 1.5);
    }

    /// <summary>White noise is no tone: it crosses zero far more often than its shift rate's sixteenth, and has no pitch.</summary>
    [Fact]
    public void WhiteNoiseHasNoPitch()
    {
        PsgSetting hiss = new(PsgSound.WhiteNoise, 0, 0, Loud);

        Assert.Null(hiss.PitchHz);
        Assert.True(CyclesPerSecond(PsgNoteRenderer.Render(hiss, 1)) > 1000);
    }

    /// <summary>The volume follows the steps: off after the first half second, the sound dies away.</summary>
    [Fact]
    public void TheVolumeFollowsTheSteps()
    {
        int[] halfSecond = [.. Enumerable.Range(0, PsgSetting.StepsPerSecond).Select(s => s < 30 ? 0 : PsgLevels.Off)];

        float[] sound = PsgNoteRenderer.Render(new PsgSetting(PsgSound.Tone, 0, 254, halfSecond), 1);

        Assert.True(Rms(sound, 0.1, 0.45) > 0.9);
        Assert.True(Rms(sound, 0.7, 1) < 0.01);
    }

    /// <summary>A try's channels: a tone or noise at a fixed rate takes one; noise clocked by tone channel 3 takes two.</summary>
    [Fact]
    public void TriesCountTheirChannels()
    {
        Assert.Equal(1, new PsgSetting(PsgSound.Tone, 0, 100, Loud).Channels);
        Assert.Equal(1, new PsgSetting(PsgSound.WhiteNoise, 2, 0, Loud).Channels);
        Assert.Equal(2, new PsgSetting(PsgSound.WhiteNoise, PsgSetting.Tone3Rate, 100, Loud).Channels);
        Assert.Null(new PsgSetting(PsgSound.Tone, 0, 100, Loud).ShiftHz);
        Assert.Null(new PsgSetting(PsgSound.WhiteNoise, 1, 0, Loud).ToneHz);
        Assert.Equal(Sn76489.NtscClockHz / 1024, new PsgSetting(PsgSound.WhiteNoise, 1, 0, Loud).ShiftHz!.Value, 1e-9);
        Assert.Equal(254, PsgSetting.NearestPeriod(440));
        Assert.True(PsgSetting.NearestPeriod(55) > PsgSetting.MaxPeriod);
    }

    /// <summary>A try with no steps is silent; arguments are checked.</summary>
    [Fact]
    public void NoStepsIsSilenceAndArgumentsAreChecked()
    {
        Assert.All(PsgNoteRenderer.Render(new PsgSetting(PsgSound.Tone, 0, 254, []), 0.1), v => Assert.Equal(0, v));
        Assert.Throws<ArgumentNullException>(() => PsgNoteRenderer.Render(null!, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PsgNoteRenderer.Render(new PsgSetting(PsgSound.Tone, 0, 254, Loud), 0));
    }

    /// <summary>Rising zero crossings per second after the first 0.1 s (the DC block settling).</summary>
    private static double CyclesPerSecond(float[] sound)
    {
        int from = SampleAnalyzer.AnalysisRateHz / 10;
        int crossings = 0;
        for (int i = from + 1; i < sound.Length; i++)
        {
            if (sound[i - 1] < 0 && sound[i] >= 0)
            {
                crossings++;
            }
        }

        return crossings / ((double)(sound.Length - from - 1) / SampleAnalyzer.AnalysisRateHz);
    }

    /// <summary>The RMS of <paramref name="sound"/> from <paramref name="from"/> to <paramref name="to"/> seconds.</summary>
    private static double Rms(float[] sound, double from, double to)
    {
        int a = (int)(from * SampleAnalyzer.AnalysisRateHz);
        int b = Math.Min(sound.Length, (int)(to * SampleAnalyzer.AnalysisRateHz));
        double sum = 0;
        for (int i = a; i < b; i++)
        {
            sum += sound[i] * sound[i];
        }

        return Math.Sqrt(sum / (b - a));
    }
}
