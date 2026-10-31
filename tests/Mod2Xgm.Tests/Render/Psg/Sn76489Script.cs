using System.Globalization;
using System.Text;
using Mod2Xgm.Render.Psg;

namespace Mod2Xgm.Tests.Render.Psg;

/// <summary>One step of a <see cref="Sn76489Script"/>: a byte written to the PSG port, or a run of output samples.</summary>
/// <param name="Value">For a write: the byte.</param>
/// <param name="Samples">For a run: how many samples to play; 0 for a write.</param>
internal readonly record struct Sn76489Step(int Value, int Samples)
{
    /// <summary>True for a run of samples, false for a write.</summary>
    public bool IsWait => Samples > 0;
}

/// <summary>
/// A write script for the SN76489 probes (tests/golden/sn76489/README.md): the text both this test project and the C++
/// reference tool (<c>tools/sn76489-reference</c>) play. One step per line: <c>w value</c> (hex) writes a byte to the
/// PSG port, <c>s count</c> (decimal) plays that many samples; <c>#</c> starts a comment.
/// </summary>
internal sealed class Sn76489Script
{
    /// <summary>Samples per second at the NTSC clock: the rate the probes' times are counted in.</summary>
    public static readonly double SampleRate = Sn76489.SampleRateHz(Sn76489.NtscClockHz);

    private readonly List<Sn76489Step> _steps = [];

    /// <summary>Starts an empty script.</summary>
    /// <param name="name">The probe's name (its file name).</param>
    /// <param name="description">What the probe plays and checks; written as the file's header comment.</param>
    public Sn76489Script(string name, string description)
    {
        Name = name;
        Description = description;
    }

    /// <summary>The probe's name.</summary>
    private string Name { get; }

    /// <summary>What the probe checks.</summary>
    private string Description { get; }

    /// <summary>The steps, in order.</summary>
    public IReadOnlyList<Sn76489Step> Steps => _steps;

    /// <summary>Reads a script's text back into steps (the name and description are not read back).</summary>
    /// <exception cref="FormatException">A line is neither a write, a run nor a comment.</exception>
    public static IReadOnlyList<Sn76489Step> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<Sn76489Step> steps = [];
        string[] lines = text.Split('\n');
        for (int number = 0; number < lines.Length; number++)
        {
            string line = lines[number].Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts is ["w", var value] && int.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int data) && data <= 0xff)
            {
                steps.Add(new Sn76489Step(data, 0));
            }
            else if (parts is ["s", var count] && int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out int samples) && samples > 0)
            {
                steps.Add(new Sn76489Step(0, samples));
            }
            else
            {
                throw new FormatException($"Line {number + 1}: '{line}' is not a step.");
            }
        }

        return steps;
    }

    /// <summary>Plays <paramref name="steps"/> on a fresh chip.</summary>
    public static int[] Render(IReadOnlyList<Sn76489Step> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        Sn76489 chip = new();
        int[] output = new int[steps.Sum(s => s.Samples)];
        int at = 0;
        foreach (Sn76489Step step in steps)
        {
            if (step.IsWait)
            {
                chip.Generate(output.AsSpan(at, step.Samples));
                at += step.Samples;
            }
            else
            {
                chip.Write((byte)step.Value);
            }
        }

        return output;
    }

    /// <summary>The script as text, header comment first.</summary>
    public string ToText()
    {
        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"# SN76489 probe: {Name}\n");
        foreach (string line in Description.Split('\n'))
        {
            text.Append(CultureInfo.InvariantCulture, $"# {line}\n");
        }

        foreach (Sn76489Step step in _steps)
        {
            text.Append(step.IsWait
                ? string.Create(CultureInfo.InvariantCulture, $"s {step.Samples}\n")
                : string.Create(CultureInfo.InvariantCulture, $"w {step.Value:x2}\n"));
        }

        return text.ToString();
    }

    /// <summary>Writes the byte <paramref name="value"/> to the PSG port.</summary>
    public Sn76489Script Write(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 0xff);
        _steps.Add(new Sn76489Step(value, 0));
        return this;
    }

    /// <summary>Plays <paramref name="samples"/> samples.</summary>
    public Sn76489Script Wait(int samples)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samples);
        _steps.Add(new Sn76489Step(0, samples));
        return this;
    }

    /// <summary>Plays <paramref name="seconds"/> worth of samples at the NTSC rate.</summary>
    public Sn76489Script Seconds(double seconds) => Wait((int)Math.Round(seconds * SampleRate));

    /// <summary>Sets tone <paramref name="channel"/>'s (0–2) 10-bit period: the latch byte with the low 4 bits, then the high 6.</summary>
    public Sn76489Script Period(int channel, int period)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(period, 0x3ff);
        Write(0x80 | (channel << 5) | (period & 0xf));
        return Write(period >> 4);
    }

    /// <summary>The period for <paramref name="hz"/> on a tone channel: clock / (32 × Hz) (docs/genesis-audio.md#pitch-conversion).</summary>
    public Sn76489Script Note(int channel, double hz) =>
        Period(channel, Math.Clamp((int)Math.Round(Sn76489.NtscClockHz / (32 * hz)), 1, 0x3ff));

    /// <summary>Sets <paramref name="channel"/>'s (0–2 tone, 3 noise) attenuation: 0 loudest, 15 off.</summary>
    public Sn76489Script Volume(int channel, int volume) => Write(0x90 | (channel << 5) | (volume & 0xf));

    /// <summary>Sets the noise: <paramref name="rate"/> 0–2 for clock/512, /1024, /2048, 3 to follow tone 3; white or periodic.</summary>
    public Sn76489Script Noise(int rate, bool white) => Write(0xe0 | (white ? 4 : 0) | (rate & 3));
}
