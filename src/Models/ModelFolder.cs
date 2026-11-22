using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;

namespace Mod2Xgm.Models;

/// <summary>
/// The folder of pretrained listening models the converter uses when it is there (plan T9.4s3,
/// docs/adr/0111-neither-and-listening-models.md, docs/adr/0138-model-folder.md): the ONNX files, ONNX Runtime's native
/// library and <see cref="ManifestName"/>, which says which models set and ONNX Runtime version the folder was made for.
/// A folder without that file isn't set up and is left alone; a folder with it that can't work is an error (exit 7).
/// </summary>
/// <param name="Path">The folder, in full.</param>
/// <param name="Models">The models the folder holds, as its manifest lists them.</param>
/// <param name="OnnxRuntimeVersion">The version of ONNX Runtime loaded from the folder.</param>
internal sealed record ModelFolder(string Path, IReadOnlyList<ModelFile> Models, string OnnxRuntimeVersion)
{
    /// <summary>
    /// The models set this converter was built for. Bump it when the converter needs other models or other exports of them,
    /// so an older folder is refused with a message saying what to get (docs/cli.md#listening-models).
    /// </summary>
    public const int ModelsSet = 1;

    /// <summary>The models set <see cref="ModelsSet"/> needs: CLAP only (ADR 0111).</summary>
    private static IReadOnlyList<string> RequiredModels { get; } = ["clap"];

    /// <summary>The file that says what the folder holds and what it was made for (docs/cli.md#listening-models).</summary>
    public const string ManifestName = "models.txt";

    /// <summary>The folder used without <c>--no-models</c>: <c>LocalApplicationData/Mod2Xgm/models</c>, as the harness's (ADR 0104).</summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mod2Xgm", "models");

    /// <summary>The ONNX Runtime version the converter was built against, without its build suffix: <c>1.32.0</c>.</summary>
    public static string ExpectedOnnxRuntimeVersion { get; } = PlainVersion(
        typeof(InferenceSession).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");

    /// <summary>The name of ONNX Runtime's native library on this system, as the folder holds it.</summary>
    public static string NativeLibraryName =>
        OperatingSystem.IsWindows() ? "onnxruntime.dll" : OperatingSystem.IsMacOS() ? "libonnxruntime.dylib" : "libonnxruntime.so";

    /// <summary>The one model set's name and file of <paramref name="name"/>, or <c>null</c> when the folder doesn't hold it.</summary>
    public ModelFile? Find(string name) => Models.FirstOrDefault(m => m.Name == name);

    /// <summary>
    /// Opens the model folder at <paramref name="path"/>: <c>null</c> when it doesn't exist or has no
    /// <see cref="ManifestName"/>, so it isn't set up. Otherwise checks the manifest, every model file's SHA-256 and ONNX
    /// Runtime's version, and loads ONNX Runtime from the folder.
    /// </summary>
    /// <exception cref="ConversionException">Exit 7: the folder is set up but can't be used, with what is wrong.</exception>
    public static ModelFolder? Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string folder = System.IO.Path.GetFullPath(path);
        string manifestPath = System.IO.Path.Combine(folder, ManifestName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        Manifest manifest = ReadManifest(folder, manifestPath);
        if (manifest.Set != ModelsSet)
        {
            throw Invalid(folder, $"it holds models set {manifest.Set}, and this version of Mod2Xgm needs set {ModelsSet}: get the models download made for it");
        }

        if (manifest.OnnxRuntime != ExpectedOnnxRuntimeVersion)
        {
            throw Invalid(folder, $"it holds ONNX Runtime {manifest.OnnxRuntime}, and this version of Mod2Xgm needs {ExpectedOnnxRuntimeVersion}: get the models download made for it");
        }

        foreach (string name in RequiredModels.Where(n => manifest.Models.All(m => m.Name != n)))
        {
            throw Invalid(folder, $"{ManifestName} doesn't list the {name} model, which models set {ModelsSet} needs");
        }

        foreach (ModelFile model in manifest.Models)
        {
            CheckFile(folder, model);
        }

        string native = System.IO.Path.Combine(folder, NativeLibraryName);
        if (!File.Exists(native))
        {
            throw Invalid(folder, $"{NativeLibraryName} (ONNX Runtime for this system) is missing; the models download for this system has it");
        }

        string loaded = OnnxRuntimeLoader.Load(native, folder);
        if (PlainVersion(loaded) != ExpectedOnnxRuntimeVersion)
        {
            throw Invalid(folder, $"its {NativeLibraryName} is ONNX Runtime {loaded}, and this version of Mod2Xgm needs {ExpectedOnnxRuntimeVersion}");
        }

        return new ModelFolder(folder, manifest.Models, PlainVersion(loaded));
    }

    /// <summary>The manifest's text for <paramref name="models"/>, as the models download writes it (tools/models-archive).</summary>
    public static string ManifestText(int set, string onnxRuntime, IEnumerable<ModelFile> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        List<string> lines =
        [
            "# Mod2Xgm listening models (docs/cli.md#listening-models). Made by tools/models-archive; don't edit.",
            string.Create(CultureInfo.InvariantCulture, $"set {set}"),
            "onnxruntime " + onnxRuntime,
            .. models.Select(m => $"model {m.Name} {m.FileName} {m.Sha256}"),
        ];
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>The SHA-256 of <paramref name="path"/>'s bytes, in lowercase hex.</summary>
    public static string Sha256Of(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>A version without its build suffix: <c>1.32.0+abc</c> → <c>1.32.0</c>.</summary>
    internal static string PlainVersion(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        return (plus < 0 ? version : version[..plus]).Trim();
    }

    private static Manifest ReadManifest(string folder, string manifestPath)
    {
        int? set = null;
        string? onnxRuntime = null;
        List<ModelFile> models = [];
        string[] lines = File.ReadAllLines(manifestPath);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (words)
            {
                case ["set", var number] when int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int n):
                    set = n;
                    break;
                case ["onnxruntime", var version]:
                    onnxRuntime = version;
                    break;
                case ["model", var name, var file, var sha] when IsFileName(file) && sha.Length == 64:
                    models.Add(new ModelFile(name, file, sha.ToLowerInvariant()));
                    break;
                default:
                    throw Invalid(folder, string.Create(CultureInfo.InvariantCulture, $"{ManifestName} line {i + 1} can't be read: \"{line}\""));
            }
        }

        if (set is not { } s || onnxRuntime is null)
        {
            throw Invalid(folder, $"{ManifestName} doesn't say which models set and ONNX Runtime version it holds");
        }

        return new Manifest(s, onnxRuntime, models);
    }

    /// <summary>A plain file name in the folder: no folder parts, so a manifest can't point outside it.</summary>
    private static bool IsFileName(string file) =>
        file.Length > 0 && file == System.IO.Path.GetFileName(file) && file is not ("." or "..");

    private static void CheckFile(string folder, ModelFile model)
    {
        string file = System.IO.Path.Combine(folder, model.FileName);
        if (!File.Exists(file))
        {
            throw Invalid(folder, $"{model.FileName} ({model.Name}) is missing");
        }

        if (Sha256Of(file) != model.Sha256)
        {
            throw Invalid(folder, $"{model.FileName} ({model.Name}) isn't the file {ManifestName} lists: it is damaged or from another download");
        }
    }

    private static ConversionException Invalid(string folder, string problem) =>
        new(ExitCode.ModelFolderInvalid, $"The model folder {folder} can't be used: {problem}. Fix it, or add --no-models to convert without it.");

    /// <summary>What <see cref="ManifestName"/> says.</summary>
    private sealed record Manifest(int Set, string OnnxRuntime, IReadOnlyList<ModelFile> Models);
}

/// <summary>One model in the folder, as the manifest lists it.</summary>
/// <param name="Name">The model's name: <c>clap</c>.</param>
/// <param name="FileName">Its ONNX file, in the folder.</param>
/// <param name="Sha256">The file's SHA-256, lowercase hex.</param>
internal sealed record ModelFile(string Name, string FileName, string Sha256);

/// <summary>
/// Loads ONNX Runtime's native library from the model folder, never from next to the executable (ADR 0111): the managed
/// API's imports are resolved to the library given the first time, for the life of the process. Where ONNX Runtime was
/// already used in the process (the harness), its own resolver stays and the version check decides.
/// </summary>
internal static class OnnxRuntimeLoader
{
    private static readonly Lock Gate = new();
    private static nint _handle;

    /// <summary>
    /// Loads <paramref name="library"/> (once per process; later calls keep the first) and returns ONNX Runtime's version
    /// string as the library reports it.
    /// </summary>
    /// <exception cref="ConversionException">Exit 7: the library can't be loaded.</exception>
    public static string Load(string library, string folder)
    {
        lock (Gate)
        {
            if (_handle == 0)
            {
                if (!NativeLibrary.TryLoad(library, out nint handle))
                {
                    throw new ConversionException(
                        ExitCode.ModelFolderInvalid,
                        $"The model folder {folder} can't be used: {Path.GetFileName(library)} doesn't load on this system; the models download for this system has the right one. Fix it, or add --no-models to convert without it.");
                }

                _handle = handle;
                try
                {
                    NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, Resolve);
                }
                catch (InvalidOperationException)
                {
                    // ONNX Runtime was already used in this process (the harness runs it too) and registered its own
                    // resolver. The library it loaded is the one the version check below asks; a mismatch is refused there.
                }
            }
        }

        return OrtEnv.Instance().GetVersionString();
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        name.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase) && !name.Contains("extensions", StringComparison.OrdinalIgnoreCase) ? _handle : 0;
}
