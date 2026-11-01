using Mod2Xgm.Render.Psg;

namespace Mod2Xgm.Tests.Render.Psg;

/// <summary>Tests for <see cref="PsgLevels"/>: the PSG's volume levels, measured from Nuked-PSG (docs/adr/0067-psg-volume-levels.md).</summary>
public sealed class PsgLevelsTests
{
    /// <summary>
    /// The table is the levels Nuked-PSG plays in the <c>volumes</c> probe (tone 2 through all sixteen volumes, one
    /// channel at a time): every level it reaches, loudest first, with the loudest at MAME's 8191 instead of 8192.
    /// </summary>
    [Fact]
    public void LevelsAreThoseNukedPlays()
    {
        int[] measured = [.. Sn76489ProbeTests.Reference("volumes", "nuked").Distinct().OrderDescending()];
        Assert.Equal(16, measured.Length);
        Assert.Equal(8192, measured[0]);
        measured[0] = 8191;
        Assert.Equal(measured, PsgLevels.Channel.ToArray());
    }

    /// <summary>The loudest level is a quarter of the chip's full scale, so four loud channels reach it.</summary>
    [Fact]
    public void LoudestIsAQuarterOfFullScale() => Assert.Equal(Sn76489.FullScale, 4 * PsgLevels.Channel[0]);

    /// <summary>Every level is the whole step it belongs to, and the steps grow steeper towards the quiet end.</summary>
    [Fact]
    public void LevelsAreWholeStepsGrowingSteeper()
    {
        for (int volume = 0; volume < PsgLevels.Off; volume++)
        {
            Assert.Equal(volume, PsgLevels.Attenuation(PsgLevels.Gain(volume)), 1e-9);
            Assert.Equal(volume, PsgLevels.Nearest(PsgLevels.Gain(volume)));
        }

        double[] stepsDb = [.. Enumerable.Range(0, PsgLevels.Off - 1).Select(v => 20 * Math.Log10(PsgLevels.Gain(v) / PsgLevels.Gain(v + 1)))];
        Assert.InRange(stepsDb[0], 2.0, 2.5);
        Assert.InRange(stepsDb[^1], 6.0, 7.0);
        Assert.Equal(PsgLevels.Off, PsgLevels.Attenuation(0));
    }

    /// <summary>
    /// Between two levels the volume moves evenly in decibels, so the nearer level in decibels wins; louder than the
    /// loudest is 0, and past the quietest it rounds to off below half the quietest level.
    /// </summary>
    [Fact]
    public void NearestIsNearestInDecibels()
    {
        double middle = Math.Sqrt(PsgLevels.Gain(5) * PsgLevels.Gain(6));
        Assert.Equal(5.5, PsgLevels.Attenuation(middle), 1e-9);
        Assert.Equal(5, PsgLevels.Nearest(middle * 1.001));
        Assert.Equal(6, PsgLevels.Nearest(middle / 1.001));
        Assert.Equal(0, PsgLevels.Nearest(2));
        Assert.Equal(PsgLevels.Off - 1, PsgLevels.Nearest(PsgLevels.Gain(PsgLevels.Off - 1) * 0.51));
        Assert.Equal(PsgLevels.Off, PsgLevels.Nearest(PsgLevels.Gain(PsgLevels.Off - 1) * 0.49));
        Assert.Equal(PsgLevels.Off, PsgLevels.Nearest(-1));
    }
}
