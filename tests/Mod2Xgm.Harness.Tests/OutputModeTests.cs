using Mod2Xgm.Output;

namespace Mod2Xgm.Harness.Tests;

/// <summary>
/// Tests of the harness's <c>--progress</c> modes (plan T17.8, docs/cli-output.md#the-harness): every mode reports
/// failures with exit 2 and its kind, and a whole run shows one step per song and the report as its result.
/// </summary>
[Collection(HarnessState.Name)]
[Trait("Category", "Integration")]
public sealed class OutputModeTests
{
    /// <summary>An input that isn't there exits 2 in fancy mode, as an error on stderr.</summary>
    [Fact]
    public void Invoke_WithMissingInputInFancyMode_PrintsError()
    {
        // Arrange
        using TempDirectory temp = new();

        // Act
        (int code, _, string error) = Run("--progress", "fancy", temp.Combine("missing.mod"), "--no-cache");

        // Assert
        Assert.Equal(Program.CannotRun, code);
        Assert.StartsWith("error Not found:", error, StringComparison.Ordinal);
        Assert.Contains("(exit 2)", error, StringComparison.Ordinal);
    }

    /// <summary>An input that isn't there exits 2 in text mode, as a <c>cannot-run</c> error on stderr.</summary>
    [Fact]
    public void Invoke_WithMissingInputInTextMode_PrintsError()
    {
        // Arrange
        using TempDirectory temp = new();

        // Act
        (int code, _, string error) = Run("--progress", "text", temp.Combine("missing.mod"), "--no-cache");

        // Assert
        Assert.Equal(Program.CannotRun, code);
        Assert.StartsWith("error exit=2 kind=cannot-run message=\"Not found:", error, StringComparison.Ordinal);
    }

    /// <summary>An input that isn't there exits 2 in json mode, as a <c>cannot-run</c> error on stdout.</summary>
    [Fact]
    public void Invoke_WithMissingInputInJsonMode_PrintsErrorToStdout()
    {
        // Arrange
        using TempDirectory temp = new();

        // Act
        (int code, string output, string error) = Run("--progress", "json", temp.Combine("missing.mod"), "--no-cache");

        // Assert
        Assert.Equal(Program.CannotRun, code);
        Assert.Contains("\"error\":\"cannot-run\"", output.Trim().Split('\n')[^1], StringComparison.Ordinal);
        Assert.Equal(string.Empty, error);
    }

    /// <summary>An input that isn't there exits 2 in silent mode, printing nothing.</summary>
    [Fact]
    public void Invoke_WithMissingInputInSilentMode_PrintsNothing()
    {
        // Arrange
        using TempDirectory temp = new();

        // Act
        (int code, string output, string error) = Run("--progress", "silent", temp.Combine("missing.mod"), "--no-cache");

        // Assert
        Assert.Equal(Program.CannotRun, code);
        Assert.Equal(string.Empty, output);
        Assert.Equal(string.Empty, error);
    }

    /// <summary>A bad command line is a <c>usage</c> error, exit 2; an unknown <c>--progress</c> value is one too.</summary>
    [Fact]
    public void Invoke_WithBadOptions_ReportsUsageError()
    {
        // Arrange
        (int code, _, string error) = Run("--progress", "text", "--rate", "100");

        // Act
        (int unknownCode, _, string unknownError) = Run("--progress", "loud");

        // Assert
        Assert.Equal(Program.CannotRun, code);
        Assert.StartsWith("error exit=2 kind=usage message=\"--rate 100: between 8000 and 192000 Hz", error, StringComparison.Ordinal);
        Assert.Equal(Program.CannotRun, unknownCode);
        Assert.Contains("loud", unknownError, StringComparison.Ordinal);
    }

    /// <summary>Runs one short song in <paramref name="mode"/>, skipping the test where libopenmpt isn't installed.</summary>
    private static (int Code, string Output, string Error) WholeRun(TempDirectory temp, string mode)
    {
        try
        {
            LibOpenMpt.Version();
        }
        catch (HarnessException ex)
        {
            Assert.Skip("libopenmpt isn't installed: " + ex.Message);
        }
        Directory.CreateDirectory(temp.Combine("no-exports"));
        return Run(
            "--progress", mode, TestFiles.ShortSong, "-o", temp.Combine("out"), "--rate", "8000", "--no-furnace",
            "--deflemask-wavs", temp.Combine("no-exports"), "--cache-dir", temp.Combine("cache"));
    }

    /// <summary>A whole run in fancy mode shows the song as a step and names the report.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Invoke_ForWholeRunInFancyMode_ShowsSong()
    {
        // Arrange
        using TempDirectory temp = new();

        // Act
        (int code, string output, string error) = WholeRun(temp, "fancy");

        // Assert
        Assert.Equal(0, code);
        Assert.Equal(string.Empty, error);
        Assert.StartsWith("── mod2xgm harness ", output, StringComparison.Ordinal);
        Assert.Contains("1 songs, ", output, StringComparison.Ordinal);
        Assert.Contains("✓ [1/1] fx-poly1", output, StringComparison.Ordinal);
        Assert.True(output.Contains("Done → " + temp.Combine("out", "report.md"), StringComparison.Ordinal), output);
    }

    /// <summary>A whole run in text mode shows the song as a step and names the report.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Invoke_ForWholeRunInTextMode_ShowsSong()
    {
        // Arrange
        using TempDirectory temp = new();

        // Act
        (int code, string output, string error) = WholeRun(temp, "text");

        // Assert
        Assert.Equal(0, code);
        Assert.Equal(string.Empty, error);
        Assert.StartsWith("run mod2xgm=", output, StringComparison.Ordinal);
        Assert.Contains("format=harness", output, StringComparison.Ordinal);
        Assert.Contains("\nstep 1/1 song:fx-poly1 start", output, StringComparison.Ordinal);
        Assert.Contains("\nstep 1/1 song:fx-poly1 done", output, StringComparison.Ordinal);
        Assert.Contains("\nresult ok exit=0 out=" + temp.Combine("out", "report.md"), output, StringComparison.Ordinal);
    }

    /// <summary>A whole run in json mode shows the song as a step and ends in the result.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Invoke_ForWholeRunInJsonMode_ShowsSong()
    {
        // Arrange
        using TempDirectory temp = new();

        // Act
        (int code, string output, string error) = WholeRun(temp, "json");

        // Assert
        string[] lines = output.Trim().Split('\n');
        Assert.Equal(0, code);
        Assert.Equal(string.Empty, error);
        Assert.Contains("\"format\":\"harness\"", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Contains("\"id\":\"song:fx-poly1\"", StringComparison.Ordinal));
        Assert.Contains("\"type\":\"result\"", lines[^1], StringComparison.Ordinal);
    }

    /// <summary>A whole run in silent mode prints nothing, and the song's time still goes into the timing history.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Invoke_ForWholeRunInSilentMode_PrintsNothingAndKeepsTiming()
    {
        // Arrange
        using TempDirectory temp = new();

        // Act
        (int code, string output, string error) = WholeRun(temp, "silent");

        // Assert
        Assert.Equal(0, code);
        Assert.Equal(string.Empty, output);
        Assert.Equal(string.Empty, error);
        Assert.Contains("\"song:fx-poly1\"", File.ReadAllText(temp.Combine("cache", "timings.json")), StringComparison.Ordinal);
    }

    /// <summary>Runs the harness with <paramref name="args"/>, its output on a dumb terminal 200 columns wide (a piped one would wrap at 80), stderr piped; nothing prompts.</summary>
    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        TerminalProbe terminal = TerminalProbe.From(false, true, 200, v => v == "TERM" ? "dumb" : null);
        int code = Program.Run(args, output, error, terminal);
        return (code, output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), error.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }
}

/// <summary>Tests that run <see cref="Program.Run"/>, which sets the static <c>Log.Logger</c>, run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HarnessState
{
    /// <summary>The collection's name.</summary>
    public const string Name = "Harness Program.Run";
}
