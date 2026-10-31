using System.Buffers.Binary;
using System.IO.Compression;

namespace Mod2Xgm.Tests.Render.Pcm;

/// <summary>
/// Plays every PCM driver probe (<see cref="PcmDriverProbes"/>) through the models and compares the DAC writes with what
/// SGDK's real XGM and XGM2 Z80 drivers make of it on an emulated Z80 (plan T7.9, tests/golden/pcm-drivers/README.md,
/// docs/adr/0066-pcm-drivers-checked-against-sgdk.md). The captures come from <c>tools/pcm-driver-reference/run.sh</c>;
/// run it again after changing a probe (with <c>UPDATE_GOLDEN=1</c> first, to rewrite the scripts).
/// </summary>
public sealed class PcmDriverProbeTests
{
    /// <summary>The probe names, for the theories.</summary>
    public static TheoryData<string> Names => new(PcmDriverProbes.All.Keys);

    private static string ProbeDirectory => Path.Combine(RepoPaths.Root, "tests", "golden", "pcm-drivers");

    /// <summary>The checked-in script is the one the builder makes (the reference tool plays the file).</summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void ScriptFileMatchesItsBuilder(string name) =>
        Golden.Verify(Path.Combine("pcm-drivers", name + ".txt"), PcmDriverProbes.All[name]().ToText());

    /// <summary>The checked-in script reads back as the probe it was built from.</summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void ScriptReadsBack(string name)
    {
        PcmDriverScript built = PcmDriverProbes.All[name]();
        PcmDriverScript parsed = PcmDriverScript.Parse(built.ToText());
        Assert.Equal(built.Xgm2, parsed.Xgm2);
        Assert.Equal(built.Samples, parsed.Samples);
        Assert.Equal(built.Frames.Count, parsed.Frames.Count);
        Assert.All(built.Frames.Zip(parsed.Frames), pair => Assert.Equal(pair.First, pair.Second));
    }

    /// <summary>Master clocks per XGM beat (3810) and per 256-beat XGM mixing block.</summary>
    private const long XgmBeatMaster = 15 * 254;

    private const long XgmBlockMaster = 256 * XgmBeatMaster;

    /// <summary>
    /// How far a driver's byte may land from where the rule puts it: the drivers send some bytes up to 35 Z80 cycles
    /// early or late (the beat wobble, docs/adr/0066-pcm-drivers-checked-against-sgdk.md): up to 0.98 of a YM3438 sample.
    /// </summary>
    private const long WobbleMaster = PcmDriverScript.MasterClocksPerSample;

    /// <summary>XGM2: how far the model's start may be from the driver's: its buffer's fill level moves the start ±23 beats.</summary>
    private const int Xgm2StartToleranceSamples = 30 * 4;

    /// <summary>
    /// XGM: SGDK's driver starts every sound on the first of its 256-beat blocks that begins at least
    /// <see cref="Mod2Xgm.Render.Pcm.XgmPcmDriver.StartLeadBeats"/> beats after the sound's song frame. Its blocks' phase is
    /// its own (set by when it was loaded), so the rule is checked against the phase of its first start.
    /// </summary>
    [Fact]
    public void XgmDriverStartsOnTheFirstBlockAfterTheLead()
    {
        PcmDriverScript script = PcmDriverProbes.All["xgm-starts"]();
        List<DacWrite> capture = Capture("xgm-starts");
        long[] starts = DriverStarts(capture, script, PcmDriverProbes.XgmStartSpacing);
        const long Tolerance = 2 * PcmDriverScript.MasterClocksPerSample; // the wobble and the estimate of the driver's beat

        // The driver's own beat spacing (0.04 % longer than the model's: its cycle count with the bus waits), so its
        // blocks don't drift against the rule over the probe's five seconds.
        List<DacWrite> beats = [.. capture.Where(w => w.Register == 0x2A && w.Master >= 0)];
        double beat = (beats[^1].Master - beats[0].Master) / (double)(beats.Count - 1);
        double block = 256 * beat;
        for (int i = 0; i < starts.Length; i++)
        {
            double earliest = (FrameOf(i, PcmDriverProbes.XgmStartSpacing) * PcmDriverScript.MasterClocksPerFrame) + (Mod2Xgm.Render.Pcm.XgmPcmDriver.StartLeadBeats * beat);
            double predicted = starts[0] + (Math.Ceiling((earliest - starts[0]) / block) * block);
            Assert.True(Math.Abs(starts[i] - predicted) <= Tolerance, $"sound {i + 1}: starts at {starts[i]}, the rule says {predicted:0} (master clocks)");
        }
    }

    /// <summary>XGM: the model starts every sound on the first of its own blocks at least the lead after the song frame.</summary>
    [Fact]
    public void XgmModelStartsOnTheFirstBlockAfterTheLead()
    {
        PcmDriverScript script = PcmDriverProbes.All["xgm-starts"]();
        long[] starts = ModelStarts(script.Render().Data, script, PcmDriverProbes.XgmStartSpacing);
        for (int i = 0; i < starts.Length; i++)
        {
            long earliest = (FrameOf(i, PcmDriverProbes.XgmStartSpacing) * PcmDriverScript.MasterClocksPerFrame) + (Mod2Xgm.Render.Pcm.XgmPcmDriver.StartLeadBeats * XgmBeatMaster);
            long predicted = (earliest + XgmBlockMaster - 1) / XgmBlockMaster * XgmBlockMaster;
            Assert.True(Math.Abs(starts[i] - predicted) <= PcmDriverScript.MasterClocksPerSample, $"sound {i + 1}: starts at {starts[i]}, the rule says {predicted} (master clocks)");
        }
    }

    /// <summary>XGM2: the model starts every sound within the driver's buffer jitter of where SGDK's driver starts it.</summary>
    [Fact]
    public void Xgm2StartsAsTheDriverDoes()
    {
        PcmDriverScript script = PcmDriverProbes.All["xgm2-starts"]();
        long[] driver = DriverStarts(Capture("xgm2-starts"), script, PcmDriverProbes.Xgm2StartSpacing);
        long[] model = ModelStarts(script.Render().Data, script, PcmDriverProbes.Xgm2StartSpacing);
        for (int i = 0; i < driver.Length; i++)
        {
            double difference = (model[i] - driver[i]) / (double)PcmDriverScript.MasterClocksPerSample;
            Assert.True(Math.Abs(difference) <= Xgm2StartToleranceSamples, $"sound {i + 1}: the model starts {difference:0.0} YM3438 samples from the driver");
        }
    }

    /// <summary>
    /// The models mix what the drivers mix: from the first sound on, the run of DAC values (each value once, as long as it
    /// lasts) is the driver's, until the probe's later commands (XGM: their timing moves with the driver's block phase;
    /// XGM2: with its buffer's fill level, so only the first sound is compared, alone).
    /// </summary>
    [Theory]
    [InlineData("xgm-mix", 7)]
    [InlineData("xgm2-mix", 5)]
    [InlineData("xgm2-starts", 6)]
    [InlineData("xgm2-half", 10)]
    public void MixingMatchesTheDriver(string name, int untilFrame)
    {
        PcmDriverScript script = PcmDriverProbes.All[name]();
        byte[] data = script.Render().Data;
        int end = PcmDriverScript.SampleAt(untilFrame);
        List<byte> model = Runs(data.Take(end));
        List<byte> driver = Runs(Capture(name).Where(w => w.Register == 0x2A && w.Master >= 0 && w.Master < end * PcmDriverScript.MasterClocksPerSample).Select(w => w.Value));
        model = [.. model.SkipWhile(v => v == 0x80)];
        driver = [.. driver.SkipWhile(v => v == 0x80)];
        int length = Math.Min(model.Count, driver.Count) - 1;
        Assert.True(length > 100, $"{name}: only {length} values to compare");
        int first = Enumerable.Range(0, length).FirstOrDefault(i => model[i] != driver[i], -1);
        Assert.True(first < 0, first < 0 ? null : $"{name}: value {first} is {model[first]:X2} in the model, {driver[first]:X2} in the driver");
    }

    /// <summary>
    /// The models' beat rates are the drivers' average, within 0.2 % (3.5 cents), over each probe whose mix doesn't clip
    /// (clipping slows the drivers: <see cref="XgmClippingSlowsTheBeatAsTheDriverDoes"/>).
    /// </summary>
    [Theory]
    [InlineData("xgm-starts")]
    [InlineData("xgm2-starts")]
    [InlineData("xgm2-stop")]
    public void BeatRateIsTheDriversAverage(string name)
    {
        PcmDriverScript script = PcmDriverProbes.All[name]();
        List<DacWrite> beats = [.. Capture(name).Where(w => w.Register == 0x2A && w.Master >= 0)];
        double driverHz = 53_693_175.0 * (beats.Count - 1) / (beats[^1].Master - beats[0].Master);
        Assert.True(Math.Abs((driverHz / script.BeatRateHz) - 1) <= 0.002, $"{name}: driver {driverHz:0.0} Hz, model {script.BeatRateHz:0.0} Hz");
    }

    /// <summary>
    /// XGM: a mix that clips slows the beat, 11 Z80 cycles a clipped add; over the four loud channels of <c>xgm-mix</c>
    /// the model sends as many beats as SGDK's driver, within 0.3 %. Without the clip cost it sent 3 % more.
    /// </summary>
    [Fact]
    public void XgmClippingSlowsTheBeatAsTheDriverDoes()
    {
        const int Frames = 12;
        PcmDriverScript script = PcmDriverProbes.All["xgm-mix"]();
        script.Render(Frames, out long model);
        long end = Frames * PcmDriverScript.MasterClocksPerFrame;
        long driver = Capture("xgm-mix").Count(w => w.Register == 0x2A && w.Master >= 0 && w.Master < end);
        Assert.True(Math.Abs(((double)model / driver) - 1) <= 0.003, $"model {model} beats, driver {driver}");
    }

    /// <summary>
    /// The drivers' beats wobble around their spacing (the models' are even): no beat is further from the one before than
    /// the usual spacing by more than 1 YM3438 sample on XGM ("too soon" / "too late" in its loop), 3 on XGM2 (around the
    /// VBlank interrupt). Measured on the probes whose mix doesn't clip.
    /// </summary>
    [Theory]
    [InlineData("xgm-starts", 1.0)]
    [InlineData("xgm2-starts", 3.0)]
    [InlineData("xgm2-stop", 3.0)]
    [InlineData("xgm2-half", 3.0)]
    public void TheBeatWobbleIsSmall(string name, double maxSamples)
    {
        List<DacWrite> beats = [.. Capture(name).Where(w => w.Register == 0x2A && w.Master >= 0)];
        long[] intervals = [.. beats.Zip(beats.Skip(1), (a, b) => b.Master - a.Master).Order()];
        long usual = intervals[intervals.Length / 2];
        double worst = intervals.Max(i => Math.Abs(i - usual)) / (double)PcmDriverScript.MasterClocksPerSample;
        Assert.True(worst <= maxSamples, $"{name}: a beat {worst:0.00} YM3438 samples off the usual spacing");
    }

    /// <summary>XGM2: the model stops a sound within the start tolerance of where SGDK's driver stops it.</summary>
    [Fact]
    public void Xgm2StopsAsTheDriverDoes()
    {
        PcmDriverScript script = PcmDriverProbes.All["xgm2-stop"]();
        byte[] data = script.Render().Data;
        long modelEnd = Array.FindLastIndex(data, v => v != 0x80) * PcmDriverScript.MasterClocksPerSample;
        long driverEnd = Capture("xgm2-stop").Last(w => w.Register == 0x2A && w.Value != 0x80).Master;
        double difference = (modelEnd - driverEnd) / (double)PcmDriverScript.MasterClocksPerSample;
        Assert.True(Math.Abs(difference) <= Xgm2StartToleranceSamples, $"the model stops {difference:0.0} YM3438 samples from the driver");
    }

    /// <summary>
    /// XGM2: the model turns the DAC on before each sound and off after the last one as SGDK's driver does, within one
    /// 64-beat block (256 YM3438 samples).
    /// </summary>
    [Theory]
    [InlineData("xgm2-starts")]
    [InlineData("xgm2-mix")]
    [InlineData("xgm2-stop")]
    public void Xgm2DacEnableFollowsTheDriver(string name)
    {
        PcmDriverScript script = PcmDriverProbes.All[name]();
        byte[] enable = script.Render().Enable;
        List<long> model = Changes(enable.Select((v, i) => (Master: i * PcmDriverScript.MasterClocksPerSample, On: v >= 0x80)));
        List<long> driver = Changes(Capture(name).Where(w => w.Register == 0x2B && w.Master >= 0).Select(w => (w.Master, On: w.Value >= 0x80)));
        Assert.Equal(driver.Count, model.Count);
        for (int i = 0; i < model.Count; i++)
        {
            double difference = (model[i] - driver[i]) / (double)PcmDriverScript.MasterClocksPerSample;
            Assert.True(Math.Abs(difference) <= 256, $"{name}: DAC change {i} is {difference:0.0} YM3438 samples from the driver's");
        }
    }

    /// <summary>The frame the start probe's sound <paramref name="index"/> (0-based) is played in.</summary>
    private static int FrameOf(int index, int spacing) => PcmDriverProbes.FirstPlayFrame + (index * spacing);

    /// <summary>When a start probe's sounds start in a capture: the write of each one's first byte, in master clocks.</summary>
    private static long[] DriverStarts(List<DacWrite> capture, PcmDriverScript script, int spacing)
    {
        List<DacWrite> dac = [.. capture.Where(w => w.Register == 0x2A)];
        long[] starts = new long[script.Samples.Count];
        for (int id = 1; id <= starts.Length; id++)
        {
            (byte first, byte second) = FirstBytes(id);
            long after = (FrameOf(id - 1, spacing) - 1) * PcmDriverScript.MasterClocksPerFrame;
            int at = Enumerable.Range(0, dac.Count - 1).First(i => dac[i].Master >= after && dac[i].Value == first && dac[i + 1].Value == second);
            starts[id - 1] = dac[at].Master;
        }

        return starts;
    }

    /// <summary>When a start probe's sounds start in the model's <c>$2A</c>: the first sample with each one's first byte, in master clocks.</summary>
    private static long[] ModelStarts(byte[] data, PcmDriverScript script, int spacing)
    {
        long[] starts = new long[script.Samples.Count];
        for (int id = 1; id <= starts.Length; id++)
        {
            (byte first, _) = FirstBytes(id);
            int after = PcmDriverScript.SampleAt(FrameOf(id - 1, spacing));
            int at = Array.FindIndex(data, after, v => v == first);
            starts[id - 1] = at * PcmDriverScript.MasterClocksPerSample;
        }

        return starts;
    }

    /// <summary>The DAC bytes of a start probe's sample <paramref name="id"/>'s first two bytes.</summary>
    private static (byte First, byte Second) FirstBytes(int id)
    {
        int step = PcmDriverProbes.StartStep(id);
        return ((byte)(PcmDriverScript.SampleByte(0, step) + 0x80), (byte)(PcmDriverScript.SampleByte(1, step) + 0x80));
    }

    /// <summary>The values in order, each run of the same value once.</summary>
    private static List<byte> Runs(IEnumerable<byte> values)
    {
        List<byte> runs = [];
        foreach (byte v in values)
        {
            if (runs.Count == 0 || runs[^1] != v)
            {
                runs.Add(v);
            }
        }

        return runs;
    }

    /// <summary>When the DAC enable changes, from the first state (off) on.</summary>
    private static List<long> Changes(IEnumerable<(long Master, bool On)> states)
    {
        List<long> changes = [];
        bool on = false;
        foreach ((long master, bool state) in states)
        {
            if (state != on)
            {
                changes.Add(master);
                on = state;
            }
        }

        return changes;
    }

    /// <summary>A capture's DAC write: when (master clocks from the song's frame 0), which register and what value.</summary>
    internal readonly record struct DacWrite(long Master, int Register, byte Value);

    /// <summary>The capture of <paramref name="name"/>: gzipped 8-byte records (int32 time, register, value, 2 zero bytes).</summary>
    internal static List<DacWrite> Capture(string name)
    {
        string path = Path.Combine(ProbeDirectory, name + ".sgdk.gz");
        Assert.True(File.Exists(path), $"{path} is missing. Run tools/pcm-driver-reference/run.sh to make it.");
        using FileStream file = File.OpenRead(path);
        using GZipStream gzip = new(file, CompressionMode.Decompress);
        using MemoryStream bytes = new();
        gzip.CopyTo(bytes);
        ReadOnlySpan<byte> data = bytes.GetBuffer().AsSpan(0, (int)bytes.Length);
        List<DacWrite> writes = [];
        for (int i = 0; i + 8 <= data.Length; i += 8)
        {
            writes.Add(new DacWrite(BinaryPrimitives.ReadInt32LittleEndian(data[i..]), data[i + 4], data[i + 5]));
        }

        return writes;
    }
}
