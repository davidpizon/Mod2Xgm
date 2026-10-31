using Mod2Xgm.Render.Fm;

namespace Mod2Xgm.Tests.Render.Fm;

/// <summary>The YM3438 model's interface: ports, argument checks and the simple facts the probes take for granted.</summary>
public sealed class Ym3438Tests
{
    /// <summary>A whole sine voice on channel 1 at 440 Hz, ready to key on.</summary>
    private static Ym3438Script SineVoice(int channel = 0) =>
        new Ym3438Script("test", "test")
            .Patch(channel, 7, 0, FmOp.Silent, FmOp.Silent, FmOp.Silent, new FmOp())
            .Note(channel, 4, 440);

    private static (int[] Left, int[] Right) Play(Ym3438 chip, int samples)
    {
        int[] left = new int[samples];
        int[] right = new int[samples];
        chip.Generate(left, right);
        return (left, right);
    }

    private static void Apply(Ym3438 chip, Ym3438Script script)
    {
        foreach (Ym3438Step step in script.Steps)
        {
            chip.WriteRegister(step.Part, (byte)step.Register, (byte)step.Value);
        }
    }

    /// <summary>One sample per 144 master clocks: about 53 267 Hz on an NTSC Genesis.</summary>
    [Fact]
    public void SampleRateIsTheClockOver144()
    {
        Assert.Equal(7_670_453 / 144.0, Ym3438.SampleRateHz(Ym3438.NtscClockHz), 9);
        Assert.Equal(7_600_489 / 144.0, Ym3438.SampleRateHz(Ym3438.PalClockHz), 9);
    }

    /// <summary>A fresh chip is silent.</summary>
    [Fact]
    public void AFreshChipIsSilent()
    {
        (int[] left, int[] right) = Play(new Ym3438(), 1000);
        Assert.All(left, v => Assert.Equal(0, v));
        Assert.All(right, v => Assert.Equal(0, v));
    }

    /// <summary>A keyed sine sounds on both speakers by default, below full scale; a reset silences it.</summary>
    [Fact]
    public void AKeyedNoteSoundsUntilReset()
    {
        Ym3438 chip = new();
        Apply(chip, SineVoice().Key(0));
        (int[] left, int[] right) = Play(chip, 2000);
        Assert.Equal(left, right);
        Assert.InRange(left.Max(), 1000, Ym3438.FullScale);

        chip.Reset();
        (left, _) = Play(chip, 10);
        Assert.All(left, v => Assert.Equal(0, v));
    }

    /// <summary>Channel 4 lives on port 1: an address on port 2 and data on port 3.</summary>
    [Fact]
    public void Port1ReachesChannels4To6()
    {
        Ym3438 chip = new();
        Apply(chip, SineVoice(3).Key(3));
        (int[] left, _) = Play(chip, 2000);
        Assert.InRange(left.Max(), 1000, Ym3438.FullScale);
    }

    /// <summary>Data on a port whose address was set on the other port is ignored, as ymfm does.</summary>
    [Fact]
    public void DataOnTheWrongPortIsIgnored()
    {
        Ym3438 chip = new();
        Apply(chip, SineVoice());

        // Address on port 0 (key on), data on port 1's data port: ignored.
        chip.Write(0, 0x28);
        chip.Write(3, 0xf0);

        // Address on port 1, data on port 0: ignored.
        chip.Write(2, 0x28);
        chip.Write(1, 0xf0);

        (int[] left, _) = Play(chip, 500);
        Assert.All(left, v => Assert.Equal(0, v));
    }

    /// <summary>With the DAC on, channel 6 plays its 9-bit value (bit 0 from <c>$2C</c> bit 3), with channel 6's panning.</summary>
    [Fact]
    public void TheDacReplacesChannel6()
    {
        Ym3438 chip = new();
        chip.WriteRegister(0, 0x2b, 0x80);
        chip.WriteRegister(0, 0x2a, 0xff);
        chip.WriteRegister(0, 0x2c, 0x08);
        chip.WriteRegister(1, 0xb6, 0x80);
        (int[] left, int[] right) = Play(chip, 4);

        // 0xff ^ 0x80 = 0x7f, shifted up with low bit 1: 255, scaled by 128 / 6.
        Assert.All(left, v => Assert.Equal(255 * 128 / 6, v));
        Assert.All(right, v => Assert.Equal(0, v));

        chip.WriteRegister(0, 0x2b, 0x00);
        (left, _) = Play(chip, 4);
        Assert.All(left, v => Assert.Equal(0, v));
    }

    /// <summary>Only parts 0 and 1 exist, and the two output spans must be the same length.</summary>
    [Fact]
    public void BadArgumentsAreRefused()
    {
        Ym3438 chip = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => chip.WriteRegister(2, 0x28, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => chip.WriteRegister(-1, 0x28, 0));
        Assert.Throws<ArgumentException>(() => chip.Generate(new int[3], new int[4]));
    }

    /// <summary>The probe script text reads back into the same steps, and a line that isn't a step is refused.</summary>
    [Fact]
    public void ScriptsReadBack()
    {
        Ym3438Script script = SineVoice().Key(0).Wait(10);
        Assert.Equal(script.Steps, Ym3438Script.Parse(script.ToText()));
        Assert.Equal(10, script.TotalSamples);
        Assert.Throws<FormatException>(() => Ym3438Script.Parse("x 1 2"));
        Assert.Throws<FormatException>(() => Ym3438Script.Parse("s 0"));
    }
}
