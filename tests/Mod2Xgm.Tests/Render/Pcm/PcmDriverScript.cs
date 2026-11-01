using System.Globalization;
using System.Text;
using Mod2Xgm.Render.Fm;
using Mod2Xgm.Render.Pcm;

namespace Mod2Xgm.Tests.Render.Pcm;

/// <summary>A PCM play or stop in a <see cref="PcmDriverScript"/> frame.</summary>
/// <param name="Channel">The PCM channel.</param>
/// <param name="Id">The sample id (1-based); 0 for a stop.</param>
/// <param name="Priority">The priority: XGM's 0, 4, 8 or 12; XGM2's 7 or 15.</param>
/// <param name="HalfSpeed">XGM2: plays at half speed.</param>
internal readonly record struct PcmDriverCommand(int Channel, int Id, int Priority, bool HalfSpeed)
{
    /// <summary>True for a stop (id 0).</summary>
    public bool IsStop => Id == 0;
}

/// <summary>
/// A PCM driver probe (tests/golden/pcm-drivers/README.md): samples and the PCM commands of each frame of a song, as text
/// that both this test project and the C++ reference tool (<c>tools/pcm-driver-reference</c>) play. Lines:
/// <c>driver xgm|xgm2</c>; <c>sample id length step</c> (byte i is <c>(sbyte)(byte)((i + 1) × step)</c>);
/// <c>play channel id priority [half]</c>; <c>stop channel priority</c>; <c>frame count</c> ends the frame and waits
/// <c>count</c> frames; <c>#</c> starts a comment.
/// </summary>
internal sealed class PcmDriverScript
{
    /// <summary>Master clocks per NTSC frame: 262 lines of 3420 (docs/genesis-audio.md).</summary>
    public const long MasterClocksPerFrame = 262 * 3420;

    /// <summary>Master clocks per YM3438 sample.</summary>
    public const long MasterClocksPerSample = 7 * 144;

    /// <summary>Frames played after the script's, as the reference tool does, so sounds and the DAC enable can end.</summary>
    private const int TailFrames = 12;

    private readonly List<string> _lines = [];
    private readonly List<PcmDriverCommand> _current = [];

    /// <summary>Starts a script for <paramref name="xgm2"/>'s driver.</summary>
    /// <param name="name">The probe's name (its file name).</param>
    /// <param name="description">What the probe plays and checks; written as the file's header comment.</param>
    /// <param name="xgm2">True for XGM2's driver, false for XGM's.</param>
    public PcmDriverScript(string name, string description, bool xgm2)
    {
        Name = name;
        Description = description;
        Xgm2 = xgm2;
        _lines.Add(xgm2 ? "driver xgm2" : "driver xgm");
    }

    /// <summary>The probe's name.</summary>
    private string Name { get; }

    /// <summary>What the probe checks.</summary>
    private string Description { get; }

    /// <summary>True for XGM2's driver, false for XGM's.</summary>
    public bool Xgm2 { get; private set; }

    /// <summary>The samples, by id − 1.</summary>
    public List<sbyte[]> Samples { get; } = [];

    /// <summary>The commands at the start of each frame.</summary>
    public List<IReadOnlyList<PcmDriverCommand>> Frames { get; } = [];

    /// <summary>Byte <paramref name="i"/> of a sample with <paramref name="step"/>: <c>(sbyte)(byte)((i + 1) × step)</c>.</summary>
    public static sbyte SampleByte(int i, int step) => unchecked((sbyte)(byte)((i + 1) * step));

    /// <summary>Adds the next sample (id = count + 1).</summary>
    public PcmDriverScript Sample(int length, int step)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        Samples.Add([.. Enumerable.Range(0, length).Select(i => SampleByte(i, step))]);
        _lines.Add(string.Create(CultureInfo.InvariantCulture, $"sample {Samples.Count} {length} {step}"));
        return this;
    }

    /// <summary>Plays sample <paramref name="id"/> on <paramref name="channel"/> in the current frame.</summary>
    public PcmDriverScript Play(int channel, int id, int priority, bool halfSpeed = false)
    {
        _current.Add(new PcmDriverCommand(channel, id, priority, halfSpeed));
        _lines.Add(string.Create(CultureInfo.InvariantCulture, $"play {channel} {id} {priority}{(halfSpeed ? " half" : "")}"));
        return this;
    }

    /// <summary>Stops <paramref name="channel"/> in the current frame.</summary>
    public PcmDriverScript Stop(int channel, int priority)
    {
        _current.Add(new PcmDriverCommand(channel, 0, priority, false));
        _lines.Add(string.Create(CultureInfo.InvariantCulture, $"stop {channel} {priority}"));
        return this;
    }

    /// <summary>Ends the current frame and waits <paramref name="count"/> frames in all.</summary>
    public PcmDriverScript Frame(int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        Frames.Add([.. _current]);
        _current.Clear();
        for (int i = 1; i < count; i++)
        {
            Frames.Add([]);
        }

        _lines.Add(string.Create(CultureInfo.InvariantCulture, $"frame {count}"));
        return this;
    }

    /// <summary>The script as text, header comment first.</summary>
    public string ToText()
    {
        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"# PCM driver probe: {Name}\n");
        foreach (string line in Description.Split('\n'))
        {
            text.Append(CultureInfo.InvariantCulture, $"# {line}\n");
        }

        foreach (string line in _lines)
        {
            text.Append(line).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Reads a script's text back (the name and description are not read back).</summary>
    /// <exception cref="FormatException">A line is not a step.</exception>
    public static PcmDriverScript Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        PcmDriverScript script = new("parsed", "", xgm2: false);
        script._lines.Clear();
        string[] lines = text.Split('\n');
        for (int number = 0; number < lines.Length; number++)
        {
            string line = lines[number].Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int N(int i) => int.Parse(f[i], NumberStyles.None, CultureInfo.InvariantCulture);
            switch (f)
            {
                case ["driver", "xgm" or "xgm2"]:
                    script.Xgm2 = f[1] == "xgm2";
                    script._lines.Add(line);
                    break;
                case ["sample", _, _, _] when N(1) == script.Samples.Count + 1:
                    script.Sample(N(2), N(3));
                    break;
                case ["play", _, _, _]:
                case ["play", _, _, _, "half"]:
                    script.Play(N(1), N(2), N(3), f.Length == 5);
                    break;
                case ["stop", _, _]:
                    script.Stop(N(1), N(2));
                    break;
                case ["frame", _]:
                    script.Frame(N(1));
                    break;
                default:
                    throw new FormatException($"Line {number + 1}: '{line}' is not a step.");
            }
        }

        return script;
    }

    /// <summary>
    /// Plays the script through the model (buffered, as the Genesis renderer uses it): each frame's commands at the frame's
    /// first YM3438 sample, frame 0 at sample 0, for the script's frames and <see cref="TailFrames"/> more.
    /// </summary>
    /// <returns>Per YM3438 sample, the bytes in <c>$2A</c> and <c>$2B</c>.</returns>
    public (byte[] Data, byte[] Enable) Render() => Render(Frames.Count + TailFrames, out _);

    /// <summary>Plays the first <paramref name="frames"/> frames (as <see cref="Render()"/>); <paramref name="beats"/> gets the beats sent.</summary>
    public (byte[] Data, byte[] Enable) Render(int frames, out long beats)
    {
        SoftwarePcmDriver driver = Xgm2 ? new Xgm2PcmDriver() : new XgmPcmDriver();
        int total = SampleAt(frames);
        byte[] data = new byte[total];
        byte[] enable = new byte[total];
        for (int f = 0; f < frames; f++)
        {
            if (f < Frames.Count)
            {
                foreach (PcmDriverCommand c in Frames[f])
                {
                    Apply(driver, c);
                }
            }

            int from = SampleAt(f);
            int to = SampleAt(f + 1);
            driver.Generate(data.AsSpan(from, to - from), enable.AsSpan(from, to - from));
        }

        beats = driver.BeatsSent;
        return (data, enable);
    }

    /// <summary>The first YM3438 sample of frame <paramref name="frame"/>.</summary>
    public static int SampleAt(int frame) => (int)(frame * MasterClocksPerFrame / MasterClocksPerSample);

    private void Apply(SoftwarePcmDriver driver, PcmDriverCommand c)
    {
        switch (driver)
        {
            case XgmPcmDriver xgm when c.IsStop:
                xgm.Stop(c.Channel, c.Priority);
                break;
            case XgmPcmDriver xgm:
                xgm.Play(c.Channel, Samples[c.Id - 1], c.Priority);
                break;
            case Xgm2PcmDriver xgm2 when c.IsStop:
                xgm2.Stop(c.Channel);
                break;
            case Xgm2PcmDriver xgm2:
                xgm2.Play(c.Channel, Samples[c.Id - 1], c.Priority, c.HalfSpeed);
                break;
        }
    }

    /// <summary>The model's beat rate for this script's driver on NTSC.</summary>
    public double BeatRateHz => Xgm2 ? Xgm2PcmDriver.BeatRateHz(Ym3438.NtscClockHz) : XgmPcmDriver.BeatRateHz(Ym3438.NtscClockHz);
}
