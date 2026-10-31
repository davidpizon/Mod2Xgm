using Mod2Xgm.Cli;

namespace Mod2Xgm.Tests.Cli;

public sealed class OutputPathsTests
{
    [Theory]
    [InlineData(nameof(OutputFormat.Dmf), ".dmf")]
    [InlineData(nameof(OutputFormat.Xgm), ".xgm")]
    [InlineData(nameof(OutputFormat.Xgm2), ".xgm2")]
    public void ExtensionMatchesTheFormat(string format, string expected) =>
        Assert.Equal(expected, OutputPaths.ExtensionFor(Enum.Parse<OutputFormat>(format)));

    [Theory]
    [InlineData(nameof(OutputFormat.Dmf), "song.dmf")]
    [InlineData(nameof(OutputFormat.Xgm), "song.xgm")]
    [InlineData(nameof(OutputFormat.Xgm2), "song.xgm2")]
    public void DefaultOutputIsNextToTheInput(string format, string expectedName)
    {
        using TempDirectory temp = new();
        ConversionOptions options = new() { InputPath = temp.Combine("song.mod"), Format = Enum.Parse<OutputFormat>(format) };

        Assert.Equal(temp.Combine(expectedName), OutputPaths.Resolve(options));
    }

    [Fact]
    public void ExplicitOutputIsUsedAsGiven()
    {
        using TempDirectory temp = new();
        ConversionOptions options = new()
        {
            InputPath = temp.Combine("song.mod"),
            Format = OutputFormat.Xgm2,
            OutputPath = temp.Combine("level1.bin"),
        };

        Assert.Equal(temp.Combine("level1.bin"), OutputPaths.Resolve(options));
    }

    [Fact]
    public void BlankOutputFallsBackToTheDefault()
    {
        using TempDirectory temp = new();
        ConversionOptions options = new() { InputPath = temp.Combine("song.mod"), OutputPath = "  " };

        Assert.Equal(temp.Combine("song.dmf"), OutputPaths.Resolve(options));
    }

    [Fact]
    public void SameFileComparesFullPaths()
    {
        using TempDirectory temp = new();
        string file = temp.Combine("a.mod");

        Assert.True(OutputPaths.SameFile(file, Path.Combine(temp.Path, ".", "a.mod")));
        Assert.False(OutputPaths.SameFile(file, temp.Combine("b.mod")));
    }

    /// <summary>The <c>--render-wav</c> file goes next to the output as <c>name.genesis.wav</c>, or where the option says.</summary>
    [Fact]
    public void RenderWavIsNextToTheOutputUnlessGiven()
    {
        using TempDirectory temp = new();
        ConversionOptions options = new() { InputPath = temp.Combine("song.mod"), RenderWav = true };
        ConversionOptions given = options with { RenderWavPath = temp.Combine("x.wav") };

        Assert.Equal(temp.Combine("out.genesis.wav"), OutputPaths.ResolveRenderWav(options, temp.Combine("out.dmf")));
        Assert.Equal(temp.Combine("x.wav"), OutputPaths.ResolveRenderWav(given, temp.Combine("out.dmf")));
    }
}
