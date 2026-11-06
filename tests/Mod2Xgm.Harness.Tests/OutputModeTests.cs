using Mod2Xgm.Output;

namespace Mod2Xgm.Harness.Tests;

/// <summary>
/// Tests of the harness's <c>--progress</c> modes (plan T17.8, docs/cli-output.md#the-harness): every mode reports
/// failures with exit 2 and its kind, and a whole run shows one step per song and the report as its result.
/// </summary>
[Collection(HarnessState.Name)]
public sealed class OutputModeTests
{
    /// <summary>An input that isn't there exits 2 in every mode, as a <c>cannot-run</c> error on stderr (stdout for json); silent prints nothing.</summary>
    [Theory]
    [InlineData("fancy")]
    [InlineData("text")]
    [InlineData("json")]
    [InlineData("silent")]
    public void AMissingInputCannotRun(string mode)
    {
        using TempDirectory temp = new();
        (int code, string output, string error) = Run("--progress", mode, temp.Combine("missing.mod"), "--no-cache");

        Assert.Equal(Program.CannotRun, code);
        switch (mode)
        {
            case "fancy":
                Assert.StartsWith("error Not found:", error, StringComparison.Ordinal);
                Assert.Contains("(exit 2)", error, StringComparison.Ordinal);
                break;
            case "text":
                Assert.StartsWith("error exit=2 kind=cannot-run message=\"Not found:", error, StringComparison.Ordinal);
                break;
            case "json":
                Assert.Contains("\"error\":\"cannot-run\"", output.Trim().Split('\n')[^1], StringComparison.Ordinal);
                Assert.Equal(string.Empty, error);
                break;
            default:
                Assert.Equal(string.Empty, output);
                Assert.Equal(string.Empty, error);
                break;
        }
    }

    /// <summary>A bad command line is a <c>usage</c> error, exit 2; an unknown <c>--progress</c> value is one too.</summary>
    [Fact]
    public void BadOptionsAreUsageErrors()
    {
        (int code, _, string error) = Run("--progress", "text", "--rate", "100");
        (int unknownCode, _, string unknownError) = Run("--progress", "loud");

        Assert.Equal(Program.CannotRun, code);
        Assert.StartsWith("error exit=2 kind=usage message=\"--rate 100: between 8000 and 192000 Hz", error, StringComparison.Ordinal);
        Assert.Equal(Program.CannotRun, unknownCode);
        Assert.Contains("loud", unknownError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A whole run of one song in each mode: the song is a step, the result names the report, and the song's time is
    /// kept in the timing history (skipped where libopenmpt isn't installed).
    /// </summary>
    [Theory]
    [InlineData("fancy")]
    [InlineData("text")]
    [InlineData("json")]
    [InlineData("silent")]
    [Trait("Speed", "Slow")]
    public void AWholeRunShowsEachSong(string mode)
    {
        try
        {
            LibOpenMpt.Version();
        }
        catch (HarnessException ex)
        {
            Assert.Skip("libopenmpt isn't installed: " + ex.Message);
        }

        using TempDirectory temp = new();
        Directory.CreateDirectory(temp.Combine("no-exports"));
        (int code, string output, string error) = Run(
            "--progress", mode, TestFiles.ShortSong, "-o", temp.Combine("out"), "--rate", "8000", "--no-furnace",
            "--deflemask-wavs", temp.Combine("no-exports"), "--cache-dir", temp.Combine("cache"));

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, error);
        string report = temp.Combine("out", "report.md");
        switch (mode)
        {
            case "fancy":
                Assert.StartsWith("── mod2xgm harness ", output, StringComparison.Ordinal);
                Assert.Contains("1 songs, ", output, StringComparison.Ordinal);
                Assert.Contains("✓ [1/1] fx-poly1", output, StringComparison.Ordinal);
                Assert.True(output.Contains("Done → " + report, StringComparison.Ordinal), output);
                break;
            case "text":
                Assert.StartsWith("run mod2xgm=", output, StringComparison.Ordinal);
                Assert.Contains("format=harness", output, StringComparison.Ordinal);
                Assert.Contains("\nstep 1/1 song:fx-poly1 start", output, StringComparison.Ordinal);
                Assert.Contains("\nstep 1/1 song:fx-poly1 done", output, StringComparison.Ordinal);
                Assert.Contains("\nresult ok exit=0 out=" + report, output, StringComparison.Ordinal);
                break;
            case "json":
                string[] lines = output.Trim().Split('\n');
                Assert.Contains("\"format\":\"harness\"", lines[0], StringComparison.Ordinal);
                Assert.Contains(lines, l => l.Contains("\"id\":\"song:fx-poly1\"", StringComparison.Ordinal));
                Assert.Contains("\"type\":\"result\"", lines[^1], StringComparison.Ordinal);
                break;
            default:
                Assert.Equal(string.Empty, output);
                break;
        }

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
