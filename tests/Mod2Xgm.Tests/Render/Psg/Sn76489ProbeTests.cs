using System.Buffers.Binary;
using System.IO.Compression;
using Mod2Xgm.Render.Psg;
using Mod2Xgm.Scoring;

namespace Mod2Xgm.Tests.Render.Psg;

/// <summary>
/// Plays every SN76489 probe (<see cref="Sn76489Probes"/>) through the port and compares it with what MAME and
/// Nuked-PSG play (plan T7.3, tests/golden/sn76489/README.md). The reference outputs come from
/// <c>tools/sn76489-reference/run.sh</c>; run it again after changing a probe (with <c>UPDATE_GOLDEN=1</c> first, to
/// rewrite the scripts). The port is MAME except where it models the chip as Nuked-PSG does
/// (docs/adr/0065-sn76489-chip-differences.md): there it must come closer to Nuked, elsewhere stay exactly MAME.
/// </summary>
public sealed class Sn76489ProbeTests
{
    /// <summary>
    /// The bar for "sounds like Nuked-PSG": the log-mel distance of a probe after averaging every four samples
    /// (docs/adr/0057-sn76489-model.md; the worst of these probes, write-quirks, measured 0.16 dB after T7.8, docs/adr/0065-sn76489-chip-differences.md).
    /// </summary>
    private const double MaxMelDistanceToNukedDb = 0.3;

    /// <summary>
    /// Probes that stay further from Nuked-PSG for a reason the port keeps on purpose: their own bar, a little above what
    /// was measured, so the difference can't grow unseen (docs/adr/0065-sn76489-chip-differences.md#what-is-left).
    /// </summary>
    private static readonly Dictionary<string, (double BarDb, string Difference)> KnownDifferences = new()
    {
        ["chord"] = (0.9, "the volume levels; the reference tool's 9 setup writes start tone 1 a sample early in Nuked-PSG (measured 0.72 dB)"),
        ["noise-restart"] = (2.3, "the volume levels (measured 1.90 dB)"),
        ["volumes"] = (2.5, "the volume levels (measured 2.07 dB)"),
        ["power-on-latch"] = (11.0, "the register the latch points at after power-on (measured 9.98 dB)"),
    };

    /// <summary>
    /// The chip's differences from MAME that the port models (docs/adr/0065-sn76489-chip-differences.md), each with the
    /// probes it changes: measured by turning each off in turn, when these and only these probes changed.
    /// </summary>
    private static readonly Dictionary<string, string[]> ChipDifferences = new()
    {
        ["tone counters count up to the period as it is now"] = ["periods", "write-quirks"],
        ["noise emptied at once on a restart, shifted on its clock's rise"] = ["chord", "periodic-noise", "white-noise", "write-quirks"],
    };

    /// <summary>Nuked-PSG's output is the sum of its four channel levels × 8192; the port's is −(sum of MAME's levels, 8191 at most).</summary>
    private const double NukedFullScale = 4 * 8192;

    /// <summary>Samples averaged into one before the spectra are compared: about 55.9 kHz, so most mel bands are audible.</summary>
    private const int Decimation = 4;

    /// <summary>The probe names, for the theories.</summary>
    public static TheoryData<string> Names => new(Sn76489Probes.All.Keys);

    /// <summary>The probes none of the <see cref="ChipDifferences"/> changes.</summary>
    public static TheoryData<string> UntouchedNames => new(Sn76489Probes.All.Keys.Where(name => !Touched(name)));

    /// <summary>The probes at least one of the <see cref="ChipDifferences"/> changes.</summary>
    public static TheoryData<string> TouchedNames => new(Sn76489Probes.All.Keys.Where(Touched));

    private static string ProbeDirectory => Path.Combine(RepoPaths.Root, "tests", "golden", "sn76489");

    /// <summary>The checked-in script is the one the builder makes (the reference tool plays the file).</summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void ScriptFileMatchesItsBuilder(string name) =>
        Golden.Verify(Path.Combine("sn76489", name + ".txt"), Sn76489Probes.All[name]().ToText());

    /// <summary>
    /// The port plays every probe that none of the chip's differences reaches sample for sample as MAME's Sega PSG does,
    /// so the port is still MAME everywhere else.
    /// </summary>
    [Theory]
    [MemberData(nameof(UntouchedNames))]
    public void PortMatchesMameExactly(string name)
    {
        int[] port = Sn76489Script.Render(Script(name));
        int[] mame = Reference(name, "mame");

        Assert.Equal(mame.Length, port.Length);
        int mismatch = port.AsSpan().CommonPrefixLength(mame);
        Assert.True(
            mismatch == port.Length,
            mismatch == port.Length ? null : $"{name}: first difference at sample {mismatch} ({mismatch / Sn76489Script.SampleRate:0.000000} s): " +
                $"port {port[mismatch]}, MAME {mame[mismatch]}");
    }

    /// <summary>
    /// The port sounds like Nuked-PSG, a transistor-level model of the Genesis's PSG (the YM7101 VDP's), on every
    /// probe: a second opinion that doesn't share MAME's code. The two rarely match sample for sample (its output has the
    /// opposite sign and its levels are its own, not MAME's 2 dB steps, which the port keeps; docs/adr/0065-sn76489-chip-differences.md),
    /// so the spectra are compared.
    /// </summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void PortSoundsLikeNuked(string name)
    {
        int[] port = Sn76489Script.Render(Script(name));
        int[] nuked = Reference(name, "nuked");
        Assert.Equal(nuked.Length, port.Length);

        double distance = SpectralMetrics.MelDistanceDb(Signal(nuked, NukedFullScale), Signal(port, Sn76489.FullScale));

        (double bar, string why) = KnownDifferences.TryGetValue(name, out (double BarDb, string Difference) known)
            ? known
            : (MaxMelDistanceToNukedDb, "no known difference");
        Assert.True(distance <= bar, $"{name}: {distance:0.000} dB from Nuked-PSG (bar {bar} dB: {why})");
    }

    /// <summary>
    /// Every probe a chip difference reaches does play differently from MAME (so the list in <see cref="ChipDifferences"/>
    /// is true) and comes closer to Nuked-PSG than MAME does: each fix moves the port towards the chip.
    /// </summary>
    [Theory]
    [MemberData(nameof(TouchedNames))]
    public void PortIsCloserToNukedThanMame(string name)
    {
        int[] port = Sn76489Script.Render(Script(name));
        int[] mame = Reference(name, "mame");
        AudioSignal nuked = Signal(Reference(name, "nuked"), NukedFullScale);
        string fixes = string.Join(", ", ChipDifferences.Where(d => d.Value.Contains(name)).Select(d => d.Key));

        Assert.False(port.AsSpan().SequenceEqual(mame), $"{name}: plays as MAME does, but is listed under {fixes}");
        double portDistance = SpectralMetrics.MelDistanceDb(nuked, Signal(port, Sn76489.FullScale));
        double mameDistance = SpectralMetrics.MelDistanceDb(nuked, Signal(mame, Sn76489.FullScale));
        Assert.True(portDistance < mameDistance, $"{name}: {portDistance:0.000} dB from Nuked-PSG, MAME {mameDistance:0.000} dB ({fixes})");
    }

    /// <summary>Every probe the lists name exists, so a renamed probe doesn't silently drop out of a check.</summary>
    [Fact]
    public void ListsNameRealProbes()
    {
        Assert.All(KnownDifferences.Keys, name => Assert.True(Sn76489Probes.All.ContainsKey(name), name));
        Assert.All(ChipDifferences.Values.SelectMany(names => names), name => Assert.True(Sn76489Probes.All.ContainsKey(name), name));
    }

    private static bool Touched(string name) => ChipDifferences.Values.Any(names => names.Contains(name));

    private static IReadOnlyList<Sn76489Step> Script(string name) =>
        Sn76489Script.Parse(File.ReadAllText(Path.Combine(ProbeDirectory, name + ".txt")));

    /// <summary>A reference output: gzipped little-endian int32s, one per sample.</summary>
    private static int[] Reference(string name, string emulator)
    {
        string path = Path.Combine(ProbeDirectory, $"{name}.{emulator}.gz");
        Assert.True(File.Exists(path), $"{path} is missing. Run tools/sn76489-reference/run.sh to make it.");
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

    /// <summary>The output scaled to ±1 with every <see cref="Decimation"/> samples averaged into one.</summary>
    private static AudioSignal Signal(int[] output, double fullScale)
    {
        float[] samples = new float[output.Length / Decimation];
        for (int i = 0; i < samples.Length; i++)
        {
            long sum = 0;
            for (int j = 0; j < Decimation; j++)
            {
                sum += output[(i * Decimation) + j];
            }

            samples[i] = (float)(sum / (Decimation * fullScale));
        }

        return new AudioSignal(samples, (int)Math.Round(Sn76489Script.SampleRate / Decimation));
    }
}
