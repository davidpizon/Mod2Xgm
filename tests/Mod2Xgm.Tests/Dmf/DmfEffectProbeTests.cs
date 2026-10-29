using System.Text;
using Mod2Xgm.Mapping;
using Mod2Xgm.Tests.Mod;

namespace Mod2Xgm.Tests.Dmf;

/// <summary>
/// Keeps the effect probes (plan T5.5, tests/golden/dmf-effect-probes) in step with <see cref="DmfEffectProbes"/> and the
/// converter: each probe's MOD, its DMF, and a description of the DMF with the conversion notes. Run with
/// <c>UPDATE_GOLDEN=1</c> to rewrite them after a change, then re-render the WAV files (README.md there).
/// </summary>
public sealed class DmfEffectProbeTests
{
    /// <summary>The probe names, for the theories.</summary>
    public static TheoryData<string> Names => new(DmfEffectProbes.All.Keys);

    private static string ProbeDirectory => Path.Combine(RepoPaths.Root, "tests", "golden", "dmf-effect-probes");

    /// <summary>The checked-in MOD is what the probe builds, byte for byte.</summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void ModFileMatchesItsBuilder(string name)
    {
        byte[] mod = DmfEffectProbes.All[name].Mod().Build();
        string path = Path.Combine(ProbeDirectory, name + ".mod");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(ProbeDirectory);
            File.WriteAllBytes(path, mod);
            return;
        }

        Assert.True(File.Exists(path), $"Probe {path} is missing. Run the tests with UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(mod, File.ReadAllBytes(path));
    }

    /// <summary>
    /// The checked-in DMF is what the converter makes of the MOD now. The decompressed bytes are compared, since zlib
    /// output may differ between platforms and runtimes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void DmfFileMatchesTheConverter(string name)
    {
        DmfConversion.Result result = Convert(name);
        string path = Path.Combine(ProbeDirectory, name + ".dmf");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(ProbeDirectory);
            File.WriteAllBytes(path, result.Bytes);
            return;
        }

        Assert.True(File.Exists(path), $"Probe {path} is missing. Run the tests with UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(DmfReader.Decompress(result.Bytes), DmfReader.Decompress(File.ReadAllBytes(path)));
    }

    /// <summary>Each probe's description and conversion notes (checked in next to it, for review) are current.</summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void ProbeIsDescribed(string name)
    {
        DmfConversion.Result result = Convert(name);
        StringBuilder text = new(DmfDescription.Describe(result.File));
        text.Append("conversion notes:\n");
        foreach (string note in result.Notes)
        {
            text.Append("  ").Append(note).Append('\n');
        }

        Golden.Verify(Path.Combine("dmf-effect-probes", name + ".txt"), text.ToString());
    }

    private static DmfConversion.Result Convert(string name)
    {
        (Func<ModBuilder> mod, MappingOptions options) = DmfEffectProbes.All[name];
        return DmfConversion.Convert(mod().Build(), options);
    }
}
