using System.Globalization;
using Mod2Xgm.Render;
using Mod2Xgm.Scoring;
using Moq;
using Serilog.Core;

namespace Mod2Xgm.Harness.Tests;

/// <summary>Tests for the pretrained-model distances and their comparison (plan T9.4k4, <see cref="EmbeddingDistance"/>, <see cref="EmbeddingsRun"/>).</summary>
public sealed class EmbeddingsTests
{
    /// <summary>A made-up model at 1 kHz with 100-sample chunks, so no resampling happens.</summary>
    private static readonly EmbeddingModel Fake = new("fake", 1_000, 100, false);

    /// <summary>Whole chunks are kept and the rest dropped.</summary>
    [Fact]
    public void Chunks_WithRemainder_DropsIt()
    {
        // Arrange
        float[] audio = [.. Enumerable.Range(0, 250).Select(i => (float)i)];

        // Act
        IReadOnlyList<float[]> sut = EmbeddingDistance.Chunks(audio, 100);

        // Assert
        Assert.Equal(2, sut.Count);
        Assert.Equal(100f, sut[1][0]);
        Assert.Equal(199f, sut[1][99]);
    }

    /// <summary>Audio shorter than one chunk becomes one chunk padded with silence.</summary>
    [Fact]
    public void Chunks_ShorterThanOne_PadsWithSilence()
    {
        // Act
        IReadOnlyList<float[]> sut = EmbeddingDistance.Chunks([1f, 2f], 4);

        // Assert
        float[] only = Assert.Single(sut);
        Assert.Equal([1f, 2f, 0f, 0f], only);
    }

    /// <summary>The cosine distance is 0 for the same direction, 1 at right angles, 2 opposite, and handles silence.</summary>
    [Theory]
    [InlineData(new[] { 1f, 2f }, new[] { 2f, 4f }, 0.0)]
    [InlineData(new[] { 1f, 0f }, new[] { 0f, 3f }, 1.0)]
    [InlineData(new[] { 1f, 1f }, new[] { -1f, -1f }, 2.0)]
    [InlineData(new[] { 0f, 0f }, new[] { 0f, 0f }, 0.0)]
    [InlineData(new[] { 0f, 0f }, new[] { 1f, 0f }, 1.0)]
    public void CosineDistance_ForVectors_IsOneMinusCosine(float[] a, float[] b, double expected)
    {
        // Act
        double sut = EmbeddingDistance.CosineDistance(a, b);

        // Assert
        Assert.Equal(expected, sut, 1e-9);
    }

    /// <summary>Embeddings of different lengths can't be compared.</summary>
    [Fact]
    public void CosineDistance_WithDifferentLengths_Throws()
    {
        // Act
        ArgumentException ex = Assert.Throws<ArgumentException>(() => EmbeddingDistance.CosineDistance([1f], [1f, 2f]));

        // Assert
        Assert.Equal("b", ex.ParamName);
    }

    /// <summary>Mono is the mean of both channels.</summary>
    [Fact]
    public void Mono_OfStereo_AveragesChannels()
    {
        // Arrange
        StereoAudio audio = new([1f, 0.5f], [0f, -0.5f], 1_000);

        // Act
        float[] sut = EmbeddingDistance.Mono(audio);

        // Assert
        Assert.Equal([0.5f, 0f], sut);
    }

    /// <summary>
    /// Chunk by chunk (David): a version that differs in one of two chunks scores half the distance of that chunk, rather
    /// than a difference averaged away first.
    /// </summary>
    [Fact]
    public void Measure_WithOneChunkDifferent_AveragesTheChunkDistances()
    {
        // Arrange
        using IListeningModel model = SignEmbedder(Fake);
        StereoAudio original = Constant(200, 0.5f);
        StereoAudio version = new([.. Enumerable.Range(0, 200).Select(i => i < 100 ? 0.5f : -0.5f)], [.. Enumerable.Range(0, 200).Select(i => i < 100 ? 0.5f : -0.5f)], 1_000);

        // Act
        double sut = EmbeddingDistance.Measure(model, original, version);

        // Assert: chunk 1 the same (0), chunk 2 opposite (2), mean 1.
        Assert.Equal(1.0, sut, 1e-9);
    }

    /// <summary>A model that gives distances is asked once per chunk pair, and its answers are averaged.</summary>
    [Fact]
    public void Measure_WithDistanceModel_AveragesItsDistances()
    {
        // Arrange
        EmbeddingModel info = Fake with { GivesDistance = true };
        Mock<IListeningModel> model = new();
        model.Setup(m => m.Model).Returns(info);
        model.SetupSequence(m => m.Distance(It.IsAny<float[]>(), It.IsAny<float[]>())).Returns(0.2).Returns(0.4).Returns(0.9);
        StereoAudio clip = Constant(300, 0.1f);

        // Act
        double sut = EmbeddingDistance.Measure(model.Object, clip, clip);

        // Assert
        Assert.Equal(0.5, sut, 1e-9);
        model.Verify(m => m.Distance(It.IsAny<float[]>(), It.IsAny<float[]>()), Times.Exactly(3));
    }

    /// <summary>The model table matches the export script: four models, each chunk a whole number of samples at its rate.</summary>
    [Fact]
    public void All_ListsTheFourModelsOfTheExport()
    {
        // Act
        IReadOnlyList<EmbeddingModel> sut = EmbeddingModel.All;

        // Assert
        Assert.Equal(["panns", "vggish", "clap", "cdpam"], sut.Select(m => m.Name));
        Assert.Single(sut, m => m.GivesDistance);
        Assert.All(sut, m => Assert.True(m.ChunkSamples > 0 && m.RateHz > 0));
        Assert.Equal("panns_distance", sut[0].FeatureName);
    }

    /// <summary>The test signal is the export's: 0.1 sin(2π 440 t), one chunk long.</summary>
    [Fact]
    public void CheckSignal_IsTheExportsSine()
    {
        // Act
        float[] sut = Fake.CheckSignal();

        // Assert
        Assert.Equal(Fake.ChunkSamples, sut.Length);
        Assert.Equal(0f, sut[0]);
        Assert.Equal((float)(0.1 * Math.Sin(2 * Math.PI * 440 / 1_000.0)), sut[1]);
    }

    /// <summary>When a model's distance decides every pick and log-mel only half, its line guesses every pick and log-mel's doesn't.</summary>
    [Fact]
    public void Compare_WhenModelDecidesThePicks_ReportsItAhead()
    {
        // Arrange
        EmbeddingAnswer[] answers = Answers(20, songs: 4);

        // Act
        IReadOnlyList<string> sut = EmbeddingsRun.Compare(answers, [Fake], [0, 0, 0, 0, 0, 1, 0]);

        // Assert
        Assert.StartsWith("Answers: 20 from 4 songs; at most 2 weights", sut[0], StringComparison.Ordinal);
        Assert.Equal("Models measured: fake", sut[1]);
        Assert.Contains(sut, l => l.StartsWith("  fake: fake_distance;", StringComparison.Ordinal) && l.EndsWith("; 100.0 %", StringComparison.Ordinal));
        Assert.Contains(sut, l => l.StartsWith("  log-mel: mel_db;", StringComparison.Ordinal) && !l.EndsWith("100.0 %", StringComparison.Ordinal));
        Assert.Contains(sut, l => l.StartsWith("  fake + learned today: modulation_db + fake_distance;", StringComparison.Ordinal));
    }

    /// <summary>A set with more features than the answers allow weights is marked as for comparison only.</summary>
    [Fact]
    public void Compare_OverTheCap_MarksTheSet()
    {
        // Arrange
        EmbeddingAnswer[] answers = Answers(12, songs: 4);

        // Act
        IReadOnlyList<string> sut = EmbeddingsRun.Compare(answers, [Fake], [0, 0, 0, 0, 0, 1, 0]);

        // Assert
        Assert.Contains(sut, l => l.StartsWith("  fake + learned today:", StringComparison.Ordinal) && l.EndsWith("(over the cap)", StringComparison.Ordinal));
    }

    /// <summary>Answers from one song can't be checked by leaving a song out.</summary>
    [Fact]
    public void Compare_WithOneSong_SaysNotCompared()
    {
        // Act
        IReadOnlyList<string> sut = EmbeddingsRun.Compare(Answers(10, songs: 1), [Fake], []);

        // Assert
        Assert.Contains(sut, l => l.StartsWith("Not compared", StringComparison.Ordinal));
    }

    /// <summary>
    /// The run measures each heard, answered row's clips with the models found, writes <c>embeddings.tsv</c> and
    /// <c>embeddings.txt</c>, and lists the models missing.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void Run_WithOneModelFound_MeasuresAndListsTheMissing()
    {
        // Arrange
        using TempDirectory temp = new();
        string listening = temp.Combine("listening");
        Directory.CreateDirectory(listening);
        string models = temp.Combine("models");
        Directory.CreateDirectory(models);
        File.WriteAllBytes(Path.Combine(models, "panns.onnx"), []);
        RemadeRow[] rows = [.. Enumerable.Range(0, 4).Select(i => Row(i, listening))];
        RemadeRow unheard = Row(4, listening) with { Heard = false };
        using IListeningModel model = SignEmbedder(EmbeddingModel.All[0] with { RateHz = 1_000, ChunkSamples = 100 });

        // Act
        int code = EmbeddingsRun.Run(new RemakeResult(0, [.. rows, unheard]), temp.Path, models, _ => model, Logger.None);

        // Assert
        Assert.Equal(0, code);
        string[] tsv = File.ReadAllLines(Path.Combine(listening, "embeddings.tsv"));
        Assert.Equal("row\tversion\tpanns_distance", tsv[0]);
        Assert.Equal(9, tsv.Length);
        Assert.Equal("r1-q0\ta\t0", tsv[1]);
        Assert.Equal("r1-q0\tb\t2", tsv[2]);
        string report = File.ReadAllText(Path.Combine(listening, "embeddings.txt"));
        Assert.Contains("Models measured: panns", report, StringComparison.Ordinal);
        Assert.Contains("Model vggish: not found in", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// With the exported models on this PC, ONNX Runtime in C# gives each the output the export recorded for the test
    /// signal. Skipped where the models folder or a model is missing, as in CI (David, 2026-11-11).
    /// </summary>
    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("panns")]
    [InlineData("vggish")]
    [InlineData("clap")]
    [InlineData("cdpam")]
    public void OnnxModel_WithExportedModel_MatchesTheExport(string name)
    {
        // Arrange
        EmbeddingModel info = EmbeddingModel.All.Single(m => m.Name == name);
        string folder = EmbeddingModel.DefaultFolder;
        if (!File.Exists(Path.Combine(folder, info.FileName)) || !File.Exists(Path.Combine(folder, info.ExpectedFileName)))
        {
            Assert.Skip($"{info.FileName} not exported to {folder} (tools/onnx-export/README.md)");
        }

        double[] expected = [.. File.ReadAllLines(Path.Combine(folder, info.ExpectedFileName)).Select(l => double.Parse(l, CultureInfo.InvariantCulture))];
        using OnnxListeningModel sut = new(info, folder);

        // Act
        double[] got = info.GivesDistance
            ? [sut.Distance(info.CheckSignal(), info.CheckSignal(second: true))]
            : [.. sut.Embed(info.CheckSignal()).Select(v => (double)v)];

        // Assert
        Assert.Equal(expected.Length, got.Length);
        double largest = Math.Max(1e-6, expected.Max(Math.Abs));
        Assert.All(got.Zip(expected), p => Assert.True(Math.Abs(p.First - p.Second) <= 1e-3 * largest, $"{p.First} vs {p.Second}"));
    }

    /// <summary>A model whose embedding is the sign of a chunk's mean and a constant: opposite chunks are 2 apart.</summary>
    private static IListeningModel SignEmbedder(EmbeddingModel info)
    {
        Mock<IListeningModel> model = new();
        model.Setup(m => m.Model).Returns(info);
        model.Setup(m => m.Embed(It.IsAny<float[]>())).Returns((float[] c) => [Math.Sign(c.Average()), 0f]);
        return model.Object;
    }

    private static StereoAudio Constant(int length, float value)
    {
        float[] samples = [.. Enumerable.Repeat(value, length)];
        return new StereoAudio(samples, samples, 1_000);
    }

    /// <summary>
    /// <paramref name="count"/> answers over <paramref name="songs"/> songs: the picked version is 0.1–0.3 nearer by the
    /// model, log-mel points the right way only every other pair, and modulation never helps.
    /// </summary>
    private static EmbeddingAnswer[] Answers(int count, int songs) =>
        [.. Enumerable.Range(0, count).Select(i =>
        {
            double mel = i % 4 < 2 ? 0.5 : -0.5;
            double gap = 0.1 * (1 + (i % 3));
            ListeningFeatures a = new(5 + mel, 30, 0.1, -10, 0.2, 3 + (0.1 * i), 6);
            ListeningFeatures b = new(5 - mel, 30, 0.1, -10, 0.2, 3 - (0.1 * i), 6);
            string pick = i % 2 == 0 ? "a" : "b";
            (double da, double db) = pick == "a" ? (0.1, 0.1 + gap) : (0.1 + gap, 0.1);
            return new EmbeddingAnswer(new FitAnswer($"r1-q{i}", $"song{i % songs}.mod", pick, a, b), [da], [db]);
        })];

    /// <summary>A heard, answered row whose clips are written: the original and A at +0.5, B at −0.5.</summary>
    private static RemadeRow Row(int i, string listening)
    {
        string name = $"r1-q{i}";
        WavFile.Write(Path.Combine(listening, name + "-o.wav"), Constant(200, 0.5f));
        WavFile.Write(Path.Combine(listening, name + "-a.wav"), Constant(200, 0.5f));
        WavFile.Write(Path.Combine(listening, name + "-b.wav"), Constant(200, -0.5f));
        ListeningFeatures features = new(5 + i, 30, 0.1, -10, 0.2, 3, 6);
        ListeningRow row = new("2026-11-11", "r1", $"q{i}", $"song{i}.mod", 0, 1, "xgm2", "", "", "c", "x", "y", "a", "", "", "");
        return new RemadeRow(row, true, features, features with { MelDb = 6 + i });
    }
}
