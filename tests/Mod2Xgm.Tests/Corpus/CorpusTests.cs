using System.Security.Cryptography;
using Mod2Xgm.Mod;
using Serilog.Core;

namespace Mod2Xgm.Tests.Corpus;

/// <summary>
/// The corpus of freely licensed MODs (plan T6.1, tests/data/corpus): every file is listed in tests/data/LICENSES.md
/// with a matching checksum, and every file reads as a 4-channel MOD. The 15-sample song is made by
/// <see cref="CorpusSongs"/>; run with <c>UPDATE_GOLDEN=1</c> to rewrite it after a change (then update its checksum).
/// </summary>
[Trait("Category", "Integration")]
public sealed class CorpusTests
{
    private static string DataDirectory => Path.Combine(RepoPaths.Root, "tests", "data");

    private static string CorpusDirectory => Path.Combine(DataDirectory, "corpus");

    /// <summary>The corpus file names, for the theories.</summary>
    public static TheoryData<string> Files =>
        new(Directory.EnumerateFiles(CorpusDirectory).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal));

    /// <summary>The corpus holds 10–20 songs (plan T6.1), every one listed in LICENSES.md, and every listed one is there.</summary>
    [Fact]
    public void Corpus_WithLicenseTable_ListsExactlyItsFiles()
    {
        // Arrange
        string[] files = [.. Files.Select(row => row.Data)];

        // Act
        IEnumerable<string> listed = ListedChecksums().Keys.Order(StringComparer.Ordinal);

        // Assert
        Assert.InRange(files.Length, 10, 20);
        Assert.Equal(files, listed);
    }

    /// <summary>Each file is the one whose license was recorded: its SHA-256 matches LICENSES.md.</summary>
    [Theory]
    [MemberData(nameof(Files))]
    public void Corpus_ForFile_MatchesListedChecksum(string file)
    {
        // Arrange
        Dictionary<string, string> listed = ListedChecksums();

        // Act
        string actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(CorpusDirectory, file))));

        // Assert
        Assert.Contains(file, listed.Keys);
        Assert.Equal(listed[file], actual);
    }

    /// <summary>Each file reads as a 4-channel MOD; conversion notes are fine, rejection is not.</summary>
    [Theory]
    [MemberData(nameof(Files))]
    public void Read_ForCorpusFile_ReturnsFourChannelMod(string file)
    {
        // Arrange
        byte[] bytes = File.ReadAllBytes(Path.Combine(CorpusDirectory, file));

        // Act
        ModModule module = ModReader.Read(bytes, new ConversionNotes(Logger.None));

        // Assert
        Assert.Equal(4, module.ChannelCount);
        Assert.NotEmpty(module.Orders);
    }

    /// <summary>The checked-in 15-sample song is what <see cref="CorpusSongs.Soundtracker15"/> builds, byte for byte.</summary>
    [Fact]
    public void Soundtracker15_WithCheckedInFile_MatchesBuilder()
    {
        // Act
        byte[] mod = CorpusSongs.Soundtracker15().Build();

        // Assert
        VerifyFile(Path.Combine(CorpusDirectory, CorpusSongs.Soundtracker15FileName), mod);
    }

    /// <summary>The corpus has both layouts: the made-in-code song is read as 15-sample and played by Ultimate Soundtracker's rules.</summary>
    [Fact]
    public void Read_WithSoundtracker15_ReadsFifteenSamples()
    {
        // Arrange
        byte[] bytes = CorpusSongs.Soundtracker15().Build();

        // Act
        ModModule module = ModReader.Read(bytes, new ConversionNotes(Logger.None));

        // Assert
        Assert.Equal(ModFormat.Soundtracker15, module.Format);
        Assert.Equal(15, module.Samples.Count);
        Assert.Equal(ModPlayer.UltimateSoundtracker, module.Player);
    }

    /// <summary>File name → SHA-256 (lower-case hex) from the corpus table in LICENSES.md.</summary>
    private static Dictionary<string, string> ListedChecksums()
    {
        Dictionary<string, string> listed = new(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(Path.Combine(DataDirectory, "LICENSES.md")))
        {
            string[] cells = [.. line.Split('|').Select(c => c.Trim())];
            if (cells.Length > 2 && cells[1].StartsWith('`') && cells[1].EndsWith(".mod`", StringComparison.Ordinal))
            {
                listed.Add(cells[1].Trim('`'), cells[^2].Trim('`'));
            }
        }

        return listed;
    }

    /// <summary>Asserts that the file holds <paramref name="expected"/>; with <c>UPDATE_GOLDEN=1</c>, rewrites it instead, as <see cref="Golden"/> does.</summary>
    private static void VerifyFile(string path, byte[] expected)
    {
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            File.WriteAllBytes(path, expected);
            return;
        }

        Assert.True(File.Exists(path), $"{path} is missing. Run the tests with UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(expected, File.ReadAllBytes(path));
    }
}
