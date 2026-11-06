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
    public void Render_WithTone_SoundsAtPeriod()
    {
        // Arrange
        PsgSetting tone = new(PsgSound.Tone, 0, 254, Loud);

        // Act
        float[] sound = PsgNoteRenderer.Render(tone, 1);

        // Assert
        Assert.InRange(sound.Length, SampleAnalyzer.AnalysisRateHz - 1, SampleAnalyzer.AnalysisRateHz);
        Assert.Equal(PsgSetting.ToneFrequency(254), CyclesPerSecond(sound), 1.5);
        Assert.InRange(sound.Skip(4410).Max(), 0.9, 1.25);
    }

    /// <summary>Periodic noise clocked by tone channel 3 buzzes a sixteenth below it: Sega's 16-bit shift register.</summary>
    [Fact]
    public void Render_WithPeriodicNoise_BuzzesSixteenthBelowClock()
    {
        // Arrange
        PsgSetting buzz = new(PsgSound.PeriodicNoise, PsgSetting.Tone3Rate, 40, Loud);

        // Act
        float[] sound = PsgNoteRenderer.Render(buzz, 1);

        // Assert
        Assert.Equal(buzz.PitchHz!.Value, CyclesPerSecond(sound), 1.5);
        Assert.Equal(PsgSetting.ToneFrequency(40) / 16, buzz.PitchHz.Value, 1e-9);
    }

    /// <summary>At a fixed rate, periodic noise buzzes at a sixteenth of the shift rate, clock / 512 at rate 0.</summary>
    [Fact]
    public void Render_WithFixedRatePeriodicNoise_BuzzesAtRate()
    {
        // Act
        PsgSetting buzz = new(PsgSound.PeriodicNoise, 0, 0, Loud);

        // Assert
        Assert.Equal(Sn76489.NtscClockHz / 512 / 16, buzz.PitchHz!.Value, 1e-9);
        Assert.Equal(buzz.PitchHz.Value, CyclesPerSecond(PsgNoteRenderer.Render(buzz, 1)), 1.5);
    }

    /// <summary>White noise is no tone: it crosses zero far more often than its shift rate's sixteenth, and has no pitch.</summary>
    [Fact]
    public void Render_WithWhiteNoise_HasNoPitch()
    {
        // Act
        PsgSetting hiss = new(PsgSound.WhiteNoise, 0, 0, Loud);

        // Assert
        Assert.Null(hiss.PitchHz);
        Assert.True(CyclesPerSecond(PsgNoteRenderer.Render(hiss, 1)) > 1000);
    }

    /// <summary>The volume follows the steps: off after the first half second, the sound dies away.</summary>
    [Fact]
    public void Render_WithVolume_FollowsSteps()
    {
        // Arrange
        int[] halfSecond = [.. Enumerable.Range(0, PsgSetting.StepsPerSecond).Select(s => s < 30 ? 0 : PsgLevels.Off)];

        // Act
        float[] sound = PsgNoteRenderer.Render(new PsgSetting(PsgSound.Tone, 0, 254, halfSecond), 1);

        // Assert
        Assert.True(Rms(sound, 0.1, 0.45) > 0.9);
        Assert.True(Rms(sound, 0.7, 1) < 0.01);
    }

    /// <summary>A tone takes one channel.</summary>
    [Fact]
    public void Channels_WithTone_IsOne()
    {
        // Act
        int channels = new PsgSetting(PsgSound.Tone, 0, 100, Loud).Channels;

        // Assert
        Assert.Equal(1, channels);
    }

    /// <summary>Noise at a fixed rate takes one channel.</summary>
    [Fact]
    public void Channels_WithFixedRateNoise_IsOne()
    {
        // Act
        int channels = new PsgSetting(PsgSound.WhiteNoise, 2, 0, Loud).Channels;

        // Assert
        Assert.Equal(1, channels);
    }

    /// <summary>Noise clocked by tone channel 3 takes two channels.</summary>
    [Fact]
    public void Channels_WithNoiseOnTone3_IsTwo()
    {
        // Act
        int channels = new PsgSetting(PsgSound.WhiteNoise, PsgSetting.Tone3Rate, 100, Loud).Channels;

        // Assert
        Assert.Equal(2, channels);
    }

    /// <summary>A tone has no shift rate.</summary>
    [Fact]
    public void ShiftHz_WithTone_IsNull()
    {
        // Act
        double? hz = new PsgSetting(PsgSound.Tone, 0, 100, Loud).ShiftHz;

        // Assert
        Assert.Null(hz);
    }

    /// <summary>Noise at a fixed rate has no tone rate.</summary>
    [Fact]
    public void ToneHz_WithFixedRateNoise_IsNull()
    {
        // Act
        double? hz = new PsgSetting(PsgSound.WhiteNoise, 1, 0, Loud).ToneHz;

        // Assert
        Assert.Null(hz);
    }

    /// <summary>Noise rate 1 shifts at the PSG clock over 1024.</summary>
    [Fact]
    public void ShiftHz_WithFixedRateNoise_FollowsClock()
    {
        // Act
        double? hz = new PsgSetting(PsgSound.WhiteNoise, 1, 0, Loud).ShiftHz;

        // Assert
        Assert.Equal(Sn76489.NtscClockHz / 1024, hz ?? double.NaN, 1e-9);
    }

    /// <summary>The nearest period to 440 Hz is 254.</summary>
    [Fact]
    public void NearestPeriod_WithA4_Returns254()
    {
        // Act
        int period = PsgSetting.NearestPeriod(440);

        // Assert
        Assert.Equal(254, period);
    }

    /// <summary>A note too low for a tone comes back past the highest period, so callers can tell.</summary>
    [Fact]
    public void NearestPeriod_BelowToneRange_IsPastMaxPeriod()
    {
        // Act
        int period = PsgSetting.NearestPeriod(55);

        // Assert
        Assert.True(period > PsgSetting.MaxPeriod);
    }

    /// <summary>A try with no steps is silent.</summary>
    [Fact]
    public void Render_WithNoSteps_IsSilent()
    {
        // Act
        float[] output = PsgNoteRenderer.Render(new PsgSetting(PsgSound.Tone, 0, 254, []), 0.1);

        // Assert
        Assert.All(output, v => Assert.Equal(0, v));
    }

    /// <summary>A null try is refused.</summary>
    [Fact]
    public void Render_WithNullSetting_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => PsgNoteRenderer.Render(null!, 1)); // null on purpose, past the annotation
    }

    /// <summary>A length of 0 is refused.</summary>
    [Fact]
    public void Render_WithZeroLength_Throws()
    {
        // Act & Assert
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
