using System.Buffers.Binary;
using System.IO.Compression;
using Mod2Xgm.Scoring;

namespace Mod2Xgm.Tests.Render.Fm;

/// <summary>
/// Plays every YM3438 probe (<see cref="Ym3438Probes"/>) through the port and compares it with what ymfm and
/// Nuked-OPN2 play (plan T7.2, tests/golden/ym3438/README.md). The reference outputs come from
/// <c>tools/ym3438-reference/run.sh</c>; run it again after changing a probe (with <c>UPDATE_GOLDEN=1</c> first, to
/// rewrite the scripts).
/// </summary>
public sealed class Ym3438ProbeTests
{
    /// <summary>
    /// The bar for "sounds like Nuked-OPN2": the log-mel distance of a probe at the chip's own rate
    /// (docs/adr/0056-ym3438-model.md; the worst of these probes measured 0.52 dB when the bar was set).
    /// </summary>
    private const double MaxMelDistanceToNukedDb = 0.6;

    /// <summary>
    /// Probes that reach a place where ymfm, and so the port, is known to differ from the chip as Nuked-OPN2 models it
    /// (docs/adr/0056-ym3438-model.md#known-differences-from-the-chip): their own bar, a little above what was measured, so
    /// the difference can't grow unseen. Plan T7.7 closes them; lower a bar when it does.
    /// </summary>
    private static readonly Dictionary<string, (double BarDb, string Difference)> KnownDifferences = new()
    {
        ["algorithms"] = (1.0, "operators keyed and modulated in one step (measured 0.88 dB)"),
        ["ch3-special"] = (1.2, "the order of the channel's clipping sum (measured 0.95 dB)"),
        ["detune-multiple"] = (3.0, "operators keyed in one step; detune overflow at the top (measured 2.76 dB)"),
        ["six-channels"] = (4.0, "the order of the channel's clipping sum (measured 3.52 dB)"),
        ["ssg-eg"] = (3.5, "no blip where the alternating shapes turn (measured 2.95 dB)"),
        ["write-quirks"] = (1.5, "operators keyed and modulated in one step (measured 1.12 dB)"),
    };

    /// <summary>Nuked-OPN2's pins summed over a sample's 24 clocks: each channel sounds on 18 of them, so ×128/18 matches ymfm's ×128/6.</summary>
    private const double NukedScale = 128.0 / 18;

    /// <summary>The probe names, for the theories.</summary>
    public static TheoryData<string> Names => new(Ym3438Probes.All.Keys);

    private static string ProbeDirectory => Path.Combine(RepoPaths.Root, "tests", "golden", "ym3438");

    /// <summary>The checked-in script is the one the builder makes (the reference tool plays the file).</summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void ScriptFileMatchesItsBuilder(string name) =>
        Golden.Verify(Path.Combine("ym3438", name + ".txt"), Ym3438Probes.All[name]().ToText());

    /// <summary>The port plays every probe sample for sample as ymfm does.</summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void PortMatchesYmfmExactly(string name)
    {
        int[] port = Ym3438Script.Render(Script(name));
        int[] ymfm = Reference(name, "ymfm");

        Assert.Equal(ymfm.Length, port.Length);
        int mismatch = port.AsSpan().CommonPrefixLength(ymfm);
        int sample = mismatch / 2;
        double seconds = sample / Ym3438Script.SampleRate;
        Assert.True(
            mismatch == port.Length,
            mismatch == port.Length ? null : $"{name}: first difference at sample {sample} ({(mismatch % 2 == 0 ? "left" : "right")}, " +
                $"{seconds:0.0000} s): port {port[mismatch]}, ymfm {ymfm[mismatch]}");
    }

    /// <summary>
    /// The port sounds like Nuked-OPN2, a transistor-level model of the YM3438, on every probe: a second opinion that
    /// doesn't share ymfm's code. The two never match sample for sample (Nuked's writes take time and its pipeline
    /// delays the output), so the spectra are compared.
    /// </summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void PortSoundsLikeNuked(string name)
    {
        int[] port = Ym3438Script.Render(Script(name));
        int[] nuked = Reference(name, "nuked");
        Assert.Equal(nuked.Length, port.Length);

        AudioSignal a = Mono(port, 1.0);
        AudioSignal b = Mono(nuked, NukedScale);
        double distance = SpectralMetrics.MelDistanceDb(b, a);

        (double bar, string why) = KnownDifferences.TryGetValue(name, out (double BarDb, string Difference) known)
            ? known
            : (MaxMelDistanceToNukedDb, "no known difference");
        Assert.True(distance <= bar, $"{name}: {distance:0.000} dB from Nuked-OPN2 (bar {bar} dB: {why})");
    }

    /// <summary>Every probe with its own bar exists, so a renamed probe doesn't silently fall back to the default.</summary>
    [Fact]
    public void KnownDifferencesNameRealProbes() =>
        Assert.All(KnownDifferences.Keys, name => Assert.True(Ym3438Probes.All.ContainsKey(name), name));

    private static IReadOnlyList<Ym3438Step> Script(string name) =>
        Ym3438Script.Parse(File.ReadAllText(Path.Combine(ProbeDirectory, name + ".txt")));

    /// <summary>A reference output: gzipped little-endian int32 pairs (left, right).</summary>
    private static int[] Reference(string name, string emulator)
    {
        string path = Path.Combine(ProbeDirectory, $"{name}.{emulator}.gz");
        Assert.True(File.Exists(path), $"{path} is missing. Run tools/ym3438-reference/run.sh to make it.");
        using FileStream file = File.OpenRead(path);
        using GZipStream gzip = new(file, CompressionMode.Decompress);
        using MemoryStream bytes = new();
        gzip.CopyTo(bytes);
        ReadOnlySpan<byte> data = bytes.GetBuffer().AsSpan(0, (int)bytes.Length);
        int[] values = new int[data.Length / 4];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadInt32LittleEndian(data[(i * 4)..]);
        }

        return values;
    }

    private static AudioSignal Mono(int[] interleaved, double scale)
    {
        float[] samples = new float[interleaved.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)((interleaved[2 * i] + interleaved[(2 * i) + 1]) * scale / 2 / Mod2Xgm.Render.Fm.Ym3438.FullScale);
        }

        return new AudioSignal(samples, (int)Math.Round(Ym3438Script.SampleRate));
    }
}
