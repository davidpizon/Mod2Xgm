using System.Text.Json;

namespace Mod2Xgm.Tests.Output;

/// <summary>
/// Whole command-line runs in each <c>--progress</c> mode, compared with their golden files under
/// <c>tests/golden/output/</c> (docs/cli-output.md), and how each mode ends a failed run.
/// </summary>
[Collection(ProgramState.Name)]
[Trait("Category", "Integration")]
public sealed class ReporterGoldenTests
{
    private const string MissingFolderOutput = "/no/such/folder/out.dmf";

    /// <summary>A whole conversion in text mode matches its golden file: the report, the notes and the result.</summary>
    [Fact]
    public void Convert_InTextMode_MatchesGolden()
    {
        // Act
        (int code, string output, string error) = OutputRuns.Convert("text");

        // Assert
        Assert.Equal((int)ExitCode.Success, code);
        Assert.Equal(string.Empty, error);
        Golden.Verify(Path.Combine("output", "text-conversion.txt"), output);
    }

    /// <summary>A failed conversion in text mode ends with an <c>error</c> line on stderr.</summary>
    [Fact]
    public void Convert_InTextModeWithUnwritableOutput_WritesErrorLineToStderr()
    {
        // Act
        (int code, _, string error) = OutputRuns.Convert("text", "--output", MissingFolderOutput);

        // Assert
        Assert.Equal((int)ExitCode.OutputNotWritable, code);
        Assert.StartsWith("error exit=4 kind=output-not-writable message=\"Output directory doesn't exist:", error, StringComparison.Ordinal);
    }

    /// <summary>A whole conversion in json mode matches its golden file and ends with a <c>result</c> object.</summary>
    [Fact]
    public void Convert_InJsonMode_MatchesGolden()
    {
        // Act
        (int code, string output, string error) = OutputRuns.Convert("json");

        // Assert
        Assert.Equal((int)ExitCode.Success, code);
        Assert.Equal(string.Empty, error);
        Assert.Equal("result", TypeOf(Lines(output)[^1]));
        Golden.Verify(Path.Combine("output", "json-conversion.jsonl"), output);
    }

    /// <summary>Every line of a json run is one JSON object of schema 1 with a type.</summary>
    [Fact]
    public void Convert_InJsonMode_EveryLineIsSchemaOneObject()
    {
        // Act
        (_, string output, _) = OutputRuns.Convert("json");

        // Assert
        Assert.All(Lines(output), line =>
        {
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.Equal(1, document.RootElement.GetProperty("v").GetInt32());
            Assert.False(string.IsNullOrEmpty(document.RootElement.GetProperty("type").GetString()));
        });
    }

    /// <summary>A failure in json mode ends with an <c>error</c> object on stdout; stderr stays empty.</summary>
    [Fact]
    public void Convert_InJsonModeWithUnwritableOutput_EndsWithErrorObject()
    {
        // Act
        (int code, string output, string error) = OutputRuns.Convert("json", "--output", MissingFolderOutput);

        // Assert
        Assert.Equal((int)ExitCode.OutputNotWritable, code);
        Assert.Equal(string.Empty, error);
        using JsonDocument last = JsonDocument.Parse(Lines(output)[^1]);
        Assert.Equal("error", last.RootElement.GetProperty("type").GetString());
        Assert.Equal(4, last.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("output-not-writable", last.RootElement.GetProperty("error").GetString());
    }

    /// <summary>
    /// A whole conversion in fancy mode with the output piped matches its golden file: the header, the report as tables,
    /// one static line per finished step, the timing table and the result, with no escape codes.
    /// </summary>
    [Fact]
    public void Convert_InFancyModePiped_MatchesGoldenWithoutEscapes()
    {
        // Act
        (int code, string output, string error) = OutputRuns.Convert("fancy");

        // Assert
        Assert.Equal((int)ExitCode.Success, code);
        Assert.Equal(string.Empty, error);
        Assert.DoesNotContain("\u001b[", output, StringComparison.Ordinal);
        Golden.Verify(Path.Combine("output", "fancy-conversion.txt"), output);
    }

    /// <summary>A failure prints the error on stderr in fancy mode too; stdout keeps what was shown before it.</summary>
    [Fact]
    public void Convert_InFancyModeWithUnwritableOutput_WritesErrorToStderr()
    {
        // Act
        (int code, string output, string error) = OutputRuns.Convert("fancy", "--output", MissingFolderOutput);

        // Assert
        Assert.Equal((int)ExitCode.OutputNotWritable, code);
        Assert.StartsWith("error Output directory doesn't exist:", error, StringComparison.Ordinal);
        Assert.Contains("(exit 4)", error, StringComparison.Ordinal);
        Assert.StartsWith("── mod2xgm", output, StringComparison.Ordinal);
    }

    /// <summary>The non-empty lines of <paramref name="text"/>.</summary>
    private static string[] Lines(string text) => text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The <c>type</c> of the JSON object on <paramref name="line"/>.</summary>
    private static string? TypeOf(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("type").GetString();
    }
}
