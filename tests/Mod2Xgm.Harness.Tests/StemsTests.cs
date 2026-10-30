using Mod2Xgm.Cli;
using Mod2Xgm.Mod;
using Serilog.Core;

namespace Mod2Xgm.Harness.Tests;

/// <summary>Tests for <see cref="StemSource"/>: one sample alone from the MOD and from the conversion (docs/adr/0051-instrument-balance-metric.md).</summary>
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
    public void TheModStemZeroesEveryOtherSample()
    {
        using TempDirectory temp = new();
        (byte[] mod, ModModule module, StemSource source) = Prepare(temp);
        int sample = source.SamplesIn(source.Orders)[0];

        byte[] solo = source.SoloMod(sample);

        Assert.Equal(mod.Length, solo.Length);
        ModSample kept = module.Samples[sample];
        Assert.Equal(mod.AsSpan(0, module.Samples[0].DataOffset).ToArray(), solo.AsSpan(0, module.Samples[0].DataOffset).ToArray());
        Assert.Equal(mod.AsSpan(kept.DataOffset, kept.Length).ToArray(), solo.AsSpan(kept.DataOffset, kept.Length).ToArray());
        foreach (ModSample other in module.Samples.Where((s, i) => i != sample && !s.IsEmpty))
        {
            Assert.All(solo.AsSpan(other.DataOffset, other.Length).ToArray(), b => Assert.Equal(0, b));
        }
    }

    /// <summary>
    /// The samples heard are the non-empty ones the patterns name; each has a voice in the conversion and a stem DMF
    /// unlike the whole song's; fewer orders hear no more samples.
    /// </summary>
    [Fact]
    public void EverySampleHeardHasAStemDmf()
    {
        using TempDirectory temp = new();
        (_, ModModule module, StemSource source) = Prepare(temp);

        IReadOnlyList<int> samples = source.SamplesIn(source.Orders);

        Assert.NotEmpty(samples);
        Assert.All(samples, s => Assert.False(module.Samples[s].IsEmpty));
        Assert.True(source.SamplesIn(1).Count <= samples.Count);
        HashSet<string> dmfs = [];
        foreach (int sample in samples)
        {
            Assert.Matches("^(FM|DAC|noise)(\\+(FM|DAC|noise))*$", source.Voices(sample, source.Orders));
            Assert.Equal(module.Samples[sample].Name, source.Name(sample));
            dmfs.Add(Convert.ToBase64String(source.SoloDmf(sample, source.Orders)));
        }

        Assert.Equal(samples.Count, dmfs.Count);
        Assert.True(source.SoloDmf(samples[0], 1).Length < source.SoloDmf(samples[0], source.Orders).Length);
    }

    /// <summary>A DMF that isn't the default conversion's is refused: the stems would come from another plan.</summary>
    [Fact]
    public void AnotherDmfIsRefused()
    {
        byte[] mod = File.ReadAllBytes(TestFiles.ShortSong);

        Assert.Throws<HarnessException>(() => StemSource.Prepare(mod, [1, 2, 3]));
    }
}
