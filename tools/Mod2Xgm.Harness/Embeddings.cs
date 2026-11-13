using System.Globalization;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Mod2Xgm.Render;
using Mod2Xgm.Scoring;
using Serilog;

namespace Mod2Xgm.Harness;

/// <summary>
/// A pretrained listening model the harness tries as an extra feature (plan T9.4k4, docs/adr/0104-pretrained-embeddings.md),
/// as <c>tools/onnx-export/export_models.py</c> exports it.
/// </summary>
/// <param name="Name">The model's name and its ONNX file's: <c>panns.onnx</c>.</param>
/// <param name="RateHz">The rate the model listens at.</param>
/// <param name="ChunkSamples">How many samples it takes at once: the length of every chunk compared.</param>
/// <param name="GivesDistance">True when the model takes two clips and gives their distance (CDPAM); false when it gives one clip's embedding.</param>
internal sealed record EmbeddingModel(string Name, int RateHz, int ChunkSamples, bool GivesDistance)
{
    /// <summary>The input of an embedding model, and the output names of each kind (export_models.py).</summary>
    public const string AudioInput = "audio";

    /// <summary>A distance model's input for the original's chunk.</summary>
    public const string ReferenceInput = "reference";

    /// <summary>A distance model's input for the version's chunk.</summary>
    public const string TestInput = "test";

    /// <summary>An embedding model's output.</summary>
    public const string EmbeddingOutput = "embedding";

    /// <summary>A distance model's output.</summary>
    public const string DistanceOutput = "distance";

    /// <summary>The models tried, as export_models.py's <c>MODELS</c> (David chose all four, 2026-11-11).</summary>
    public static IReadOnlyList<EmbeddingModel> All { get; } =
    [
        new("panns", 32_000, 64_000, false),
        new("vggish", 16_000, 15_600, false),
        new("clap", 48_000, 480_000, false),
        new("cdpam", 22_050, 44_100, true),
    ];

    /// <summary>The feature's name in the report: <c>panns_distance</c>.</summary>
    public string FeatureName => Name + "_distance";

    /// <summary>The ONNX file's name.</summary>
    public string FileName => Name + ".onnx";

    /// <summary>The file of the output the export expects for the test signal.</summary>
    public string ExpectedFileName => Name + ".expected.txt";

    /// <summary>The folder the export script writes to and the harness reads by default: <c>LocalApplicationData/Mod2Xgm/models</c>.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mod2Xgm", "models");

    /// <summary>
    /// The export's test signal: <c>0.1 sin(2π 440 t)</c>, plus <c>0.02 sin(2π 3000 t)</c> for a distance model's test
    /// input, one chunk at the model's rate.
    /// </summary>
    public float[] CheckSignal(bool second = false)
    {
        float[] x = new float[ChunkSamples];
        for (int i = 0; i < x.Length; i++)
        {
            double t = (double)i / RateHz;
            double v = 0.1 * Math.Sin(2 * Math.PI * 440 * t);
            if (second)
            {
                v += 0.02 * Math.Sin(2 * Math.PI * 3000 * t);
            }

            x[i] = (float)v;
        }

        return x;
    }
}

/// <summary>A pretrained model ready to run on chunks of <see cref="EmbeddingModel.ChunkSamples"/> samples.</summary>
internal interface IListeningModel : IDisposable
{
    /// <summary>Which model it is.</summary>
    EmbeddingModel Model { get; }

    /// <summary>The embedding of one chunk; only for a model that gives embeddings.</summary>
    float[] Embed(float[] chunk);

    /// <summary>The distance from <paramref name="reference"/> to <paramref name="test"/>; only for a model that gives distances.</summary>
    double Distance(float[] reference, float[] test);
}

/// <summary>A model exported to ONNX, run in process through ONNX Runtime on the CPU.</summary>
internal sealed class OnnxListeningModel : IListeningModel
{
    private readonly InferenceSession _session;

    /// <summary>Loads <paramref name="model"/>'s ONNX file from <paramref name="folder"/>.</summary>
    public OnnxListeningModel(EmbeddingModel model, string folder)
    {
        ArgumentNullException.ThrowIfNull(model);
        Model = model;
        _session = new InferenceSession(Path.Combine(folder, model.FileName));
    }

    /// <inheritdoc/>
    public EmbeddingModel Model { get; }

    /// <inheritdoc/>
    public float[] Embed(float[] chunk) => Run([EmbeddingModel.AudioInput], [chunk], EmbeddingModel.EmbeddingOutput);

    /// <inheritdoc/>
    public double Distance(float[] reference, float[] test) =>
        Run([EmbeddingModel.ReferenceInput, EmbeddingModel.TestInput], [reference, test], EmbeddingModel.DistanceOutput)[0];

    /// <inheritdoc/>
    public void Dispose() => _session.Dispose();

    private float[] Run(string[] names, float[][] chunks, string output)
    {
        foreach (float[] chunk in chunks)
        {
            if (chunk.Length != Model.ChunkSamples)
            {
                throw new ArgumentException($"{Model.Name} takes {Model.ChunkSamples} samples, not {chunk.Length}", nameof(chunks));
            }
        }

        OrtValue[] inputs = [.. chunks.Select(c => OrtValue.CreateTensorValueFromMemory(c, [1, c.Length]))];
        try
        {
            using RunOptions options = new();
            using IDisposableReadOnlyCollection<OrtValue> results = _session.Run(options, names, inputs, [output]);
            return results[0].GetTensorDataAsSpan<float>().ToArray();
        }
        finally
        {
            foreach (OrtValue input in inputs)
            {
                input.Dispose();
            }
        }
    }
}

/// <summary>
/// How far a version's clip is from the original's by a pretrained model, chunk by chunk (David, 2026-11-11): both clips are
/// cut into the model's chunks, each chunk is compared with the same chunk of the original, and the distances are averaged,
/// so a short difference is never averaged away before it is measured.
/// </summary>
internal static class EmbeddingDistance
{
    /// <summary>
    /// The mean distance from <paramref name="original"/> to <paramref name="version"/> by <paramref name="model"/>: the
    /// cosine distance between embeddings, or the model's own distance.
    /// </summary>
    public static double Measure(IListeningModel model, StereoAudio original, StereoAudio version)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(version);
        int rate = model.Model.RateHz;
        IReadOnlyList<float[]> a = Chunks(Mono(original.At(rate)), model.Model.ChunkSamples);
        IReadOnlyList<float[]> b = Chunks(Mono(version.At(rate)), model.Model.ChunkSamples);
        int count = Math.Min(a.Count, b.Count);
        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            sum += model.Model.GivesDistance ? model.Distance(a[i], b[i]) : CosineDistance(model.Embed(a[i]), model.Embed(b[i]));
        }

        return sum / count;
    }

    /// <summary>Both channels averaged: what the scorer compares too (ADR 0042).</summary>
    public static float[] Mono(StereoAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        float[] mono = new float[audio.Length];
        for (int i = 0; i < mono.Length; i++)
        {
            mono[i] = (audio.Left[i] + audio.Right[i]) / 2;
        }

        return mono;
    }

    /// <summary>
    /// <paramref name="audio"/> cut into whole chunks of <paramref name="length"/> samples, the rest dropped; one chunk padded
    /// with silence when the audio is shorter than one.
    /// </summary>
    public static IReadOnlyList<float[]> Chunks(float[] audio, int length)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (audio.Length < length)
        {
            float[] padded = new float[length];
            audio.CopyTo(padded, 0);
            return [padded];
        }

        return [.. Enumerable.Range(0, audio.Length / length).Select(i => audio[(i * length)..((i + 1) * length)])];
    }

    /// <summary>1 − the cosine of the angle between <paramref name="a"/> and <paramref name="b"/>: 0 alike, 2 opposite; 0 when both are silent, 1 when one is.</summary>
    public static double CosineDistance(float[] a, float[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"embeddings of {a.Length} and {b.Length} values", nameof(b));
        }

        double dot = 0;
        double aa = 0;
        double bb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            aa += (double)a[i] * a[i];
            bb += (double)b[i] * b[i];
        }

        return aa == 0 && bb == 0 ? 0 : aa == 0 || bb == 0 ? 1 : 1 - (dot / Math.Sqrt(aa * bb));
    }
}

/// <summary>One answer with each tried model's distance for both versions (NaN where a model is missing).</summary>
/// <param name="Answer">The answer and its <c>src/Scoring</c> features.</param>
/// <param name="A">Version A's distance by each model, in the order of the models measured.</param>
/// <param name="B">Version B's, likewise.</param>
internal sealed record EmbeddingAnswer(FitAnswer Answer, IReadOnlyList<double> A, IReadOnlyList<double> B);

/// <summary>
/// <c>--embeddings</c> (docs/harness.md#pretrained-models): after the remake, measures every answered row's clips with each
/// pretrained model found in the models folder and compares them, on leave-one-song-out checks, with log-mel and today's
/// learned score. Writes <c>listening/embeddings.tsv</c> and <c>listening/embeddings.txt</c>; never touches
/// <c>src/Scoring/ListeningWeights.cs</c>, since the converter can't run these models (David, 2026-11-11).
/// </summary>
internal static class EmbeddingsRun
{
    /// <summary>
    /// Measures and compares on <paramref name="remake"/>'s rows with the models in <paramref name="modelsFolder"/>,
    /// <paramref name="open"/> loading each. Always 0: missing models and rows left out are listed.
    /// </summary>
    public static int Run(RemakeResult remake, string outputDirectory, string modelsFolder, Func<EmbeddingModel, IListeningModel> open, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(remake);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(logger);
        string folder = Path.Combine(outputDirectory, "listening");
        Directory.CreateDirectory(folder);
        List<string> lines = [];
        List<IListeningModel> models = [];
        try
        {
            foreach (EmbeddingModel model in EmbeddingModel.All)
            {
                if (File.Exists(Path.Combine(modelsFolder, model.FileName)))
                {
                    models.Add(open(model));
                }
                else
                {
                    lines.Add($"Model {model.Name}: not found in {modelsFolder} (tools/onnx-export/README.md)");
                }
            }

            List<EmbeddingAnswer> answers = [];
            StringBuilder table = new StringBuilder("row\tversion").AppendJoin(string.Empty, models.Select(m => "\t" + m.Model.FeatureName)).Append('\n');
            foreach (RemadeRow r in remake.Rows)
            {
                if (r is not { Heard: true, A: { } a, B: { } b, Row.Pick.Length: > 0 })
                {
                    continue;
                }

                StereoAudio original = WavFile.Read(Path.Combine(folder, r.Row.Name + "-o.wav"));
                double[] distancesA = Measure(models, original, WavFile.Read(Path.Combine(folder, r.Row.Name + "-a.wav")));
                double[] distancesB = Measure(models, original, WavFile.Read(Path.Combine(folder, r.Row.Name + "-b.wav")));
                AppendRow(table, r.Row.Name, "a", distancesA);
                AppendRow(table, r.Row.Name, "b", distancesB);
                answers.Add(new EmbeddingAnswer(FitAnswer.From(r, a, b), distancesA, distancesB));
            }

            File.WriteAllText(Path.Combine(folder, "embeddings.tsv"), table.ToString());
            lines.InsertRange(0, Compare(answers, [.. models.Select(m => m.Model)], ListeningWeights.Weights));
        }
        finally
        {
            foreach (IListeningModel model in models)
            {
                model.Dispose();
            }
        }

        File.WriteAllText(Path.Combine(folder, "embeddings.txt"), string.Join('\n', lines) + "\n");
        foreach (string line in lines)
        {
            logger.Information("{Line:l}", line);
        }

        return 0;
    }

    /// <summary>
    /// The report: one line per set of features tried, with its leave-one-song-out log-loss and how often it guessed the
    /// a/b picks; log-mel, today's learned score (the features <paramref name="weights"/> keeps), each model alone and each
    /// model with today's features. A set over the answers' cap of weights is marked: it is for comparison only.
    /// </summary>
    public static IReadOnlyList<string> Compare(IReadOnlyList<EmbeddingAnswer> answers, IReadOnlyList<EmbeddingModel> models, IReadOnlyList<double> weights)
    {
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(weights);
        FitAnswer[] fitAnswers = [.. answers.Select(a => a.Answer)];
        int songs = fitAnswers.Select(a => a.Song).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int cap = fitAnswers.Length / ListeningFit.AnswersPerWeight;
        List<string> lines =
        [
            string.Create(CultureInfo.InvariantCulture, $"Answers: {fitAnswers.Length} from {songs} songs; at most {cap} weights in a fit (sets over it are for comparison only)"),
            string.Create(CultureInfo.InvariantCulture, $"Models measured: {(models.Count == 0 ? "none" : string.Join(", ", models.Select(m => m.Name)))}"),
        ];
        if (fitAnswers.Length == 0 || songs < 2)
        {
            lines.Add("Not compared: leaving one song out needs answers from two songs");
            return lines;
        }

        IReadOnlyList<string> names = [.. ListeningFeatures.Names, .. models.Select(m => m.FeatureName)];
        double[][] differences = [.. answers.Select(Difference)];
        int[] today = [.. Enumerable.Range(0, weights.Count).Where(j => weights[j] != 0)];
        int melDb = names.Select((name, j) => (name, j)).First(n => n.name == "mel_db").j;
        List<(string Label, int[] Features)> sets = [("log-mel", [melDb])];
        if (today.Length > 0)
        {
            sets.Add(("learned today", today));
        }

        for (int m = 0; m < models.Count; m++)
        {
            int column = ListeningFeatures.Names.Count + m;
            sets.Add((models[m].Name, [column]));
            if (today.Length > 0)
            {
                sets.Add((models[m].Name + " + learned today", [.. today, column]));
            }
        }

        lines.Add("Set: features; λ; leave-one-song-out log-loss (a coin: 0.693); a/b picks guessed on songs left out");
        foreach ((string label, int[] features) in sets)
        {
            string list = string.Join(" + ", features.Select(j => names[j]));
            string over = features.Length > cap ? " (over the cap)" : string.Empty;
            lines.Add(ListeningFit.Trial(fitAnswers, differences, features) is { } t
                ? string.Create(CultureInfo.InvariantCulture, $"  {label}: {list}; λ {t.Lambda:0.###}; {t.LogLoss:0.###}; {(t.Agreement is { } p ? p.ToString("0.0", CultureInfo.InvariantCulture) + " %" : "–")}{over}")
                : $"  {label}: {list}; not compared: a feature never differs between A and B");
        }

        return lines;
    }

    /// <summary>The <c>src/Scoring</c> differences <c>f(B) − f(A)</c>, then each model's.</summary>
    private static double[] Difference(EmbeddingAnswer answer)
    {
        IReadOnlyList<double> a = answer.Answer.A.Values;
        IReadOnlyList<double> b = answer.Answer.B.Values;
        return [.. a.Select((v, j) => b[j] - v), .. answer.A.Select((v, m) => answer.B[m] - v)];
    }

    private static double[] Measure(IReadOnlyList<IListeningModel> models, StereoAudio original, StereoAudio version) =>
        [.. models.Select(m => EmbeddingDistance.Measure(m, original, version))];

    private static void AppendRow(StringBuilder table, string row, string version, IEnumerable<double> distances)
    {
        table.Append(row).Append('\t').Append(version);
        foreach (double d in distances)
        {
            table.Append('\t').Append(d.ToString("0.######", CultureInfo.InvariantCulture));
        }

        table.Append('\n');
    }
}
