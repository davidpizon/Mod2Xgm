using System.Text.Json;

namespace Mod2Xgm.Tests.Output;

/// <summary>Whole command-line runs that check the phases a conversion walks through (plan T17.5, docs/cli-output.md#phases).</summary>
[Collection(ProgramState.Name)]
[Trait("Category", "Integration")]
public sealed class ConversionRunPhasesTests
{
    /// <summary>A whole conversion in text mode names each step of its plan, in order.</summary>
    [Fact]
    public void Run_InTextMode_StartsEveryStepInOrder()
    {
        // Act
        (int code, string output, _) = OutputRuns.Convert("text", "--verbose", "--render-wav");

        // Assert
        Assert.Equal((int)ExitCode.Success, code);
        string[] steps = [.. output.Split('\n').Where(l => l.StartsWith("step ", StringComparison.Ordinal) && l.Contains(" start ", StringComparison.Ordinal))];
        Assert.Equal(["check", "read", "scan", "model", "play", "analyze", "map", "write", "render"], steps.Select(l => l.Split(' ')[2]));
        Assert.StartsWith("step 1/9 check start", steps[0], StringComparison.Ordinal);
    }

    /// <summary>A whole conversion in text mode ends with every step's time.</summary>
    [Fact]
    public void Run_InTextMode_EndsWithEveryStepsTime()
    {
        // Act
        (_, string output, _) = OutputRuns.Convert("text", "--verbose", "--render-wav");

        // Assert
        Assert.Contains("\ntime check=#s read=#s scan=#s model=#s play=#s analyze=#s map=#s write=#s render=#s total=#s", output, StringComparison.Ordinal);
    }

    /// <summary>The counted steps reach their totals in a whole run.</summary>
    [Fact]
    public void Run_InJsonMode_EveryStepEndsWithDoneEqualToTotal()
    {
        // Act
        (int code, string output, _) = OutputRuns.Convert("json", "--verbose", "--render-wav");

        // Assert
        Assert.Equal((int)ExitCode.Success, code);
        string[] ends = EndEvents(output);
        Assert.Equal(9, ends.Length);
        Assert.All(ends, end =>
        {
            using JsonDocument document = JsonDocument.Parse(end);
            Assert.Equal(document.RootElement.GetProperty("total").GetInt64(), document.RootElement.GetProperty("done").GetInt64());
        });
    }

    /// <summary>The render counts its ticks and the analysis its six samples.</summary>
    [Fact]
    public void Run_InJsonMode_RenderAndAnalyzeCountTheirWork()
    {
        // Act
        (_, string output, _) = OutputRuns.Convert("json", "--verbose", "--render-wav");

        // Assert
        string[] ends = EndEvents(output);
        Assert.Contains(ends, e => e.Contains("\"id\":\"render\"", StringComparison.Ordinal) && !e.Contains("\"total\":0", StringComparison.Ordinal));
        Assert.Contains(ends, e => e.Contains("\"id\":\"analyze\"", StringComparison.Ordinal) && e.Contains("\"total\":6", StringComparison.Ordinal));
    }

    /// <summary>The <c>json</c> lines of phase-end events.</summary>
    private static string[] EndEvents(string output) =>
        [.. output.Split('\n').Where(l => l.Contains("\"event\":\"end\"", StringComparison.Ordinal))];
}
