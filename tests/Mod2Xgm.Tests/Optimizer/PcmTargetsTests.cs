using Mod2Xgm.Optimizer;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="PcmTargets"/>: each target's caps, rates, bytes and names.</summary>
public sealed class PcmTargetsTests
{
    /// <summary>The hard caps are the ones docs/sample-capacity.md lists.</summary>
    [Fact]
    public void TheCapsAreTheTargets()
    {
        Assert.Equal([144, 63, 122], PcmTargets.All.Select(PcmTargets.Slots));
        Assert.Equal([1, 4, 3], PcmTargets.All.Select(PcmTargets.Voices));
    }

    /// <summary>DMF plays at the lowest rate code at or above the note's; XGM at about 14 kHz; XGM2 at 13.3 kHz, or half that.</summary>
    [Fact]
    public void EachTargetHasItsRate()
    {
        Assert.Equal(11025, PcmTargets.PlayRateHz(PcmTarget.Dmf, 8287, false));
        Assert.Equal(8000, PcmTargets.PlayRateHz(PcmTarget.Dmf, 8000, false));
        Assert.InRange(PcmTargets.PlayRateHz(PcmTarget.Xgm, 8287, false), 13_900, 14_100);
        Assert.InRange(PcmTargets.PlayRateHz(PcmTarget.Xgm2, 8287, false), 13_300, 13_330);
        Assert.Equal(PcmTargets.PlayRateHz(PcmTarget.Xgm2, 8287, false) / 2, PcmTargets.PlayRateHz(PcmTarget.Xgm2, 8287, true), 9);
    }

    /// <summary>DMF stores 2 bytes a point; XGM and XGM2 one, in 256-byte blocks.</summary>
    [Fact]
    public void BytesFollowTheStorage()
    {
        Assert.Equal(200, PcmTargets.Bytes(PcmTarget.Dmf, 100));
        Assert.Equal(256, PcmTargets.Bytes(PcmTarget.Xgm, 1));
        Assert.Equal(512, PcmTargets.Bytes(PcmTarget.Xgm2, 257));
        Assert.Equal(0, PcmTargets.Bytes(PcmTarget.Xgm, 0));
    }

    /// <summary>Targets and savings have short names for logs and CSVs.</summary>
    [Fact]
    public void NamesAreShort()
    {
        Assert.Equal(["dmf", "xgm", "xgm2"], PcmTargets.All.Select(PcmTargets.Name));
        Assert.Equal("none", PcmTargets.Name(PcmSavings.None));
        Assert.Equal("half speed+semitones+short loops+trim tail", PcmTargets.Name(PcmSavings.HalfSpeed | PcmSavings.Semitones | PcmSavings.ShortLoops | PcmSavings.TrimTail));
    }
}
