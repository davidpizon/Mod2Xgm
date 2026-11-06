using Mod2Xgm.Dmf;
using Mod2Xgm.Render.Psg;

namespace Mod2Xgm.Tests.Dmf;

/// <summary>Tests for <see cref="DmfUnits"/>: volume steps, F-numbers, and the digit and byte limits of effect values.</summary>
public sealed class DmfUnitsTests
{
    /// <summary>FM volume is 0.75 dB a step from 7F: half volume (−6.02 dB) is 8 steps down; silence is 0.</summary>
    [Theory]
    [InlineData(1.0, 0x7F)]
    [InlineData(0.5, 0x7F - 8)]
    [InlineData(0.25, 0x7F - 16)]
    [InlineData(0.0, 0)]
    [InlineData(1e-9, 0)]
    public void FmVolume_WithAttenuation_UsesThreeQuarterDecibelSteps(double volume, int expected)
    {
        // Act & Assert
        Assert.Equal(expected, DmfUnits.FmVolume(volume));
    }

    /// <summary>
    /// PSG volume is the chip's nearest level counted down from F (docs/adr/0067-psg-volume-levels.md): half volume
    /// (−6.02 dB) is C (−6.28 dB) rather than D (−4.12 dB); below half the quietest level is 0.
    /// </summary>
    [Theory]
    [InlineData(1.0, 0xF)]
    [InlineData(0.5, 0xC)]
    [InlineData(0.0089 * 0.6, 1)]
    [InlineData(0.0089 * 0.4, 0)]
    [InlineData(0.0, 0)]
    public void PsgVolume_WithLevel_ReturnsNearestChipLevel(double volume, int expected)
    {
        // Act & Assert
        Assert.Equal(expected, DmfUnits.PsgVolume(volume));
    }

    /// <summary>The exact steps for a channel kind are unrounded.</summary>
    [Theory]
    [InlineData(false, 0x7F - 8.03)]
    [InlineData(true, 0xF - 2.88)]
    public void ExactVolume_WithLevel_IsUnrounded(bool psg, double expected)
    {
        // Act
        double volume = DmfUnits.ExactVolume(0.5, psg);

        // Assert
        Assert.Equal(expected, volume, 0.01);
    }

    /// <summary>On the PSG, the chip's levels are whole steps.</summary>
    [Fact]
    public void ExactVolume_WithPsgChipLevel_IsWholeStep()
    {
        // Act
        double volume = DmfUnits.ExactVolume(PsgLevels.Gain(3), psg: true);

        // Assert
        Assert.Equal(0xF - 3, volume, 1e-9);
    }

    /// <summary>A level past full scale is kept in range.</summary>
    [Fact]
    public void ExactVolume_PastFullScale_StaysInRange()
    {
        // Act
        double volume = DmfUnits.ExactVolume(2, psg: false);

        // Assert
        Assert.Equal(0x7F, volume);
    }

    /// <summary>The F-number of C is the same in every octave.</summary>
    [Theory]
    [InlineData(48)]
    [InlineData(-12)]
    public void Fnum_WithC_RepeatsEveryOctave(int semitone)
    {
        // Act
        double fnum = DmfUnits.Fnum(semitone);

        // Assert
        Assert.Equal(DmfUnits.FnumOfC, fnum, 1e-9);
    }

    /// <summary>An octave's B is just under twice its C.</summary>
    [Fact]
    public void Fnum_WithB_IsJustUnderTwiceC()
    {
        // Act
        double fnum = DmfUnits.Fnum(59);

        // Assert
        Assert.Equal(DmfUnits.FnumOfC * Math.Pow(2, 11.0 / 12), fnum, 1e-9);
    }

    /// <summary>A slide's F-number distance is its change in log-frequency times the F-number halfway along.</summary>
    [Theory]
    [InlineData(1, 48.5)]
    [InlineData(-1, 47.5)]
    public void FnumDistance_WithSemitone_MeasuresHalfwayAlong(int semitones, double halfway)
    {
        // Act
        double distance = DmfUnits.FnumDistance(48, semitones);

        // Assert
        Assert.Equal(DmfUnits.Fnum(halfway) * Math.Log(2) / 12, distance, 1e-9);
    }

    /// <summary>Sliding down from C is measured in the octave below, where F-numbers are nearly twice as large.</summary>
    [Fact]
    public void FnumDistance_DownFromC_IsNearlyTwiceUp()
    {
        // Act
        double down = DmfUnits.FnumDistance(48, -1);

        // Assert
        Assert.True(down > 1.8 * DmfUnits.FnumDistance(48, 1));
    }

    /// <summary>Half the period is 12 semitones up.</summary>
    [Fact]
    public void Semitones_WithPeriods_ReturnsDistance()
    {
        // Act
        double semitones = DmfUnits.Semitones(428, 214);

        // Assert
        Assert.Equal(12, semitones, 1e-9);
    }

    /// <summary>A step past the period is no change.</summary>
    [Fact]
    public void Semitones_WithStepPastPeriod_ReturnsZero()
    {
        // Act
        double semitones = DmfUnits.Semitones(10, 20);

        // Assert
        Assert.Equal(0, semitones);
    }

    /// <summary>Digits are 1–15 once above 0.</summary>
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.2, 1)]
    [InlineData(7.5, 8)]
    [InlineData(40, 15)]
    [InlineData(400, 15)]
    public void Digit_WithValue_IsClamped(double value, int expected)
    {
        // Act
        int digit = DmfUnits.Digit(value);

        // Assert
        Assert.Equal(expected, digit);
    }

    /// <summary>Byte values are 1–255 once above 0.</summary>
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.2, 1)]
    [InlineData(7.5, 8)]
    [InlineData(40, 40)]
    [InlineData(400, 255)]
    public void ByteValue_WithValue_IsClamped(double value, int expected)
    {
        // Act
        int result = DmfUnits.ByteValue(value);

        // Assert
        Assert.Equal(expected, result);
    }
}
