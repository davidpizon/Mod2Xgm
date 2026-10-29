namespace Mod2Xgm.Harness.Tests;

/// <summary>Locations in the checkout and scratch folders for the harness tests.</summary>
internal static class TestFiles
{
    /// <summary>The repository root: the folder above the test binaries that holds <c>Mod2Xgm.slnx</c>.</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>A short corpus song (31 s, plan T6.1).</summary>
    public static string ShortSong => Path.Combine(Root, "tests", "data", "corpus", "fx-poly1.mod");

    /// <summary>A sine tone, the same in both channels.</summary>
    public static StereoAudio Tone(double seconds, int sampleRate = 8000, double hz = 440, float level = 0.5f)
    {
        float[] samples = new float[(int)(seconds * sampleRate)];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = level * (float)Math.Sin(2 * Math.PI * hz * i / sampleRate);
        }

        return new StereoAudio(samples, samples, sampleRate);
    }

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Mod2Xgm.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"Mod2Xgm.slnx not found above {AppContext.BaseDirectory}.");
    }
}

/// <summary>A unique temporary directory, deleted with its contents on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    /// <summary>Creates the directory.</summary>
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mod2xgm-harness-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>Full path of the directory.</summary>
    public string Path { get; }

    /// <summary>A path inside the directory.</summary>
    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Deletes the directory and everything in it.</summary>
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
