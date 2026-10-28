namespace Mod2Xgm.Tests;

/// <summary>
/// Compares text with a checked-in file under <c>tests/golden/</c> (docs/testing.md). Run the tests with
/// <c>UPDATE_GOLDEN=1</c> to rewrite the files from the current output; review the diff before committing.
/// </summary>
internal static class Golden
{
    /// <summary>Asserts that <paramref name="actual"/> equals the golden file <paramref name="relativePath"/>, ignoring CR/LF differences.</summary>
    public static void Verify(string relativePath, string actual)
    {
        string path = Path.Combine(GoldenDirectory(), relativePath);
        string normalized = Normalize(actual);
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? GoldenDirectory());
            File.WriteAllText(path, normalized);
            return;
        }

        Assert.True(File.Exists(path), $"Golden file {path} is missing. Run the tests with UPDATE_GOLDEN=1 to create it. Output:\n{normalized}");
        Assert.Equal(Normalize(File.ReadAllText(path)), normalized);
    }

    // Git checks text files out with CRLF on Windows (.gitattributes text=auto), and Serilog writes Environment.NewLine.
    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary><c>tests/golden/</c>, found by walking up from the test binaries to the folder that holds <c>Mod2Xgm.slnx</c>.</summary>
    private static string GoldenDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Mod2Xgm.slnx")))
            {
                return Path.Combine(directory.FullName, "tests", "golden");
            }
        }

        throw new InvalidOperationException($"Mod2Xgm.slnx not found above {AppContext.BaseDirectory}.");
    }
}
