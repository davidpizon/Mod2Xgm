using Mod2Xgm.Mapping;
using Mod2Xgm.Xgm;
using Serilog.Core;

namespace Mod2Xgm.Tests.Xgm;

/// <summary>
/// The acceptance check of docs/adr/0139-xgm2-frame-budget.md: files <see cref="Xgm2Writer"/> writes, decoded as SGDK's
/// driver plays them, leave the chips as the recorded song does on every sounding channel and render to the same samples.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Xgm2IntendedSongTests
{
    private const byte KeyOn = 0xF0;

    /// <summary>Patches, levels, frequencies through the shared latch, keys, a pan and a loop come out as recorded.</summary>
    [Fact]
    public void Write_ForPatchesNotesAndLoop_PlaysTheRecording()
    {
        // Arrange
        List<ChipEvent>[] frames = [.. Enumerable.Range(0, 30).Select(_ => new List<ChipEvent>())];
        frames[0].AddRange(Patch(0, 0, seed: 1));
        frames[0].AddRange([Fm(0, 0xA4, 0x22), Fm(0, 0xA0, 0x69), Fm(0, 0x28, KeyOn), ChipEvent.Psg(0x8E), ChipEvent.Psg(0x0F), ChipEvent.Psg(0x90)]);
        frames[5].Add(Fm(0, 0x4C, 0x10));
        frames[10].AddRange([Fm(0, 0xA4, 0x22), Fm(0, 0xA0, 0x80)]);
        frames[12].Add(Fm(0, 0x28, 0x00));
        frames[14].AddRange(Patch(0, 1, seed: 2));
        frames[14].AddRange([Fm(0, 0xA4, 0x1A), Fm(0, 0xA5, 0x2A), Fm(0, 0xA0, 0x10), Fm(0, 0xA1, 0x20)]);
        frames[15].AddRange([Fm(0, 0x28, KeyOn), Fm(0, 0x28, KeyOn | 1)]);
        frames[20].Add(Fm(0, 0xB4, 0x80));
        frames[22].Add(Fm(0, 0x4D, 0x08));
        frames[25].AddRange([Fm(0, 0x28, 0x00), Fm(0, 0x28, 0x01), ChipEvent.Psg(0x9F)]);
        GenesisStream stream = new([.. frames], LoopFrame: 2, Pal: false, PcmRateHz: 13_300);

        // Act
        Xgm2Output output = Xgm2Writer.Write(stream, new ConversionNotes(Logger.None));
        IntendedSongCheck sut = IntendedSong.Check(stream, output);

        // Assert
        Assert.True(sut.Same, sut.ToString());
        Assert.Equal(0, output.Stats.Splits);
    }

    /// <summary>
    /// A frame that would split, with six channels' patches before their key ons, plays without a split once the writer
    /// moves patch writes into the silent frames before it, and still plays the recording.
    /// </summary>
    [Fact]
    public void Write_ForFrameOverBudgetAfterSilence_MovesPatchesAndPlaysTheRecording()
    {
        // Arrange
        List<ChipEvent>[] frames = [.. Enumerable.Range(0, 40).Select(_ => new List<ChipEvent>())];
        for (int part = 0; part < 2; part++)
        {
            for (int index = 0; index < 3; index++)
            {
                frames[20].AddRange(Patch(part, index, seed: (part * 3) + index + 1).Where(e => e.Register < 0x90));
                frames[20].AddRange([Fm(part, (byte)(0xA4 + index), 0x22), Fm(part, (byte)(0xA0 + index), 0x69), Fm(0, 0x28, (byte)(KeyOn | (part * 4) + index))]);
            }
        }

        frames[30].AddRange(Enumerable.Range(0, 6).Select(c => Fm(0, 0x28, (byte)((c / 3 * 4) + (c % 3)))));
        GenesisStream stream = new([.. frames], LoopFrame: null, Pal: false, PcmRateHz: 13_300);

        // Act
        Xgm2Output output = Xgm2Writer.Write(stream, new ConversionNotes(Logger.None));
        IntendedSongCheck sut = IntendedSong.Check(stream, output);

        // Assert
        Assert.True(sut.Same, sut.ToString());
        Assert.True(output.Stats.Moved > 0, "nothing moved");
        Assert.Equal(0, output.Stats.Splits);
    }

    /// <summary>Corpus songs, recorded as a conversion records them, play as recorded.</summary>
    [Theory]
    [Trait("Speed", "Slow")]
    [InlineData("soundtracker15.mod")]
    [InlineData("cancan.mod")]
    [InlineData("hit.mod")]
    [InlineData("8bit_castle.mod")]
    public void Write_ForCorpusSong_PlaysTheRecording(string name)
    {
        // Arrange
        GenesisStream stream = IntendedSong.Record(File.ReadAllBytes(Path.Combine(RepoPaths.Root, "tests", "data", "corpus", name)));

        // Act
        Xgm2Output output = Xgm2Writer.Write(stream, new ConversionNotes(Logger.None));
        IntendedSongCheck sut = IntendedSong.Check(stream, output);

        // Assert
        Assert.True(output.Stats.Splits > 0 || sut.Same, sut.ToString());
    }

    /// <summary>
    /// A local song (never committed) plays as recorded: set <c>M2X_XGM2_ACCEPT_MOD</c> to its path, and
    /// <c>M2X_XGM2_ACCEPT_DAC</c> to the samples for <c>--require-dac</c> (comma-separated, 1-based).
    /// </summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Write_ForLocalSong_PlaysTheRecording()
    {
        // Arrange
        string? path = Environment.GetEnvironmentVariable("M2X_XGM2_ACCEPT_MOD");
        Assert.SkipWhen(string.IsNullOrEmpty(path) || !File.Exists(path), "M2X_XGM2_ACCEPT_MOD names no local MOD");
        int[] dac = [.. (Environment.GetEnvironmentVariable("M2X_XGM2_ACCEPT_DAC") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => int.Parse(s, System.Globalization.CultureInfo.InvariantCulture) - 1)];
        MappingOptions options = MappingOptions.Default with { RequireDac = new HashSet<int>(dac) };
        GenesisStream stream = IntendedSong.Record(File.ReadAllBytes(path), options);

        // Act
        Xgm2Output output = Xgm2Writer.Write(stream, new ConversionNotes(Logger.None));
        IntendedSongCheck sut = IntendedSong.Check(stream, output);

        // Assert
        Assert.True(sut.Same, $"{output.Stats.Splits} splits; {sut}");
    }

    /// <summary>An FM register write.</summary>
    private static ChipEvent Fm(int part, byte register, byte value) => ChipEvent.Fm(part, register, value);

    /// <summary>A whole patch for channel <paramref name="index"/> of <paramref name="part"/>: every slot register, then algorithm and pan.</summary>
    private static IEnumerable<ChipEvent> Patch(int part, int index, int seed)
    {
        for (int row = 0x30; row <= 0x90; row += 0x10)
        {
            for (int slot = 0; slot < 16; slot += 4)
            {
                int value = row switch
                {
                    0x40 => slot == 12 ? 0x04 : 0x20 + seed,
                    0x80 => 0x0F,
                    0x90 => 0,
                    _ => (row + slot + (seed * 7)) & 0x1F,
                };
                yield return Fm(part, (byte)(row + slot + index), (byte)value);
            }
        }

        yield return Fm(part, (byte)(0xB0 + index), (byte)(0x30 + seed % 8));
        yield return Fm(part, (byte)(0xB4 + index), 0xC0);
    }
}
