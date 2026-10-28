namespace Mod2Xgm.Tests;

/// <summary>Locations in the repository checkout, for tests that read checked-in files (goldens, source).</summary>
internal static class RepoPaths
{
    /// <summary>The repository root: the folder above the test binaries that holds <c>Mod2Xgm.slnx</c>.</summary>
    public static string Root { get; } = FindRoot();

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
