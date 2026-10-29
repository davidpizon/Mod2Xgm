using Mod2Xgm.Dmf;

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
    public void FmVolumeIsInThreeQuarterDecibelSteps(double volume, int expected) => Assert.Equal(expected, DmfUnits.FmVolume(volume));

    /// <summary>PSG volume is 2 dB a step from F: half volume is 3 steps down.</summary>
    [Theory]
    [InlineData(1.0, 0xF)]
    [InlineData(0.5, 0xF - 3)]
    [InlineData(0.0, 0)]
    public void PsgVolumeIsInTwoDecibelSteps(double volume, int expected) => Assert.Equal(expected, DmfUnits.PsgVolume(volume));

    /// <summary>The exact steps for a channel kind are unrounded and kept in range.</summary>
    [Fact]
    public void ExactVolumeIsUnrounded()
    {
        Assert.Equal(0x7F - 8.03, DmfUnits.ExactVolume(0.5, psg: false), 0.01);
        Assert.Equal(0xF - 3.01, DmfUnits.ExactVolume(0.5, psg: true), 0.01);
        Assert.Equal(0x7F, DmfUnits.ExactVolume(2, psg: false));
    }

    /// <summary>The F-number of C is the same in every octave; an octave's B is just under twice it.</summary>
    [Fact]
    public void FnumRepeatsEveryOctave()
    {
        Assert.Equal(DmfUnits.FnumOfC, DmfUnits.Fnum(48), 1e-9);
        Assert.Equal(DmfUnits.FnumOfC, DmfUnits.Fnum(-12), 1e-9);
        Assert.Equal(DmfUnits.FnumOfC * Math.Pow(2, 11.0 / 12), DmfUnits.Fnum(59), 1e-9);
    }

    /// <summary>
    /// A slide's F-number distance is its change in log-frequency times the F-number halfway along; sliding down from C is
    /// measured in the octave below, where F-numbers are nearly twice as large.
    /// </summary>
    [Fact]
    public void FnumDistanceIsMeasuredHalfwayAlong()
    {
        Assert.Equal(DmfUnits.Fnum(48.5) * Math.Log(2) / 12, DmfUnits.FnumDistance(48, 1), 1e-9);
        Assert.Equal(DmfUnits.Fnum(47.5) * Math.Log(2) / 12, DmfUnits.FnumDistance(48, -1), 1e-9);
        Assert.True(DmfUnits.FnumDistance(48, -1) > 1.8 * DmfUnits.FnumDistance(48, 1));
    }

    /// <summary>Semitones between periods; a step past the period is no change.</summary>
    [Fact]
    public void SemitonesBetweenPeriods()
    {
        Assert.Equal(12, DmfUnits.Semitones(428, 214), 1e-9);
        Assert.Equal(0, DmfUnits.Semitones(10, 20));
    }

    /// <summary>Digits are 1–15 once above 0; byte values 1–255.</summary>
    [Theory]
    [InlineData(0.0, 0, 0)]
    [InlineData(0.2, 1, 1)]
    [InlineData(7.5, 8, 8)]
    [InlineData(40, 15, 40)]
    [InlineData(400, 15, 255)]
    public void DigitsAndBytesAreClamped(double value, int digit, int byteValue)
    {
        Assert.Equal(digit, DmfUnits.Digit(value));
        Assert.Equal(byteValue, DmfUnits.ByteValue(value));
    }

    /// <summary>Decibels become steps of 0.75 dB on FM and 2 dB on the PSG.</summary>
    [Fact]
    public void DecibelsToSteps()
    {
        Assert.Equal(8, DmfUnits.DbToSteps(6, psg: false), 1e-9);
        Assert.Equal(3, DmfUnits.DbToSteps(6, psg: true), 1e-9);
    }
}
