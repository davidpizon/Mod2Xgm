using Mod2Xgm.Cli;
using Mod2Xgm.Output;

namespace Mod2Xgm.Tests.Output;

/// <summary>Tests of <see cref="ConversionCommand.Phases"/>: which steps a run takes for each option mix (docs/cli-output.md#phases).</summary>
public sealed class ConversionPhasesTests
{
    /// <summary>Each option mix has its own steps, in order.</summary>
    [Theory]
    [InlineData("dmf", false, false, "check read scan model play map write")]
    [InlineData("dmf", true, false, "check read scan model play analyze map write")]
    [InlineData("dmf", false, true, "check read scan model play map write render")]
    [InlineData("xgm2", false, true, "check read scan model play map write render")]
    [InlineData("xgm", true, true, "check read scan model play analyze map render")]
    public void Phases_WithOptionMix_ReturnsItsSteps(string format, bool analyzes, bool renderWav, string expected)
    {
        // Arrange
        ConversionOptions options = new()
        {
            InputPath = "song.mod",
            Format = Enum.Parse<OutputFormat>(format, ignoreCase: true),
            RenderWav = renderWav,
            InfoOnly = true,
        };

        // Act
        IReadOnlyList<PhaseInfo> phases = ConversionCommand.Phases(options, analyzes);

        // Assert
        Assert.Equal(expected, string.Join(' ', phases.Select(p => p.Id)));
    }

    /// <summary>The steps are numbered from 1 within the run, whatever the mix.</summary>
    [Fact]
    public void Phases_WithAnyMix_NumbersStepsFromOne()
    {
        // Arrange
        ConversionOptions options = new() { InputPath = "song.mod", Format = OutputFormat.Xgm, RenderWav = true, InfoOnly = true };

        // Act
        IReadOnlyList<PhaseInfo> phases = ConversionCommand.Phases(options, analyzes: true);

        // Assert
        Assert.Equal(Enumerable.Range(1, phases.Count), phases.Select(p => p.Number));
    }
}
