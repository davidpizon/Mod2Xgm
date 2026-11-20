using Mod2Xgm.Dmf;
using Mod2Xgm.Xgm;

namespace Mod2Xgm.Tests.Dmf.Sanity;

/// <summary>
/// The DMF sanity check's paths on the committed DefleMask probes (<c>tests/golden/dmf-probes/</c>, with DefleMask's VGM
/// exports in <c>vgm/</c>), so its pieces are checked in CI, where the local songs are missing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SanityPathTests
{
    private static string Probe(string name, string extension) =>
        Path.Combine(RepoPaths.Root, "tests", "golden", "dmf-probes", extension == ".vgm" ? "vgm" : "", name + extension);

    private static ChipLog Vgm(string name) => ChipLog.FromVgm(VgmLog.Read(File.ReadAllBytes(Probe(name, ".vgm"))));

    /// <summary>FM writes, a key-on and a PSG tone go through the XGM2 writer and come back from the decoder unchanged.</summary>
    [Fact]
    public void Decode_ForWrittenLog_ReturnsSameState()
    {
        // Arrange
        ChipLog log = new(
            [
                [ChipWrite.Fm(0, 0xA4, 0x22), ChipWrite.Fm(0, 0xA0, 0x69), ChipWrite.Fm(0, 0x28, 0xF0), ChipWrite.Psg(0x8E), ChipWrite.Psg(0x0F), ChipWrite.Psg(0x90)],
                [],
                [ChipWrite.Fm(0, 0x28, 0x00), ChipWrite.Psg(0x9F)],
            ],
            LoopFrame: null,
            FrameRateHz: 60);
        Xgm2Output output = Xgm2Writer.Write(log.ToXgm2Stream(pal: false), new ConversionNotes(new CollectingSink().CreateLogger()));

        // Act
        ChipLog sut = Xgm2Decoder.Decode(output.Bytes, output.Samples);

        // Assert
        Assert.Equal(0, LogComparer.CompareState(log, sut).FramesDiffering);
    }

    /// <summary>Control path: DefleMask's own writes, through the XGM2 writer and back, leave the chips as the VGM does.</summary>
    [Theory]
    [InlineData("fm-note")]
    [InlineData("psg-note")]
    [InlineData("ext-ch3")]
    [InlineData("custom-hz")]
    public void Decode_ForProbeVgmThroughWriter_MatchesVgmState(string probe)
    {
        // Arrange
        ChipLog vgm = Vgm(probe);
        Xgm2Output output = Xgm2Writer.Write(vgm.ToXgm2Stream(pal: false), new ConversionNotes(new CollectingSink().CreateLogger()));

        // Act
        ChipLog sut = Xgm2Decoder.Decode(output.Bytes, output.Samples);

        // Assert
        Assert.Equal(0, LogComparer.CompareState(vgm, sut).FramesDiffering);
    }

    /// <summary>The test's DefleMask model plays each probe as DefleMask's VGM export does, register for register.</summary>
    [Theory]
    [InlineData("fm-note")]
    [InlineData("psg-note")]
    [InlineData("ext-ch3")]
    [InlineData("custom-hz")]
    [InlineData("dac-sample")]
    public void Play_ForProbe_MatchesVgmState(string probe)
    {
        // Arrange
        DmfFile file = DmfReader.Read(File.ReadAllBytes(Probe(probe, ".dmf")), lenient: true);

        // Act
        DmfPlayback sut = DmfPlayer.Play(file);

        // Assert
        Assert.Empty(LogComparer.CompareState(Vgm(probe), sut.Log).Examples);
    }
}
