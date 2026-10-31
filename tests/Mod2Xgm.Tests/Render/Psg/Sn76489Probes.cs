namespace Mod2Xgm.Tests.Render.Psg;

/// <summary>
/// The SN76489 probe scripts (plan T7.3, tests/golden/sn76489/README.md): short write sequences that between them reach
/// every part of the PSG a song can use: the tone periods (including 0 and 1), all sixteen volumes, the three channels
/// together, white and periodic noise at every rate, noise that follows tone 3, the noise restart, and the latch's
/// write quirks. Each is played by the port and by MAME and Nuked-PSG (<c>tools/sn76489-reference</c>), and the
/// results compared.
/// </summary>
internal static class Sn76489Probes
{
    /// <summary>Every probe, by name.</summary>
    public static IReadOnlyDictionary<string, Func<Sn76489Script>> All { get; } = new Dictionary<string, Func<Sn76489Script>>
    {
        ["tone"] = Tone,
        ["volumes"] = Volumes,
        ["periods"] = Periods,
        ["chord"] = Chord,
        ["white-noise"] = WhiteNoise,
        ["periodic-noise"] = PeriodicNoise,
        ["noise-restart"] = NoiseRestart,
        ["write-quirks"] = WriteQuirks,
        ["power-on-latch"] = PowerOnLatch,
    };

    private static Sn76489Script Tone() =>
        new Sn76489Script("tone", "Tone 1 alone at 440 Hz, the loudest volume; then off.")
            .Note(0, 440)
            .Volume(0, 0)
            .Seconds(0.2)
            .Volume(0, 15)
            .Seconds(0.02);

    private static Sn76489Script Volumes()
    {
        Sn76489Script script = new("volumes", "Tone 2 at 330 Hz through all sixteen volumes, 0 (loudest) to 15 (off).");
        script.Note(1, 330);
        for (int volume = 0; volume < 16; volume++)
        {
            script.Volume(1, volume).Seconds(0.03);
        }

        return script;
    }

    private static Sn76489Script Periods()
    {
        Sn76489Script script = new(
            "periods",
            "Tone 3 at periods from the top ($3FF, 109 Hz) to the bottom, including 1 and 0 (both flip every step on Sega's\n" +
            "chip, about 112 kHz), then the period changed by one byte at a time during the note.");
        script.Volume(2, 2);
        foreach (int period in new[] { 0x3ff, 0x200, 0x0fe, 0x040, 0x011, 0x008, 0x003, 0x002, 0x001, 0x000 })
        {
            script.Period(2, period).Seconds(0.03);
        }

        // Only the high 6 bits (a data byte, latch still on tone 3's period), then only the low 4 (a latch byte).
        return script.Period(2, 0x0fe)
            .Seconds(0.03)
            .Write(0x08)
            .Seconds(0.03)
            .Write(0xc3)
            .Seconds(0.03)
            .Volume(2, 15)
            .Seconds(0.01);
    }

    private static Sn76489Script Chord() =>
        new Sn76489Script("chord", "A chord on the three tone channels at different volumes, then all four channels at the loudest volume.")
            .Note(0, 261.6)
            .Note(1, 329.6)
            .Note(2, 392.0)
            .Volume(0, 0)
            .Volume(1, 3)
            .Volume(2, 6)
            .Seconds(0.15)
            .Volume(1, 0)
            .Volume(2, 0)
            .Noise(1, white: true)
            .Volume(3, 0)
            .Seconds(0.1)
            .Volume(0, 15)
            .Volume(1, 15)
            .Volume(2, 15)
            .Volume(3, 15)
            .Seconds(0.01);

    private static Sn76489Script WhiteNoise() => NoiseRates("white-noise", white: true);

    private static Sn76489Script PeriodicNoise() => NoiseRates("periodic-noise", white: false);

    /// <summary>The noise at each fixed rate, then following tone 3 (silent) at a high and a low period, then a period change while it follows.</summary>
    private static Sn76489Script NoiseRates(string name, bool white)
    {
        Sn76489Script script = new(
            name,
            $"{(white ? "White" : "Periodic")} noise at rates 0-2 (clock/512, /1024, /2048), then following tone 3 (muted) at\n" +
            "periods $010 and $200; then tone 3's period changed while the noise follows it.");
        script.Volume(3, 0);
        for (int rate = 0; rate < 3; rate++)
        {
            script.Noise(rate, white).Seconds(0.05);
        }

        return script.Period(2, 0x010)
            .Noise(3, white)
            .Seconds(0.05)
            .Period(2, 0x200)
            .Seconds(0.05)
            .Period(2, 0x040)
            .Seconds(0.05)
            .Volume(3, 15)
            .Seconds(0.01);
    }

    private static Sn76489Script NoiseRestart()
    {
        Sn76489Script script = new(
            "noise-restart",
            "White noise at rate 0, its register written again (unchanged) every 2 ms, which restarts the shift register\n" +
            "each time; then the noise at volumes 0-15.");
        script.Volume(3, 0);
        for (int i = 0; i < 25; i++)
        {
            script.Noise(0, white: true).Seconds(0.002);
        }

        for (int volume = 0; volume < 16; volume++)
        {
            script.Volume(3, volume).Seconds(0.01);
        }

        return script;
    }

    private static Sn76489Script WriteQuirks()
    {
        Sn76489Script script = new(
            "write-quirks",
            "Data bytes (bit 7 clear) after each kind of latch: after a volume latch they set the volume, after the noise\n" +
            "latch they set the noise, after a period latch the high bits; a latch byte alone changes only the low 4 bits.");
        script.Note(0, 440).Volume(0, 0).Seconds(0.03);

        // The latch is on tone 1's volume: data bytes now set it.
        script.Write(0x04).Seconds(0.03).Write(0x08).Seconds(0.03).Write(0x00).Seconds(0.03);

        // The latch is on the noise register: a data byte sets it (and restarts the noise).
        script.Volume(3, 2).Noise(0, white: false).Seconds(0.03).Write(0x05).Seconds(0.03).Volume(3, 15);

        // The latch is on tone 1's period: data bytes set its high bits; a latch byte sets the low bits only.
        return script.Write(0x80)
            .Seconds(0.03)
            .Write(0x0a)
            .Seconds(0.03)
            .Write(0x8f)
            .Seconds(0.03)
            .Volume(0, 15)
            .Seconds(0.01);
    }

    private static Sn76489Script PowerOnLatch() =>
        new Sn76489Script(
                "power-on-latch",
                "A data byte written before any latch byte goes to whichever register the latch points at after power-on (on\n" +
                "MAME's Sega PSG, tone 2's volume, so tone 2 then sounds at volume 0); then tone 1 set up the normal way.")
            .Write(0x00)
            .Note(1, 660)
            .Seconds(0.05)
            .Volume(1, 15)
            .Note(0, 440)
            .Volume(0, 0)
            .Seconds(0.05)
            .Volume(0, 15)
            .Seconds(0.01);
}
