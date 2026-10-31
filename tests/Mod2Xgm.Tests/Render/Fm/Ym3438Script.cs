using System.Globalization;
using System.Text;
using Mod2Xgm.Render.Fm;

namespace Mod2Xgm.Tests.Render.Fm;

/// <summary>One step of a <see cref="Ym3438Script"/>: a register write, or a run of output samples.</summary>
/// <param name="Part">For a write: port pair 0 (channels 1–3, system) or 1 (channels 4–6).</param>
/// <param name="Register">For a write: the register.</param>
/// <param name="Value">For a write: the value.</param>
/// <param name="Samples">For a run: how many samples to play; 0 for a write.</param>
internal readonly record struct Ym3438Step(int Part, int Register, int Value, int Samples)
{
    /// <summary>True for a run of samples, false for a register write.</summary>
    public bool IsWait => Samples > 0;
}

/// <summary>
/// A register-write script for the YM3438 probes (tests/golden/ym3438/README.md): the text both this test project and
/// the C++ reference tool (<c>tools/ym3438-reference</c>) play. One step per line: <c>w part reg value</c> (hex) writes a
/// register, <c>s count</c> (decimal) plays that many samples; <c>#</c> starts a comment.
/// </summary>
internal sealed class Ym3438Script
{
    /// <summary>Samples per second at the NTSC clock: the rate the probes' times are counted in.</summary>
    public static readonly double SampleRate = Ym3438.SampleRateHz(Ym3438.NtscClockHz);

    /// <summary>The register offset of each operator O1–O4 within a channel's block: the register order is O1, O3, O2, O4.</summary>
    private static ReadOnlySpan<int> SlotOffsets => [0, 8, 4, 12];

    private readonly List<Ym3438Step> _steps = [];

    /// <summary>Starts an empty script.</summary>
    /// <param name="name">The probe's name (its file name).</param>
    /// <param name="description">What the probe plays and checks; written as the file's header comment.</param>
    public Ym3438Script(string name, string description)
    {
        Name = name;
        Description = description;
    }

    /// <summary>The probe's name.</summary>
    private string Name { get; }

    /// <summary>What the probe checks.</summary>
    private string Description { get; }

    /// <summary>The steps, in order.</summary>
    public IReadOnlyList<Ym3438Step> Steps => _steps;

    /// <summary>The total number of samples the script plays.</summary>
    public int TotalSamples => _steps.Sum(s => s.Samples);

    /// <summary>Reads a script's text back into steps (the name and description are not read back).</summary>
    /// <exception cref="FormatException">A line is neither a write, a run nor a comment.</exception>
    public static IReadOnlyList<Ym3438Step> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<Ym3438Step> steps = [];
        string[] lines = text.Split('\n');
        for (int number = 0; number < lines.Length; number++)
        {
            string line = lines[number].Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts is ["w", var part, var register, var value])
            {
                steps.Add(new Ym3438Step(Hex(part), Hex(register), Hex(value), 0));
            }
            else if (parts is ["s", var count] && int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out int samples) && samples > 0)
            {
                steps.Add(new Ym3438Step(0, 0, 0, samples));
            }
            else
            {
                throw new FormatException($"Line {number + 1}: '{line}' is not a step.");
            }
        }

        return steps;

        static int Hex(string s) => int.Parse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    /// <summary>Plays <paramref name="steps"/> on a fresh chip; the output interleaved left, right.</summary>
    public static int[] Render(IReadOnlyList<Ym3438Step> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        Ym3438 chip = new();
        int total = steps.Sum(s => s.Samples);
        int[] output = new int[total * 2];
        int[] left = new int[total];
        int[] right = new int[total];
        int at = 0;
        foreach (Ym3438Step step in steps)
        {
            if (step.IsWait)
            {
                chip.Generate(left.AsSpan(at, step.Samples), right.AsSpan(at, step.Samples));
                at += step.Samples;
            }
            else
            {
                chip.WriteRegister(step.Part, (byte)step.Register, (byte)step.Value);
            }
        }

        for (int i = 0; i < total; i++)
        {
            output[2 * i] = left[i];
            output[(2 * i) + 1] = right[i];
        }

        return output;
    }

    /// <summary>The script as text, header comment first.</summary>
    public string ToText()
    {
        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"# YM3438 probe: {Name}\n");
        foreach (string line in Description.Split('\n'))
        {
            text.Append(CultureInfo.InvariantCulture, $"# {line}\n");
        }

        foreach (Ym3438Step step in _steps)
        {
            text.Append(step.IsWait
                ? string.Create(CultureInfo.InvariantCulture, $"s {step.Samples}\n")
                : string.Create(CultureInfo.InvariantCulture, $"w {step.Part} {step.Register:x2} {step.Value:x2}\n"));
        }

        return text.ToString();
    }

    /// <summary>Writes <paramref name="value"/> to <paramref name="register"/> on <paramref name="part"/>.</summary>
    public Ym3438Script Write(int part, int register, int value)
    {
        _steps.Add(new Ym3438Step(part, register, value & 0xff, 0));
        return this;
    }

    /// <summary>Plays <paramref name="samples"/> samples.</summary>
    public Ym3438Script Wait(int samples)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samples);
        _steps.Add(new Ym3438Step(0, 0, 0, samples));
        return this;
    }

    /// <summary>Plays <paramref name="seconds"/> worth of samples at the NTSC rate.</summary>
    public Ym3438Script Seconds(double seconds) => Wait((int)Math.Round(seconds * SampleRate));

    /// <summary>Writes a system register (port 0).</summary>
    public Ym3438Script System(int register, int value) => Write(0, register, value);

    /// <summary>Writes a per-channel register (<c>$A0</c>, <c>$B0</c>, …) of <paramref name="channel"/> (0–5).</summary>
    public Ym3438Script Channel(int channel, int register, int value) => Write(channel / 3, register + (channel % 3), value);

    /// <summary>Writes a per-operator register (<c>$30</c>…<c>$90</c>) of operator <paramref name="op"/> (1–4) of <paramref name="channel"/>.</summary>
    public Ym3438Script Operator(int channel, int op, int register, int value) =>
        Write(channel / 3, register + SlotOffsets[op - 1] + (channel % 3), value);

    /// <summary>Sets a whole voice: algorithm, feedback, and operators O1–O4.</summary>
    public Ym3438Script Patch(int channel, int algorithm, int feedback, FmOp o1, FmOp o2, FmOp o3, FmOp o4)
    {
        Channel(channel, 0xb0, (feedback << 3) | algorithm);
        FmOp[] ops = [o1, o2, o3, o4];
        for (int op = 1; op <= 4; op++)
        {
            FmOp o = ops[op - 1];
            Operator(channel, op, 0x30, (o.Dt << 4) | o.Mul);
            Operator(channel, op, 0x40, o.Tl);
            Operator(channel, op, 0x50, (o.Ks << 6) | o.Ar);
            Operator(channel, op, 0x60, (o.Am << 7) | o.Dr);
            Operator(channel, op, 0x70, o.Sr);
            Operator(channel, op, 0x80, (o.Sl << 4) | o.Rr);
            Operator(channel, op, 0x90, o.Ssg);
        }

        return this;
    }

    /// <summary>Sets the panning (<c>$B4</c>): left, right, tremolo depth and vibrato depth.</summary>
    public Ym3438Script Pan(int channel, bool left, bool right, int ams = 0, int pms = 0) =>
        Channel(channel, 0xb4, (left ? 0x80 : 0) | (right ? 0x40 : 0) | (ams << 4) | pms);

    /// <summary>Sets block and F-number: the high byte first (it latches), then the low byte.</summary>
    public Ym3438Script Frequency(int channel, int block, int fnum)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fnum, 0x7ff);
        Channel(channel, 0xa4, (block << 3) | (fnum >> 8));
        return Channel(channel, 0xa0, fnum & 0xff);
    }

    /// <summary>Block and F-number for <paramref name="hz"/> in block <paramref name="block"/> (docs/genesis-audio.md#pitch-conversion).</summary>
    public Ym3438Script Note(int channel, int block, double hz) =>
        Frequency(channel, block, (int)Math.Round(hz * (1 << 20) / SampleRate / (1 << (block - 1))));

    /// <summary>Keys on the operators in <paramref name="mask"/> (bit 0 = O1) of <paramref name="channel"/>, and off the rest.</summary>
    public Ym3438Script Key(int channel, int mask = 0xf) => System(0x28, (mask << 4) | (channel % 3) | ((channel / 3) << 2));

    /// <summary>Keys every operator of <paramref name="channel"/> off.</summary>
    public Ym3438Script KeyOff(int channel) => Key(channel, 0);
}

/// <summary>One operator's settings, register fields as the chip has them.</summary>
/// <param name="Mul">Frequency multiple, 0–15 (0 = ½).</param>
/// <param name="Tl">Total level, 0 (loudest) – 127.</param>
/// <param name="Ar">Attack rate, 0–31.</param>
/// <param name="Dr">Decay rate, 0–31.</param>
/// <param name="Sr">Sustain rate, 0–31.</param>
/// <param name="Sl">Sustain level, 0–15.</param>
/// <param name="Rr">Release rate, 0–15.</param>
/// <param name="Dt">Detune, 0–7.</param>
/// <param name="Ks">Key scale rate, 0–3.</param>
/// <param name="Am">LFO tremolo on (1) or off (0).</param>
/// <param name="Ssg">SSG-EG: 0 off, 8–15 on with shape 0–7.</param>
internal sealed record FmOp(int Mul = 1, int Tl = 0, int Ar = 31, int Dr = 0, int Sr = 0, int Sl = 0, int Rr = 15, int Dt = 0, int Ks = 0, int Am = 0, int Ssg = 0)
{
    /// <summary>An operator turned all the way down.</summary>
    public static FmOp Silent => new(Tl: 127);
}
