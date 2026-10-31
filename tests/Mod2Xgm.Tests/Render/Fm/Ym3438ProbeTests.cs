using System.Buffers.Binary;
using System.IO.Compression;
using Mod2Xgm.Scoring;

namespace Mod2Xgm.Tests.Render.Fm;

/// <summary>
/// Plays every YM3438 probe (<see cref="Ym3438Probes"/>) through the port and compares it with what ymfm and
/// Nuked-OPN2 play (plan T7.2, tests/golden/ym3438/README.md). The reference outputs come from
/// <c>tools/ym3438-reference/run.sh</c>; run it again after changing a probe (with <c>UPDATE_GOLDEN=1</c> first, to
/// rewrite the scripts). The port is ymfm except where it models the chip as Nuked-OPN2 does
/// (docs/adr/0064-ym3438-chip-differences.md): there it must come closer to Nuked, elsewhere stay exactly ymfm.
/// </summary>
public sealed class Ym3438ProbeTests
{
    /// <summary>
    /// The bar for "sounds like Nuked-OPN2": the log-mel distance of a probe at the chip's own rate
    /// (docs/adr/0056-ym3438-model.md; the worst of these probes measured 0.52 dB when the bar was set).
    /// </summary>
    private const double MaxMelDistanceToNukedDb = 0.6;

    /// <summary>
    /// Probes whose sound stays further from Nuked-OPN2 for a reason the model can't remove: their own bar, a little above
    /// what was measured, so the difference can't grow unseen (docs/adr/0064-ym3438-chip-differences.md#what-is-left).
    /// </summary>
    private static readonly Dictionary<string, (double BarDb, string Difference)> KnownDifferences = new()
    {
        ["detune-multiple"] = (3.0, "F-number writes land mid-sample in Nuked-OPN2, on a sample boundary in the model (measured 2.84 dB)"),
        ["six-channels"] = (2.2, "the reference tool keys the six channels up to 170 samples apart in Nuked-OPN2 (measured 1.91 dB)"),
    };

    /// <summary>
    /// The chip's differences from ymfm that the port models (docs/adr/0064-ym3438-chip-differences.md), each with the
    /// probes it changes: measured by turning each off in turn, when these and only these probes changed.
    /// </summary>
    private static readonly Dictionary<string, string[]> ChipDifferences = new()
    {
        ["clipping sum in slot order"] = ["ch3-special", "clip-order", "detune-multiple", "modulation-order", "six-channels"],
        ["operator 3 modulated by the previous sample"] = ["algorithms", "modulation-order", "write-quirks"],
        ["operator 1 keyed a sample late"] =
            ["algorithms", "ch3-special", "clip-order", "detune-multiple", "feedback", "lfo", "modulation-order", "six-channels", "write-quirks"],
        ["SSG-EG turn: inversion a sample early, phase held at 0"] = ["ssg-eg", "ssg-shapes"],
        ["SSG-EG inverted hold keeps its overshoot"] = ["ssg-shapes"],
        ["no envelope step in the key-on sample"] = ["envelope-rates"],
        ["envelope timer wraps from 4095 to 1"] =
        [
            "algorithms", "ch3-special", "clip-order", "detune-multiple", "envelope-rates", "envelopes", "feedback", "lfo", "sine",
            "six-channels", "ssg-eg", "ssg-shapes", "write-quirks",
        ],
        ["fast envelope rates step in Nuked-OPN2's order"] = ["envelope-rates", "ssg-shapes"],
    };

    /// <summary>
    /// The share of samples each one-channel probe must play as Nuked-OPN2 does, a little below what was measured. The
    /// probes that sound several channels are left out: Nuked's writes take time, so their channels start apart.
    /// </summary>
    private static readonly Dictionary<string, double> MinSamplesLikeNuked = new()
    {
        ["algorithms"] = 0.99,
        ["ch3-special"] = 0.99,
        ["clip-order"] = 0.99,
        ["detune-multiple"] = 0.90,
        ["envelope-rates"] = 0.999,
        ["feedback"] = 0.95,
        ["modulation-order"] = 0.99,
        ["sine"] = 0.999,
        ["ssg-eg"] = 0.99,
        ["ssg-shapes"] = 0.999,
        ["top-note"] = 0.99,
        ["write-quirks"] = 0.90,
    };

    /// <summary>Nuked-OPN2's pins summed over a sample's 24 clocks: each channel sounds on 18 of them, so ×128/18 matches ymfm's ×128/6.</summary>
    private const double NukedScale = 128.0 / 18;

    /// <summary>The most samples Nuked-OPN2's output trails the port's by (its pipeline; 2 to 4 measured).</summary>
    private const int MaxNukedLag = 8;

    /// <summary>The probe names, for the theories.</summary>
    public static TheoryData<string> Names => new(Ym3438Probes.All.Keys);

    /// <summary>The probes none of the <see cref="ChipDifferences"/> changes.</summary>
    public static TheoryData<string> UntouchedNames => new(Ym3438Probes.All.Keys.Where(name => !Touched(name)));

    /// <summary>The probes at least one of the <see cref="ChipDifferences"/> changes.</summary>
    public static TheoryData<string> TouchedNames => new(Ym3438Probes.All.Keys.Where(Touched));

    /// <summary>The one-channel probes, for the sample-for-sample comparison with Nuked-OPN2.</summary>
    public static TheoryData<string> OneChannelNames => new(MinSamplesLikeNuked.Keys);

    private static string ProbeDirectory => Path.Combine(RepoPaths.Root, "tests", "golden", "ym3438");

    /// <summary>The checked-in script is the one the builder makes (the reference tool plays the file).</summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void ScriptFileMatchesItsBuilder(string name) =>
        Golden.Verify(Path.Combine("ym3438", name + ".txt"), Ym3438Probes.All[name]().ToText());

    /// <summary>
    /// The port plays every probe that none of the chip's differences reaches sample for sample as ymfm does, so the port
    /// is still ymfm everywhere else.
    /// </summary>
    [Theory]
    [MemberData(nameof(UntouchedNames))]
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

    /// <summary>
    /// Every probe a chip difference reaches does play differently from ymfm (so the list in <see cref="ChipDifferences"/>
    /// is true) and plays at least as many samples as Nuked-OPN2 does as ymfm manages: each fix moves the port towards the chip.
    /// </summary>
    [Theory]
    [MemberData(nameof(TouchedNames))]
    public void PortIsCloserToNukedThanYmfm(string name)
    {
        int[] port = Ym3438Script.Render(Script(name));
        int[] ymfm = Reference(name, "ymfm");
        int[] nuked = Reference(name, "nuked");
        string fixes = string.Join(", ", ChipDifferences.Where(d => d.Value.Contains(name)).Select(d => d.Key));

        Assert.False(port.AsSpan().SequenceEqual(ymfm), $"{name}: plays as ymfm does, but is listed under {fixes}");
        double portShare = ShareLikeNuked(port, nuked);
        double ymfmShare = ShareLikeNuked(ymfm, nuked);
        Assert.True(portShare >= ymfmShare, $"{name}: {portShare:P1} of samples as Nuked-OPN2 plays them, ymfm {ymfmShare:P1} ({fixes})");
    }

    /// <summary>
    /// The port plays the one-channel probes sample for sample as Nuked-OPN2 does, give or take its pipeline delay and
    /// rounding, on at least the share in <see cref="MinSamplesLikeNuked"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(OneChannelNames))]
    public void PortPlaysLikeNukedSampleForSample(string name)
    {
        double share = ShareLikeNuked(Ym3438Script.Render(Script(name)), Reference(name, "nuked"));
        Assert.True(share >= MinSamplesLikeNuked[name], $"{name}: {share:P2} of samples as Nuked-OPN2 plays them (bar {MinSamplesLikeNuked[name]:P1})");
    }

    /// <summary>
    /// Every probe renders well within a time limit, so a change that loops forever fails here instead of hanging the
    /// run (plan T7.7: the converter may be slow, never stuck).
    /// </summary>
    [Theory(Timeout = 60_000)]
    [MemberData(nameof(Names))]
    public async Task EveryProbeRendersInBoundedTime(string name)
    {
        IReadOnlyList<Ym3438Step> script = Script(name);
        int[] port = await Task.Run(() => Ym3438Script.Render(script), TestContext.Current.CancellationToken);
        Assert.Equal(Reference(name, "ymfm").Length, port.Length);
    }

    /// <summary>Every probe the lists name exists, so a renamed probe doesn't silently drop out of a check.</summary>
    [Fact]
    public void ListsNameRealProbes()
    {
        Assert.All(KnownDifferences.Keys, name => Assert.True(Ym3438Probes.All.ContainsKey(name), name));
        Assert.All(ChipDifferences.Values.SelectMany(names => names), name => Assert.True(Ym3438Probes.All.ContainsKey(name), name));
        Assert.All(MinSamplesLikeNuked.Keys, name => Assert.True(Ym3438Probes.All.ContainsKey(name), name));
    }

    private static bool Touched(string name) => ChipDifferences.Values.Any(names => names.Contains(name));

    /// <summary>
    /// The share of <paramref name="output"/>'s samples that equal Nuked-OPN2's, scaled, within half a Nuked step, at
    /// the pipeline delay (0 to <see cref="MaxNukedLag"/> samples) where they agree best.
    /// </summary>
    private static double ShareLikeNuked(int[] output, int[] nuked)
    {
        int best = 0;
        int samples = output.Length / 2;
        for (int lag = 0; lag <= MaxNukedLag; lag++)
        {
            int same = 0;
            for (int i = 0; i + lag < samples; i++)
            {
                if (Math.Abs(output[2 * i] - (nuked[2 * (i + lag)] * NukedScale)) <= NukedScale / 2 &&
                    Math.Abs(output[(2 * i) + 1] - (nuked[(2 * (i + lag)) + 1] * NukedScale)) <= NukedScale / 2)
                {
                    same++;
                }
            }

            best = Math.Max(best, same);
        }

        return (double)best / samples;
    }

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
