using Mod2Xgm.Optimizer;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="PcmTargets"/>: each target's caps, rates, bytes and names.</summary>
public sealed class PcmTargetsTests
{
    /// <summary>The slot caps are the ones docs/sample-capacity.md lists.</summary>
    [Fact]
    public void Slots_ForTarget_MatchesCaps()
    {
        // Act
        IEnumerable<int> slots = PcmTargets.All.Select(PcmTargets.Slots);

        // Assert
        Assert.Equal([144, 63, 122], slots);
    }

    /// <summary>The voice caps are the ones docs/sample-capacity.md lists.</summary>
    [Fact]
    public void Voices_ForTarget_MatchesCaps()
    {
        // Act
        IEnumerable<int> voices = PcmTargets.All.Select(PcmTargets.Voices);

        // Assert
        Assert.Equal([1, 4, 3], voices);
    }

    /// <summary>DMF plays at the lowest rate code at or above the note's.</summary>
    [Theory]
    [InlineData(8287, 11025)]
    [InlineData(8000, 8000)]
    public void PlayRateHz_ForDmf_ReturnsLowestRateCodeAbove(double noteHz, double expected)
    {
        // Act
        double rate = PcmTargets.PlayRateHz(PcmTarget.Dmf, noteHz, false);

        // Assert
        Assert.Equal(expected, rate);
    }

    /// <summary>XGM plays at about 14 kHz.</summary>
    [Fact]
    public void PlayRateHz_ForXgm_IsAbout14Khz()
    {
        // Act
        double rate = PcmTargets.PlayRateHz(PcmTarget.Xgm, 8287, false);

        // Assert
        Assert.InRange(rate, 13_900, 14_100);
    }

    /// <summary>XGM2 plays at 13.3 kHz.</summary>
    [Fact]
    public void PlayRateHz_ForXgm2_IsAbout13Khz()
    {
        // Act
        double rate = PcmTargets.PlayRateHz(PcmTarget.Xgm2, 8287, false);

        // Assert
        Assert.InRange(rate, 13_300, 13_330);
    }

    /// <summary>XGM2 at half speed plays at half that.</summary>
    [Fact]
    public void PlayRateHz_ForXgm2AtHalfSpeed_IsHalf()
    {
        // Act
        double rate = PcmTargets.PlayRateHz(PcmTarget.Xgm2, 8287, true);

        // Assert
        Assert.Equal(PcmTargets.PlayRateHz(PcmTarget.Xgm2, 8287, false) / 2, rate, 9);
    }

    /// <summary>DMF stores 2 bytes a point; XGM and XGM2 one, in 256-byte blocks.</summary>
    [Theory]
    [InlineData(nameof(PcmTarget.Dmf), 100, 200)]
    [InlineData(nameof(PcmTarget.Xgm), 1, 256)]
    [InlineData(nameof(PcmTarget.Xgm2), 257, 512)]
    [InlineData(nameof(PcmTarget.Xgm), 0, 0)]
    public void Bytes_ForTarget_FollowsStorage(string target, int points, int expected)
    {
        // Act
        int bytes = PcmTargets.Bytes(Enum.Parse<PcmTarget>(target), points);

        // Assert
        Assert.Equal(expected, bytes);
    }

    /// <summary>Targets have short names for logs and CSVs.</summary>
    [Fact]
    public void Name_ForTarget_IsShort()
    {
        // Act
        IEnumerable<string> names = PcmTargets.All.Select(PcmTargets.Name);

        // Assert
        Assert.Equal(["dmf", "xgm", "xgm2"], names);
    }

    /// <summary>No savings is named "none".</summary>
    [Fact]
    public void Name_ForNoSavings_IsNone()
    {
        // Act
        string name = PcmTargets.Name(PcmSavings.None);

        // Assert
        Assert.Equal("none", name);
    }

    /// <summary>Several savings are named in order, joined by a plus.</summary>
    [Fact]
    public void Name_ForEverySaving_JoinsThem()
    {
        // Act
        string name = PcmTargets.Name(PcmSavings.HalfSpeed | PcmSavings.Semitones | PcmSavings.ShortLoops | PcmSavings.TrimTail);

        // Assert
        Assert.Equal("half speed+semitones+short loops+trim tail", name);
    }
}
