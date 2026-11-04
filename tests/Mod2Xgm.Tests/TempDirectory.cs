namespace Mod2Xgm.Tests;

/// <summary>
/// A unique temporary directory, deleted with its contents on dispose: the only place integration tests write files
/// (docs/testing-principles.md#integration-tests).
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    /// <summary>Creates a new, empty directory under the system's temporary folder.</summary>
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mod2xgm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>The directory's full path.</summary>
    public string Path { get; }

    /// <summary>Creates a file containing a minimal valid MOD (or <paramref name="bytes"/>) and returns its full path.</summary>
    public string CreateFile(string name, byte[]? bytes = null)
    {
        string path = System.IO.Path.Combine(Path, name);
        File.WriteAllBytes(path, bytes ?? Mod.ModBuilder.Minimal());
        return path;
    }

    /// <summary>The full path of <paramref name="name"/> in this directory; nothing is created.</summary>
    public string Combine(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Deletes the directory and everything in it.</summary>
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
