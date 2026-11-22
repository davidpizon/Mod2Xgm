using System.Runtime.InteropServices;
using System.Xml.Linq;
using Mod2Xgm.Cli;
using Mod2Xgm.Models;
using Serilog.Events;

namespace Mod2Xgm.Tests.Models;

/// <summary>
/// Tests for <see cref="ModelFolder"/> (plan T9.4s3, docs/adr/0138-model-folder.md): a folder that isn't set up is left
/// alone, one that is set up but can't work stops with exit 7 saying why, and a complete one loads ONNX Runtime from the
/// folder and is named on every run that uses it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ModelFolderTests
{
    /// <summary>The bytes the stand-in model file holds: the checks hash it, nothing runs it yet.</summary>
    private static readonly byte[] StandInModel = [1, 2, 3, 4];

    /// <summary>A folder that doesn't exist isn't set up: no models, no error.</summary>
    [Fact]
    public void Open_WithMissingFolder_ReturnsNull()
    {
        // Arrange
        using TempDirectory temp = new();

        // Act
        ModelFolder? sut = ModelFolder.Open(temp.Combine("none"));

        // Assert
        Assert.Null(sut);
    }

    /// <summary>A folder with models but no models.txt (the harness's, ADR 0104) isn't set up for the converter.</summary>
    [Fact]
    public void Open_WithoutManifest_ReturnsNull()
    {
        // Arrange
        using TempDirectory temp = new();
        temp.CreateFile("clap.onnx", StandInModel);

        // Act
        ModelFolder? sut = ModelFolder.Open(temp.Path);

        // Assert
        Assert.Null(sut);
    }

    /// <summary>A complete folder loads ONNX Runtime from itself and lists its models and the runtime's version.</summary>
    [Fact]
    public void Open_WithCompleteFolder_ReturnsModelsAndVersion()
    {
        // Act
        ModelFolder? sut = ModelFolder.Open(LoadedModelFolder.Path);

        // Assert
        Assert.NotNull(sut);
        Assert.Equal("clap", Assert.Single(sut.Models).Name);
        Assert.Equal(ModelFolder.ExpectedOnnxRuntimeVersion, sut.OnnxRuntimeVersion);
        Assert.NotNull(sut.Find("clap"));
    }

    /// <summary>A folder made for another models set is refused, saying which set this version needs.</summary>
    [Fact]
    public void Open_WithOtherModelsSet_ThrowsModelFolderInvalid()
    {
        // Arrange
        using TempDirectory temp = new();
        Complete(temp, set: ModelFolder.ModelsSet + 1);

        // Act
        ConversionException error = Assert.Throws<ConversionException>(() => ModelFolder.Open(temp.Path));

        // Assert
        Assert.Equal(ExitCode.ModelFolderInvalid, error.Code);
        Assert.Contains($"needs set {ModelFolder.ModelsSet}", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A folder whose manifest names another ONNX Runtime version is refused.</summary>
    [Fact]
    public void Open_WithOtherOnnxRuntimeVersion_ThrowsModelFolderInvalid()
    {
        // Arrange
        using TempDirectory temp = new();
        Complete(temp, onnxRuntime: "0.1.0");

        // Act
        ConversionException error = Assert.Throws<ConversionException>(() => ModelFolder.Open(temp.Path));

        // Assert
        Assert.Equal(ExitCode.ModelFolderInvalid, error.Code);
        Assert.Contains("ONNX Runtime 0.1.0", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A model file that isn't the one the manifest lists (damaged or swapped) is refused.</summary>
    [Fact]
    public void Open_WithDamagedModel_ThrowsModelFolderInvalid()
    {
        // Arrange
        using TempDirectory temp = new();
        Complete(temp);
        temp.CreateFile("clap.onnx", "damaged"u8.ToArray());

        // Act
        ConversionException error = Assert.Throws<ConversionException>(() => ModelFolder.Open(temp.Path));

        // Assert
        Assert.Equal(ExitCode.ModelFolderInvalid, error.Code);
        Assert.Contains("damaged", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A model the manifest lists but the folder lacks is refused.</summary>
    [Fact]
    public void Open_WithMissingModelFile_ThrowsModelFolderInvalid()
    {
        // Arrange
        using TempDirectory temp = new();
        Complete(temp);
        File.Delete(temp.Combine("clap.onnx"));

        // Act
        ConversionException error = Assert.Throws<ConversionException>(() => ModelFolder.Open(temp.Path));

        // Assert
        Assert.Equal(ExitCode.ModelFolderInvalid, error.Code);
        Assert.Contains("clap.onnx (clap) is missing", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A manifest without CLAP, which models set 1 needs, is refused.</summary>
    [Fact]
    public void Open_WithoutClap_ThrowsModelFolderInvalid()
    {
        // Arrange
        using TempDirectory temp = new();
        temp.CreateFile(ModelFolder.ManifestName, ModelFolder.ManifestText(ModelFolder.ModelsSet, ModelFolder.ExpectedOnnxRuntimeVersion, []).Select(c => (byte)c).ToArray());

        // Act
        ConversionException error = Assert.Throws<ConversionException>(() => ModelFolder.Open(temp.Path));

        // Assert
        Assert.Equal(ExitCode.ModelFolderInvalid, error.Code);
        Assert.Contains("doesn't list the clap model", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A manifest line the converter can't read is refused, with its line number.</summary>
    [Fact]
    public void Open_WithUnreadableLine_ThrowsModelFolderInvalid()
    {
        // Arrange
        using TempDirectory temp = new();
        File.WriteAllText(temp.Combine(ModelFolder.ManifestName), "set 1\nonnxruntime 1.0.0\nsomething else\n");

        // Act
        ConversionException error = Assert.Throws<ConversionException>(() => ModelFolder.Open(temp.Path));

        // Assert
        Assert.Equal(ExitCode.ModelFolderInvalid, error.Code);
        Assert.Contains("line 3", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A model file name with a folder in it can't point outside the model folder: the line is refused.</summary>
    [Fact]
    public void Open_WithFileOutsideFolder_ThrowsModelFolderInvalid()
    {
        // Arrange
        using TempDirectory temp = new();
        File.WriteAllText(temp.Combine(ModelFolder.ManifestName), $"set 1\nonnxruntime 1.0.0\nmodel clap ../clap.onnx {new string('0', 64)}\n");

        // Act
        ConversionException error = Assert.Throws<ConversionException>(() => ModelFolder.Open(temp.Path));

        // Assert
        Assert.Equal(ExitCode.ModelFolderInvalid, error.Code);
        Assert.Contains("line 3", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A manifest that doesn't say its models set is refused.</summary>
    [Fact]
    public void Open_WithoutSetLine_ThrowsModelFolderInvalid()
    {
        // Arrange
        using TempDirectory temp = new();
        File.WriteAllText(temp.Combine(ModelFolder.ManifestName), "# nothing\n");

        // Act
        ConversionException error = Assert.Throws<ConversionException>(() => ModelFolder.Open(temp.Path));

        // Assert
        Assert.Equal(ExitCode.ModelFolderInvalid, error.Code);
        Assert.Contains("doesn't say which models set", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A folder without ONNX Runtime for this system is refused, naming the library.</summary>
    [Fact]
    public void Open_WithoutNativeLibrary_ThrowsModelFolderInvalid()
    {
        // Arrange
        using TempDirectory temp = new();
        Complete(temp);
        File.Delete(temp.Combine(ModelFolder.NativeLibraryName));

        // Act
        ConversionException error = Assert.Throws<ConversionException>(() => ModelFolder.Open(temp.Path));

        // Assert
        Assert.Equal(ExitCode.ModelFolderInvalid, error.Code);
        Assert.Contains(ModelFolder.NativeLibraryName + " (ONNX Runtime for this system) is missing", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A version's build suffix is dropped before versions are compared.</summary>
    [Fact]
    public void PlainVersion_WithBuildSuffix_DropsIt()
    {
        // Act
        string sut = ModelFolder.PlainVersion("1.32.0+abc123");

        // Assert
        Assert.Equal("1.32.0", sut);
    }

    /// <summary>
    /// The converter's ONNX Runtime (the managed package) and the native package the harness, the tests and
    /// tools/models-archive take the library from are pinned to one version, which the converter expects.
    /// </summary>
    [Fact]
    public void ExpectedOnnxRuntimeVersion_MatchesBothPackages()
    {
        // Arrange
        XDocument props = XDocument.Load(Path.Combine(RepoPaths.Root, "Directory.Packages.props"));
        string? Version(string package) => props.Descendants("PackageVersion").FirstOrDefault(p => (string?)p.Attribute("Include") == package)?.Attribute("Version")?.Value;

        // Act
        string sut = ModelFolder.ExpectedOnnxRuntimeVersion;

        // Assert
        Assert.Equal(Version("Microsoft.ML.OnnxRuntime.Managed"), sut);
        Assert.Equal(Version("Microsoft.ML.OnnxRuntime"), sut);
    }

    /// <summary>A run with a complete model folder says which models it uses and from where.</summary>
    [Fact]
    public void Execute_WithModelFolder_LogsModelsLine()
    {
        // Arrange
        using TempDirectory temp = new();
        ConversionOptions options = new() { Quality = Quality.Fast, InputPath = temp.CreateFile("song.mod"), ModelsDirectory = LoadedModelFolder.Path };
        CollectingSink sink = new();

        // Act
        ConversionCommand.Execute(options, sink.CreateLogger());

        // Assert
        Assert.Contains(sink.Messages(LogEventLevel.Information), m => m.StartsWith("Listening models: clap (models set 1, ONNX Runtime ", StringComparison.Ordinal) && m.EndsWith(LoadedModelFolder.Path, StringComparison.Ordinal));
    }

    /// <summary>A run without a model folder prints no models line.</summary>
    [Fact]
    public void Execute_WithoutModelFolder_LogsNoModelsLine()
    {
        // Arrange
        using TempDirectory temp = new();
        ConversionOptions options = new() { Quality = Quality.Fast, InputPath = temp.CreateFile("song.mod") };
        CollectingSink sink = new();

        // Act
        ConversionCommand.Execute(options, sink.CreateLogger());

        // Assert
        Assert.DoesNotContain(sink.Messages(LogEventLevel.Information), m => m.StartsWith("Listening models", StringComparison.Ordinal));
    }

    /// <summary>A model folder that can't work stops the run with exit 7 before anything is written.</summary>
    [Fact]
    public void Execute_WithBrokenModelFolder_ThrowsModelFolderInvalidAndWritesNothing()
    {
        // Arrange
        using TempDirectory temp = new();
        using TempDirectory models = new();
        File.WriteAllText(models.Combine(ModelFolder.ManifestName), "set 1\n");
        ConversionOptions options = new() { Quality = Quality.Fast, InputPath = temp.CreateFile("song.mod"), ModelsDirectory = models.Path };

        // Act
        ConversionException error = Assert.Throws<ConversionException>(() => ConversionCommand.Execute(options, new CollectingSink().CreateLogger()));

        // Assert
        Assert.Equal(ExitCode.ModelFolderInvalid, error.Code);
        Assert.False(File.Exists(temp.Combine("song.dmf")));
    }

    /// <summary>
    /// Sets <paramref name="temp"/> up as a model folder: a stand-in CLAP file, ONNX Runtime's native library from the test
    /// binaries, and a manifest for <paramref name="set"/> and <paramref name="onnxRuntime"/>. ONNX Runtime is loaded from
    /// <see cref="LoadedModelFolder"/> first, so this folder's copy is never loaded and can be deleted.
    /// </summary>
    private static void Complete(TempDirectory temp, int set = ModelFolder.ModelsSet, string? onnxRuntime = null)
    {
        _ = LoadedModelFolder.Path;
        Fill(temp, set, onnxRuntime);
    }

    /// <summary>Puts a stand-in CLAP file, ONNX Runtime and a manifest in <paramref name="temp"/>, loading nothing.</summary>
    internal static void Fill(TempDirectory temp, int set = ModelFolder.ModelsSet, string? onnxRuntime = null)
    {
        string model = temp.CreateFile("clap.onnx", StandInModel);
        File.Copy(NativeLibrary(), temp.Combine(ModelFolder.NativeLibraryName));
        ModelFile clap = new("clap", "clap.onnx", ModelFolder.Sha256Of(model));
        File.WriteAllText(temp.Combine(ModelFolder.ManifestName), ModelFolder.ManifestText(set, onnxRuntime ?? ModelFolder.ExpectedOnnxRuntimeVersion, [clap]));
    }

    /// <summary>ONNX Runtime's native library for this system, as the Microsoft.ML.OnnxRuntime package puts it next to the tests.</summary>
    private static string NativeLibrary()
    {
        string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        return Path.Combine(AppContext.BaseDirectory, "runtimes", os + "-" + arch, "native", ModelFolder.NativeLibraryName);
    }
}

/// <summary>
/// One complete model folder for <see cref="ModelFolderTests"/>, opened before any other so ONNX Runtime is loaded from
/// it: a loaded library stays locked on Windows for the life of the process, so the other tests' copies of it are never
/// loaded and their folders can be deleted. The folder itself stays in the temporary folder.
/// </summary>
internal static class LoadedModelFolder
{
    private static readonly Lazy<string> Folder = new(Create);

    /// <summary>The folder's full path; the first use sets it up and loads ONNX Runtime from it.</summary>
    public static string Path => Folder.Value;

    private static string Create()
    {
        TempDirectory temp = new();
        ModelFolderTests.Fill(temp);
        _ = ModelFolder.Open(temp.Path);
        return temp.Path;
    }
}
