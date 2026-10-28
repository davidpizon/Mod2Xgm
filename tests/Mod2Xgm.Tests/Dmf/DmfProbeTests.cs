using Mod2Xgm.Dmf;

namespace Mod2Xgm.Tests.Dmf;

/// <summary>
/// Keeps the probe DMF files (plan T4.3) in step with <see cref="DmfProbes"/>, and checks the copies David saves from
/// DefleMask ("Save As" into <c>tests/golden/dmf-probes/deflemask/</c>) hold the same song as ours.
/// Run with <c>UPDATE_GOLDEN=1</c> to rewrite the probe files and their descriptions.
/// </summary>
public sealed class DmfProbeTests
{
    /// <summary>The probe names, for the theories.</summary>
    public static TheoryData<string> Names => new(DmfProbes.All.Keys);

    private static string ProbeDirectory => Path.Combine(RepoPaths.Root, "tests", "golden", "dmf-probes");

    private static string DefleMaskDirectory => Path.Combine(ProbeDirectory, "deflemask");

    /// <summary>
    /// The checked-in probe file holds exactly what the builder makes. The decompressed bytes are compared, since zlib
    /// output may differ between platforms and runtimes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void ProbeFileMatchesItsBuilder(string name)
    {
        DmfBuilder builder = DmfProbes.All[name]();
        string path = Path.Combine(ProbeDirectory, name + ".dmf");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(ProbeDirectory);
            File.WriteAllBytes(path, builder.Build());
            return;
        }

        Assert.True(File.Exists(path), $"Probe {path} is missing. Run the tests with UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(builder.BuildDecompressed(), DmfReader.Decompress(File.ReadAllBytes(path)));
    }

    /// <summary>Each probe passes the strict reader, and its description (checked in next to it, for review) is current.</summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void ProbeIsValidAndDescribed(string name)
    {
        DmfFile probe = DmfReader.Read(DmfProbes.All[name]().Build());

        Assert.Equal(0, probe.TrailingBytes);
        Golden.Verify(Path.Combine("dmf-probes", name + ".txt"), DmfDescription.Describe(probe));
    }

    /// <summary>
    /// Every copy saved from DefleMask holds the same song as our probe: same timing, instruments, notes, effects and
    /// samples (the file version, pattern names and sample cut points may differ). Copies are added as David saves
    /// them; a file whose name matches no probe fails, so a typo doesn't go unchecked.
    /// </summary>
    [Fact]
    public void DefleMaskCopiesHoldTheSameSong()
    {
        if (!Directory.Exists(DefleMaskDirectory))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(DefleMaskDirectory, "*.dmf"))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            Assert.True(DmfProbes.All.ContainsKey(name), $"{path} matches no probe; name it after one of: {string.Join(", ", DmfProbes.All.Keys)}.");

            DmfFile ours = DmfReader.Read(DmfProbes.All[name]().Build());
            DmfFile theirs = DmfReader.Read(File.ReadAllBytes(path));
            Assert.Equal(DmfDescription.Describe(ours), DmfDescription.Describe(theirs));
        }
    }

    /// <summary>The description leaves out what only says how the file was saved, so a newer DefleMask's copy still matches.</summary>
    [Fact]
    public void DescriptionIgnoresHowTheFileWasSaved()
    {
        DmfBuilder ours = new DmfBuilder { MatrixRows = 2 }.WithSample(new DmfSample("s", 5, 5, 50, 16, null, null, [1, 2, 3]));
        DmfBuilder newer = new DmfBuilder { Version = 27, MatrixRows = 2, CustomHzBytes = "60\0"u8.ToArray(), TrailingBytes = [0] }
            .WithPatternName(0, 1, "verse")
            .WithSample(new DmfSample("s", 5, 5, 50, 16, 0, 3, [1, 2, 3]));
        DmfBuilder different = new DmfBuilder { MatrixRows = 2 }.WithSample(new DmfSample("s", 5, 5, 50, 16, null, null, [1, 2, 4]));

        string described = DmfDescription.Describe(DmfReader.Read(ours.Build()));
        Assert.Equal(described, DmfDescription.Describe(DmfReader.Read(newer.Build())));
        Assert.NotEqual(described, DmfDescription.Describe(DmfReader.Read(different.Build())));
    }
}
