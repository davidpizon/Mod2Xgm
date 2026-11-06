using Mod2Xgm.Mapping;
using Mod2Xgm.Model;

namespace Mod2Xgm.Tests.Mapping;

/// <summary>Tests for <see cref="VoiceLevel"/>: sample levels and FM patch levels on the DAC's scale (ADR 0050).</summary>
public sealed class VoiceLevelTests
{
    /// <summary>A looped sample's level is its loop's RMS, whatever comes before the loop.</summary>
    [Fact]
    public void Sample_WithLoop_MeasuresLoop()
    {
        // Arrange
        float[] data = [.. Enumerable.Repeat(1f, 64), .. Sine(64, 0.5)];

        // Act
        double level = VoiceLevel.Sample(new Sample("held", data, new SampleLoop(64, 64)));

        // Assert
        Assert.Equal(0.5 / Math.Sqrt(2), level, 3);
    }

    /// <summary>A one-shot's level is its loudest window: a hit that fades is as loud as its start.</summary>
    [Fact]
    public void Sample_WithOneShot_MeasuresLoudestWindow()
    {
        // Arrange
        Sample hit = new("hit", [.. Sine(256, 0.8), .. Sine(2048, 0.1)], null);

        // Act
        double level = VoiceLevel.Sample(hit);

        // Assert
        Assert.Equal(0.8 / Math.Sqrt(2), level, 2);
        Assert.Equal(VoiceLevel.Loudest(hit), level);
    }

    /// <summary>A short sample is measured whole.</summary>
    [Fact]
    public void Loudest_WithShortSample_MeasuresItWhole()
    {
        // Act
        double level = VoiceLevel.Loudest(new Sample("short", [0.25f, -0.25f, 0.25f, -0.25f], null));

        // Assert
        Assert.Equal(0.25, level, 6);
    }

    /// <summary>An empty sample has no level.</summary>
    [Fact]
    public void Loudest_WithEmptySample_ReturnsZero()
    {
        // Act
        double level = VoiceLevel.Loudest(new Sample("empty", [], null));

        // Assert
        Assert.Equal(0, level);
    }

    /// <summary>The loudest level finds an attack louder than the loop.</summary>
    [Fact]
    public void Loudest_WithLouderAttack_IgnoresLoop()
    {
        // Arrange
        Sample sample = new("attack and loop", [.. Sine(512, 1.0), .. Sine(512, 0.25)], new SampleLoop(512, 512));

        // Act
        double level = VoiceLevel.Loudest(sample);

        // Assert
        Assert.Equal(1 / Math.Sqrt(2), level, 2);
    }

    /// <summary>A loop's level ignores a louder attack.</summary>
    [Fact]
    public void Sample_WithLouderAttack_IgnoresAttack()
    {
        // Arrange
        Sample sample = new("attack and loop", [.. Sine(512, 1.0), .. Sine(512, 0.25)], new SampleLoop(512, 512));

        // Act
        double level = VoiceLevel.Sample(sample);

        // Assert
        Assert.Equal(0.25 / Math.Sqrt(2), level, 2);
    }

    /// <summary>The Sine patch is one carrier at TL 0: the level of a full-scale sine, peaking at full scale.</summary>
    [Fact]
    public void Fm_WithSinePatch_ReturnsFullScaleSine()
    {
        // Act
        (double rms, double peak) = VoiceLevel.Fm(FmPatchLibrary.Sine, sustained: true);

        // Assert
        Assert.Equal(1 / Math.Sqrt(2), rms, 9);
        Assert.Equal(1, peak, 9);
    }

    /// <summary>Carriers' powers add: the Organ's four carriers at TL 22, 34, 44 and 22.</summary>
    [Fact]
    public void Fm_WithSeveralCarriers_AddsPowers()
    {
        // Arrange
        double[] amplitudes = [.. new[] { 22, 34, 44, 22 }.Select(tl => Math.Pow(10, -0.75 * tl / 20))];

        // Act
        (double rms, double peak) = VoiceLevel.Fm(FmPatchLibrary.Organ, sustained: false);

        // Assert
        Assert.Equal(Math.Sqrt(amplitudes.Sum(a => a * a / 2)), rms, 9);
        Assert.Equal(amplitudes.Sum(), peak, 9);
    }

    /// <summary>A held Brass note settles 12 dB (SL 4) below its attack; its carriers' peak is over full scale.</summary>
    [Fact]
    public void Fm_WithSustainedNote_SettlesAtSustainLevel()
    {
        // Arrange
        double attack = VoiceLevel.Fm(FmPatchLibrary.Brass, sustained: false).Rms;

        // Act
        (double held, double peak) = VoiceLevel.Fm(FmPatchLibrary.Brass, sustained: true);

        // Assert
        Assert.Equal(-12, 20 * Math.Log10(held / attack), 6);
        Assert.True(peak > 1, $"peak {peak}");
    }

    /// <summary>A carrier that decays to SL 15 counts as silent when held, as does one at TL 127.</summary>
    [Fact]
    public void Fm_WithHeldNoteAtSustainLevel15_IsSilent()
    {
        // Act
        double rms = VoiceLevel.Fm(FadingPatch(), sustained: true).Rms;

        // Assert
        Assert.Equal(0, rms);
    }

    /// <summary>A carrier that decays to SL 15 still sounds at its attack.</summary>
    [Fact]
    public void Fm_WithAttackAtSustainLevel15_Sounds()
    {
        // Act
        double rms = VoiceLevel.Fm(FadingPatch(), sustained: false).Rms;

        // Assert
        Assert.Equal(1 / Math.Sqrt(2), rms, 3);
    }

    /// <summary>Which operators are carriers under each algorithm (Yamaha order OP1–OP4).</summary>
    [Theory]
    [InlineData(0, new[] { 3 })]
    [InlineData(3, new[] { 3 })]
    [InlineData(4, new[] { 1, 3 })]
    [InlineData(5, new[] { 1, 2, 3 })]
    [InlineData(6, new[] { 1, 2, 3 })]
    [InlineData(7, new[] { 0, 1, 2, 3 })]
    public void Carriers_WithAlgorithm_ReturnsItsOperators(int algorithm, int[] expected)
    {
        // Act
        IEnumerable<int> carriers = VoiceLevel.Carriers(algorithm);

        // Assert
        Assert.Equal(expected, carriers);
    }

    /// <summary>A sine of <paramref name="amplitude"/> with <paramref name="cycle"/> frames a cycle.</summary>
    private static float[] Sine(int frames, double amplitude, int cycle = 32) =>
        [.. Enumerable.Range(0, frames).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * i / cycle)))];

    /// <summary>A patch with one carrier that decays to SL 15 and three at TL 127.</summary>
    private static FmPatch FadingPatch()
    {
        FmOperator fading = new(1, 0, 0, 0, 31, false, 10, 0, 15, 8);
        FmOperator mute = new(1, 0, 127, 0, 31, false, 0, 0, 0, 8);
        return new FmPatch("fade", 7, 0, [fading, mute, mute, mute]);
    }
}
