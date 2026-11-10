using System.Diagnostics;
using Mod2Xgm.Cli;
using Serilog.Core;

namespace Mod2Xgm.Tests.Xgm;

/// <summary>
/// SGDK compliance of the XGM2 writer (plan T10.1, AGENTS.md#sgdk-compliance): SGDK's own xgm2tool reads the file and
/// compiles it to the <c>.xgc</c> its Z80 driver plays, and rescomp builds an <c>XGM2</c> resource from it. They need SGDK
/// (the <c>GDK</c> environment variable, or <c>C:\SGDK</c>) and Java, so they are skipped where those are missing, as in CI.
/// SGDK reads XGM2 files by the <c>.xgm</c> extension, so the tests copy the <c>.xgm2</c> output to that name.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Xgm2SgdkTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>xgm2tool parses the converted song, finds every recording and compiles it to a non-empty <c>.xgc</c>.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("fx-poly1.mod")]
    public void Xgm2Tool_WithConvertedSong_CompilesIt(string corpusSong)
    {
        // Arrange
        string sgdk = SgdkBin();
        using TempDirectory temp = new();
        string xgm = Convert(temp, corpusSong, out int samples);

        // Act
        (int exit, string output) = Run(temp.Path, "java", "-jar", Path.Combine(sgdk, "xgm2tool.jar"), xgm, "song.xgc");

        // Assert
        Assert.True(exit == 0, output);
        Assert.Contains($"Number of PCM sample: {samples}", output, StringComparison.Ordinal);
        Assert.True(new FileInfo(temp.Combine("song.xgc")).Length > 0, output);
    }

    /// <summary>rescomp accepts the converted song as an <c>XGM2</c> resource, as an SGDK game's build would.</summary>
    [Fact]
    public void Rescomp_WithConvertedSong_BuildsXgm2Resource()
    {
        // Arrange
        string sgdk = SgdkBin();
        using TempDirectory temp = new();
        Convert(temp, "fx-poly1.mod", out _);
        File.WriteAllText(temp.Combine("music.res"), "XGM2 song \"song.xgm\"\n");

        // Act
        (int exit, string output) = Run(temp.Path, "java", "-jar", Path.Combine(sgdk, "rescomp.jar"), "music.res", "music.s");

        // Assert
        Assert.True(exit == 0, output);
        Assert.DoesNotContain("error", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("song", File.ReadAllText(temp.Combine("music.h")), StringComparison.Ordinal);
    }

    /// <summary>SGDK's <c>bin</c> folder, or skips the test when SGDK or Java isn't installed.</summary>
    private static string SgdkBin()
    {
        string root = Environment.GetEnvironmentVariable("GDK") is { Length: > 0 } gdk ? gdk : @"C:\SGDK";
        string bin = Path.Combine(root, "bin");
        Assert.SkipUnless(File.Exists(Path.Combine(bin, "xgm2tool.jar")) && File.Exists(Path.Combine(bin, "rescomp.jar")), $"SGDK isn't installed at {root} (set GDK)");
        Assert.SkipUnless(JavaAvailable(), "Java isn't on the PATH");
        return bin;
    }

    private static bool JavaAvailable()
    {
        try
        {
            return Run(Path.GetTempPath(), "java", "-version").Exit == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Converts the corpus song (or, for "", the minimal test MOD) to XGM2 and copies it to <c>song.xgm</c>.</summary>
    private static string Convert(TempDirectory temp, string corpusSong, out int samples)
    {
        string input = corpusSong.Length == 0
            ? temp.CreateFile("song.mod")
            : temp.CreateFile("song.mod", File.ReadAllBytes(Path.Combine(RepoPaths.Root, "tests", "data", "corpus", corpusSong)));
        ConversionOptions options = new() { InputPath = input, Format = OutputFormat.Xgm2 };
        ConversionCommand.Execute(options, Logger.None);
        byte[] file = File.ReadAllBytes(temp.Combine("song.xgm2"));
        File.WriteAllBytes(temp.Combine("song.xgm"), file);

        // The SID table's filled entries are the samples and the end of the last (docs/formats/xgm.md).
        int filled = Enumerable.Range(0, 124).Count(i => file[0x0c + (2 * i)] != 0xff || file[0x0d + (2 * i)] != 0xff);
        samples = Math.Max(0, filled - 1);
        return "song.xgm";
    }

    private static (int Exit, string Output) Run(string directory, string program, params string[] arguments)
    {
        ProcessStartInfo start = new(program, arguments)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Start returns null only when it reuses a running process, which UseShellExecute = false never does.
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(Timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{program} {string.Join(' ', arguments)} didn't finish in {Timeout}.");
        }

        return (process.ExitCode, output.Result + error.Result);
    }
}
