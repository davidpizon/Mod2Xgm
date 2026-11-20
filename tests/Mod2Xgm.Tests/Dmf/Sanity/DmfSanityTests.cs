namespace Mod2Xgm.Tests.Dmf.Sanity;

/// <summary>
/// The DMF sanity check (docs/testing.md#dmf-sanity-check): songs made in DefleMask, kept locally in
/// <c>tests/data/dmf/</c> with DefleMask's VGM export of each, go to XGM2 and are compared with that export. The songs
/// aren't freely licensed, so the folder is git-ignored and the check is skipped where it is missing, as in CI.
/// Differences are potential defects, written to <c>tests/data/dmf/sanity-report.md</c>; a test fails only when its
/// song can't be put through at all. One test per song keeps each under the 10-minute hang limit (ADR 0079).
/// </summary>
[Trait("Category", "Integration")]
[Trait("Speed", "Slow")]
public sealed class DmfSanityTests
{
    /// <summary>
    /// Every DMF in the local folder with a VGM next to it, by file name; none where the folder is missing. Set
    /// <c>M2X_DMF_SANITY_SONGS</c> to a comma-separated list of names (without <c>.dmf</c>) to check only those.
    /// </summary>
    public static TheoryData<string> Songs
    {
        get
        {
            TheoryData<string> songs = [];
            if (Directory.Exists(DmfSanityReport.Folder))
            {
                string[] only = (Environment.GetEnvironmentVariable("M2X_DMF_SANITY_SONGS") ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                songs.AddRange(Directory.GetFiles(DmfSanityReport.Folder, "*.dmf")
                    .Where(p => File.Exists(Path.ChangeExtension(p, ".vgm")))
                    .Where(p => only.Length == 0 || only.Contains(Path.GetFileNameWithoutExtension(p), StringComparer.Ordinal))
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .Order(StringComparer.Ordinal));
            }

            return songs;
        }
    }

    /// <summary>
    /// The song goes through both paths and the three checks without failing (or is listed as not read, for an old DMF
    /// version), and joins the report. Set <c>M2X_DMF_SANITY_SOUND=0</c> to leave out the (slow) sound check.
    /// </summary>
    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(Songs))]
    public void Check_ForLocalSong_AddsToReport(string song)
    {
        // Arrange
        bool sound = Environment.GetEnvironmentVariable("M2X_DMF_SANITY_SOUND") != "0";

        // Act
        SongResult result = SanityRun.Check(Path.Combine(DmfSanityReport.Folder, song), sound);
        DmfSanityReport.Add(result);

        // Assert
        Assert.True(result.Error is not null || result.Paths.Count == 2, $"{song}: {result.Paths.Count} paths");
    }
}

/// <summary>
/// Collects <see cref="DmfSanityTests"/>' results and rewrites <c>sanity-report.md</c> after each, with every song that has
/// run so far in name order, so the report is whole once the last song is in.
/// </summary>
internal static class DmfSanityReport
{
    private static readonly Dictionary<string, SongResult> Results = [with(StringComparer.Ordinal)];
    private static readonly Lock Writing = new();

    /// <summary>The local songs' folder.</summary>
    internal static string Folder => Path.Combine(RepoPaths.Root, "tests", "data", "dmf");

    /// <summary>Adds one song's result and rewrites the report.</summary>
    internal static void Add(SongResult result)
    {
        lock (Writing)
        {
            Results[result.Song] = result;
            SongResult[] songs = [.. Results.Values.OrderBy(r => r.Song, StringComparer.Ordinal)];
            File.WriteAllText(Path.Combine(Folder, "sanity-report.md"), SanityReport.Write(songs, DateTimeOffset.Now));
        }
    }
}
