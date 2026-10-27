namespace Mod2Xgm.Tests;

/// <summary>A unique temporary directory, deleted with its contents on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mod2xgm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Creates a file containing a minimal valid MOD (or <paramref name="bytes"/>) and returns its full path.</summary>
    public string CreateFile(string name, byte[]? bytes = null)
    {
        string path = System.IO.Path.Combine(Path, name);
        File.WriteAllBytes(path, bytes ?? Mod.ModBuilder.Minimal());
        return path;
    }

    public string Combine(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
