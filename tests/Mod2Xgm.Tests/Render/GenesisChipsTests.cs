using Mod2Xgm.Cli;
using Mod2Xgm.Render;

namespace Mod2Xgm.Tests.Render;

/// <summary>Tests for <see cref="GenesisChips"/>'s timing (docs/adr/0128-xgm2-real-frame-rate.md).</summary>
public sealed class GenesisChipsTests
{
    /// <summary>
    /// A frame is one VBlank: the master clock over 3420 clocks a line times 262 lines (NTSC) or 313 (PAL), a little
    /// under 60 and 50 Hz.
    /// </summary>
    [Theory]
    [InlineData(false, 59.9227)]
    [InlineData(true, 49.7015)]
    public void FrameRateHz_ForRegion_IsTheRealVBlankRate(bool pal, double expected)
    {
        // Act
        double sut = GenesisChips.FrameRateHz(pal ? Region.Pal : Region.Ntsc);

        // Assert
        Assert.Equal(expected, sut, 4);
    }
}
