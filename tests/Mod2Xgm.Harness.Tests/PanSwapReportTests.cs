using Mod2Xgm.Render;

namespace Mod2Xgm.Harness.Tests;

/// <summary>Tests for the tables <c>--pan-swaps</c> writes (plan T9.4r, docs/adr/0108-pan-swap-count.md).</summary>
public sealed class PanSwapReportTests
{
    /// <summary>A version of song.mod with one shared-DAC swap of sample 3 away from the left, and a sample steadily off side.</summary>
    private static PanSwapVersion Version(string format = "xgm2", string mapping = "optimizer") => new(
        "song.mod",
        format,
        mapping,
        new PanSwapCount(
            [new PanSwap(2, 1.5, -1, -1, 0, PanCause.SharedDac, 2.25, 0.5, 0.75, false)],
            [new PanSampleSummary(2, 4, 2.25, 1), new PanSampleSummary(4, 3, 3, 0)]),
        ["kick", "snare", "hat, open", "bass", "pad"]);

    /// <summary>Each swap is one CSV line with its song, version, 1-based sample, quoted name, sides as letters and cause.</summary>
    [Fact]
    public void Csv_WithOneSwap_WritesItsLine()
    {
        // Act
        string sut = PanSwapReport.Csv([Version()]);

        // Assert
        Assert.Equal(
            PanSwapReport.CsvHeader + "\nsong.mod,xgm2,optimizer,3,\"hat, open\",1.5,L,L,C,away,1,shared-dac,2.25,0.5,0.75,no\n",
            sut);
    }

    /// <summary>The totals count each format and mapper's swaps by cause, and the version line lists the sample steadily off side.</summary>
    [Fact]
    public void Markdown_WithOneSwap_CountsItByCause()
    {
        // Act
        string sut = PanSwapReport.Markdown([Version(), Version(mapping: "simple") with { Count = new PanSwapCount([], []) }]);

        // Assert
        Assert.Contains("| xgm2 | optimizer | 1 | 1 | 0 | 0 | 0 | 1 of 1 | 1 | 1 |", sut, StringComparison.Ordinal);
        Assert.Contains("| xgm2 | simple | 0 | 0 | 0 | 0 | 0 | 0 of 1 | 0 | 0 |", sut, StringComparison.Ordinal);
        Assert.Contains("| song.mod | xgm2 | optimizer | 1 | 1 | 2.25 | 0.75 | 5 |", sut, StringComparison.Ordinal);
        Assert.Contains("| song.mod | xgm2 | simple | 0 | 0 | – | – | – |", sut, StringComparison.Ordinal);
    }

    /// <summary>Sides, causes and kinds have the words the CSV uses.</summary>
    [Fact]
    public void Words_ForEachValue_AreTheCsvWords()
    {
        // Act
        string[] sut =
        [
            PanSwapReport.Side(-1), PanSwapReport.Side(0), PanSwapReport.Side(1),
            PanSwapReport.Cause(PanCause.None), PanSwapReport.Cause(PanCause.PreMix), PanSwapReport.Cause(PanCause.CentredBass), PanSwapReport.Cause(PanCause.Psg),
            PanSwapReport.Kind(PanSwapKind.Back), PanSwapReport.Kind(PanSwapKind.Across),
        ];

        // Assert
        Assert.Equal(["L", "C", "R", "none", "pre-mix", "centred-bass", "psg", "back", "across"], sut);
    }
}
