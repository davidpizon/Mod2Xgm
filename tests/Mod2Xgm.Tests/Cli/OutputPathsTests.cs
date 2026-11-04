using Mod2Xgm.Cli;

namespace Mod2Xgm.Tests.Cli;

/// <summary>Tests for <see cref="OutputPaths"/>: where the output and the WAV render go. Pure path work, no files.</summary>
public sealed class OutputPathsTests
{
    /// <summary>A folder the tests name paths in; nothing is created there.</summary>
    private static readonly string Root = Path.GetFullPath("output-paths-tests");

    /// <summary>Each format has its own extension.</summary>
    [Theory]
    [InlineData(nameof(OutputFormat.Dmf), ".dmf")]
    [InlineData(nameof(OutputFormat.Xgm), ".xgm")]
    [InlineData(nameof(OutputFormat.Xgm2), ".xgm2")]
    public void ExtensionFor_WithFormat_ReturnsItsExtension(string format, string expected)
    {
        // Act
        string extension = OutputPaths.ExtensionFor(Enum.Parse<OutputFormat>(format));

        // Assert
        Assert.Equal(expected, extension);
    }

    /// <summary>Without <c>--output</c>, the output goes next to the input with the format's extension.</summary>
    [Theory]
    [InlineData(nameof(OutputFormat.Dmf), "song.dmf")]
    [InlineData(nameof(OutputFormat.Xgm), "song.xgm")]
    [InlineData(nameof(OutputFormat.Xgm2), "song.xgm2")]
    public void Resolve_WithoutOutputPath_ReturnsPathNextToInput(string format, string expectedName)
    {
        // Arrange
        ConversionOptions options = new() { InputPath = Path.Combine(Root, "song.mod"), Format = Enum.Parse<OutputFormat>(format) };

        // Act
        string path = OutputPaths.Resolve(options);

        // Assert
        Assert.Equal(Path.Combine(Root, expectedName), path);
    }

    /// <summary>An explicit <c>--output</c> is used as given, whatever its extension.</summary>
    [Fact]
    public void Resolve_WithOutputPath_ReturnsItAsGiven()
    {
        // Arrange
        ConversionOptions options = new()
        {
            InputPath = Path.Combine(Root, "song.mod"),
            Format = OutputFormat.Xgm2,
            OutputPath = Path.Combine(Root, "level1.bin"),
        };

        // Act
        string path = OutputPaths.Resolve(options);

        // Assert
        Assert.Equal(Path.Combine(Root, "level1.bin"), path);
    }

    /// <summary>A blank <c>--output</c> falls back to the default.</summary>
    [Fact]
    public void Resolve_WithBlankOutputPath_ReturnsDefault()
    {
        // Arrange
        ConversionOptions options = new() { InputPath = Path.Combine(Root, "song.mod"), OutputPath = "  " };

        // Act
        string path = OutputPaths.Resolve(options);

        // Assert
        Assert.Equal(Path.Combine(Root, "song.dmf"), path);
    }

    /// <summary>Two spellings of the same full path are the same file.</summary>
    [Fact]
    public void SameFile_WithEquivalentPaths_ReturnsTrue()
    {
        // Act
        bool same = OutputPaths.SameFile(Path.Combine(Root, "a.mod"), Path.Combine(Root, ".", "a.mod"));

        // Assert
        Assert.True(same);
    }

    /// <summary>Different names are different files.</summary>
    [Fact]
    public void SameFile_WithDifferentNames_ReturnsFalse()
    {
        // Act
        bool same = OutputPaths.SameFile(Path.Combine(Root, "a.mod"), Path.Combine(Root, "b.mod"));

        // Assert
        Assert.False(same);
    }

    /// <summary>The <c>--render-wav</c> file goes next to the output as <c>name.genesis.wav</c>.</summary>
    [Fact]
    public void ResolveRenderWav_WithoutPath_ReturnsGenesisWavNextToOutput()
    {
        // Arrange
        ConversionOptions options = new() { InputPath = Path.Combine(Root, "song.mod"), RenderWav = true };

        // Act
        string path = OutputPaths.ResolveRenderWav(options, Path.Combine(Root, "out.dmf"));

        // Assert
        Assert.Equal(Path.Combine(Root, "out.genesis.wav"), path);
    }

    /// <summary>A path given to <c>--render-wav</c> is used as given.</summary>
    [Fact]
    public void ResolveRenderWav_WithPath_ReturnsItAsGiven()
    {
        // Arrange
        ConversionOptions options = new() { InputPath = Path.Combine(Root, "song.mod"), RenderWav = true, RenderWavPath = Path.Combine(Root, "x.wav") };

        // Act
        string path = OutputPaths.ResolveRenderWav(options, Path.Combine(Root, "out.dmf"));

        // Assert
        Assert.Equal(Path.Combine(Root, "x.wav"), path);
    }
}
