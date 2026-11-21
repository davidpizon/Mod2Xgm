using System.Security.Cryptography;
using System.Text;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>
/// A fingerprint of the source code an FM fit depends on (docs/adr/0136-fm-fits-across-builds.md): FM fits are kept
/// across builds until <c>CandidateCache.FmFitterVersion</c> changes, so a change to this code must bump it.
/// </summary>
internal static class FmCodeFingerprint
{
    /// <summary>The fingerprint <c>CandidateCache.FmFitterVersion</c> was last set for.</summary>
    public const string Expected = "4aa7d9b220152f9a9c0af2e50e613579bf6690220c9b8504223ccd961b2b4b10";

    /// <summary>
    /// The files whose code can change a fit, relative to the repository root: the searches, the evaluator and its note
    /// renders and scores, the analysis of those renders, the chip model, the library seeds and the cache's file format.
    /// </summary>
    private static IReadOnlyList<string> Files { get; } =
    [
        "src/Analysis/SampleAnalyzer.cs",
        "src/Mapping/FmPatchLibrary.cs",
        "src/Optimizer/CandidateCodec.cs",
        "src/Optimizer/FmEvaluator.cs",
        "src/Optimizer/FmExhaustiveSearch.cs",
        "src/Optimizer/FmFitOptions.cs",
        "src/Optimizer/FmFitter.cs",
        "src/Optimizer/FmGeneticSearch.cs",
        "src/Optimizer/FmLocalSearch.cs",
        "src/Optimizer/FmNoteRenderer.cs",
        "src/Optimizer/FmPitch.cs",
        "src/Optimizer/FmStructure.cs",
        "src/Optimizer/NoteScorer.cs",
        "src/Render/Fm/FmChannel.cs",
        "src/Render/Fm/FmEngine.cs",
        "src/Render/Fm/FmOperator.cs",
        "src/Render/Fm/FmTables.cs",
        "src/Render/Fm/OpnRegisters.cs",
        "src/Render/Fm/Ym3438.cs",
        "src/Scoring/FftPlan.cs",
        "src/Scoring/MelAnalyzer.cs",
    ];

    /// <summary>The repository checkout the tests run from.</summary>
    public static string RepositoryRoot() => RepoPaths.Root;

    /// <summary>
    /// SHA-256 over each of <see cref="Files"/>' path and text under <paramref name="root"/>, line endings made LF so a
    /// Windows checkout and a Linux one agree; as lowercase hex.
    /// </summary>
    public static string Of(string root)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in Files)
        {
            string text = File.ReadAllText(Path.Combine(root, file)).Replace("\r\n", "\n", StringComparison.Ordinal);
            hash.AppendData(Encoding.UTF8.GetBytes(file + "\n" + text + "\n"));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
