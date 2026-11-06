using Mod2Xgm.Cli;
using Mod2Xgm.Mod;
using Serilog.Core;

namespace Mod2Xgm.Harness.Tests;

/// <summary>Tests for <see cref="StemSource"/>: one sample alone from the MOD and from the conversion (docs/adr/0051-instrument-balance-metric.md).</summary>
[Trait("Category", "Integration")]
public sealed class StemsTests
{
    /// <summary>The song converted with the default options, as the harness does, and the source made from it.</summary>
    private static (byte[] Mod, ModModule Module, StemSource Source) Prepare(TempDirectory temp)
    {
        string dmf = temp.Combine("song.dmf");
        ConversionCommand.Execute(new ConversionOptions { InputPath = TestFiles.ShortSong, OutputPath = dmf, Force = true }, Logger.None);
        byte[] mod = File.ReadAllBytes(TestFiles.ShortSong);
        return (mod, ModReader.Read(mod, new ConversionNotes(Logger.None)), StemSource.Prepare(mod, File.ReadAllBytes(dmf)));
    }

    /// <summary>The MOD stem keeps the sample's data and the patterns, and zeroes every other sample's data.</summary>
    [Fact]
    public void SoloMod_WithSample_ZeroesEveryOtherSample()
    {
        // Arrange
        using TempDirectory temp = new();
        (byte[] mod, ModModule module, StemSource source) = Prepare(temp);
        int sample = source.SamplesIn(source.Orders)[0];

        // Act
        byte[] solo = source.SoloMod(sample);

        // Assert
        ModSample kept = module.Samples[sample];
        IEnumerable<ModSample> others = module.Samples.Where((s, i) => i != sample && !s.IsEmpty);
        Assert.Equal(mod.Length, solo.Length);
        Assert.Equal(mod.AsSpan(0, module.Samples[0].DataOffset).ToArray(), solo.AsSpan(0, module.Samples[0].DataOffset).ToArray());
        Assert.Equal(mod.AsSpan(kept.DataOffset, kept.Length).ToArray(), solo.AsSpan(kept.DataOffset, kept.Length).ToArray());
        Assert.All(others, other => Assert.All(solo.AsSpan(other.DataOffset, other.Length).ToArray(), b => Assert.Equal(0, b)));
    }

    /// <summary>The samples heard are the non-empty ones the patterns name; fewer orders hear no more samples.</summary>
    [Fact]
    public void SamplesIn_WithOrders_ReturnsSamplesHeard()
    {
        // Arrange
        using TempDirectory temp = new();
        (_, ModModule module, StemSource source) = Prepare(temp);

        // Act
        IReadOnlyList<int> samples = source.SamplesIn(source.Orders);

        // Assert
        Assert.NotEmpty(samples);
        Assert.All(samples, s => Assert.False(module.Samples[s].IsEmpty));
        Assert.True(source.SamplesIn(1).Count <= samples.Count);
    }

    /// <summary>Each sample heard has a voice in the conversion, and its name.</summary>
    [Fact]
    public void Voices_ForSamplesHeard_NamesVoiceEach()
    {
        // Arrange
        using TempDirectory temp = new();
        (_, ModModule module, StemSource source) = Prepare(temp);

        // Act
        IReadOnlyList<int> samples = source.SamplesIn(source.Orders);

        // Assert
        Assert.All(samples, sample =>
        {
            Assert.Matches("^(FM|DAC|noise)(\\+(FM|DAC|noise))*$", source.Voices(sample, source.Orders));
            Assert.Equal(module.Samples[sample].Name, source.Name(sample));
        });
    }

    /// <summary>Each sample heard has its own stem DMF.</summary>
    [Fact]
    public void SoloDmf_ForSamplesHeard_DiffersForEach()
    {
        // Arrange
        using TempDirectory temp = new();
        (_, _, StemSource source) = Prepare(temp);
        IReadOnlyList<int> samples = source.SamplesIn(source.Orders);

        // Act
        string[] dmfs = [.. samples.Select(sample => Convert.ToBase64String(source.SoloDmf(sample, source.Orders)))];

        // Assert
        Assert.Equal(samples.Count, dmfs.Distinct().Count());
    }

    /// <summary>A stem of fewer orders is a smaller DMF.</summary>
    [Fact]
    public void SoloDmf_WithFewerOrders_IsSmaller()
    {
        // Arrange
        using TempDirectory temp = new();
        (_, _, StemSource source) = Prepare(temp);
        int sample = source.SamplesIn(source.Orders)[0];

        // Act
        byte[] part = source.SoloDmf(sample, 1);

        // Assert
        Assert.True(part.Length < source.SoloDmf(sample, source.Orders).Length);
    }

    /// <summary>A DMF that isn't the default conversion's is refused: the stems would come from another plan.</summary>
    [Fact]
    public void Dmf_WithOtherDmf_Throws()
    {
        // Act
        byte[] mod = File.ReadAllBytes(TestFiles.ShortSong);

        // Assert
        Assert.Throws<HarnessException>(() => StemSource.Prepare(mod, [1, 2, 3]));
    }
}
