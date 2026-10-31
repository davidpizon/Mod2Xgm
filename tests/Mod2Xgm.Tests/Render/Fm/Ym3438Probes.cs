namespace Mod2Xgm.Tests.Render.Fm;

/// <summary>
/// The YM3438 probe scripts (plan T7.2, tests/golden/ym3438/README.md): short register-write sequences that between them
/// reach every part of the chip the converter can use: the eight algorithms, feedback, envelopes and key scaling,
/// detune and multiple, the LFO, SSG-EG, CH3 special mode, the DAC, panning and clipping, and the write quirks. Each is
/// played by the port and by ymfm and Nuked-OPN2 (<c>tools/ym3438-reference</c>), and the results compared.
/// </summary>
internal static class Ym3438Probes
{
    /// <summary>Every probe, by name.</summary>
    public static IReadOnlyDictionary<string, Func<Ym3438Script>> All { get; } = new Dictionary<string, Func<Ym3438Script>>
    {
        ["sine"] = Sine,
        ["algorithms"] = Algorithms,
        ["feedback"] = Feedback,
        ["envelopes"] = Envelopes,
        ["detune-multiple"] = DetuneMultiple,
        ["lfo"] = Lfo,
        ["ssg-eg"] = SsgEg,
        ["ch3-special"] = Ch3Special,
        ["dac"] = Dac,
        ["six-channels"] = SixChannels,
        ["write-quirks"] = WriteQuirks,
    };

    /// <summary>A carrier alone at full level: the plain sine.</summary>
    private static readonly FmOp Carrier = new(Mul: 1, Rr: 8);

    /// <summary>A modulator with a short decay, so a note's brightness changes as it sounds.</summary>
    private static readonly FmOp Modulator = new(Mul: 2, Tl: 28, Dr: 12, Sl: 4, Sr: 3, Rr: 8);

    private static Ym3438Script Sine() =>
        new Ym3438Script("sine", "Operator 4 alone (algorithm 7) at 440 Hz, block 4: held, then released at rate 8.")
            .Patch(0, 7, 0, FmOp.Silent, FmOp.Silent, FmOp.Silent, Carrier)
            .Note(0, 4, 440)
            .Key(0)
            .Seconds(0.25)
            .KeyOff(0)
            .Seconds(0.1);

    private static Ym3438Script Algorithms()
    {
        Ym3438Script script = new("algorithms", "Each algorithm 0-7 in turn on channel 1, the same four operators, feedback 4, at 220 Hz.");
        for (int algorithm = 0; algorithm < 8; algorithm++)
        {
            script.Patch(0, algorithm, 4, Modulator with { Mul = 1 }, Modulator with { Mul = 3, Tl = 34 }, Modulator with { Mul = 2, Tl = 30 }, Carrier with { Tl = 8 })
                .Note(0, 3, 220)
                .Key(0)
                .Seconds(0.09)
                .KeyOff(0)
                .Seconds(0.02);
        }

        return script;
    }

    private static Ym3438Script Feedback()
    {
        Ym3438Script script = new("feedback", "Operator 1 alone (algorithm 7) with feedback 0-7, rising until it turns to noise.");
        for (int feedback = 0; feedback < 8; feedback++)
        {
            script.Patch(0, 7, feedback, Carrier, FmOp.Silent, FmOp.Silent, FmOp.Silent)
                .Note(0, 4, 330)
                .Key(0)
                .Seconds(0.05)
                .KeyOff(0)
                .Seconds(0.01);
        }

        return script;
    }

    private static Ym3438Script Envelopes()
    {
        Ym3438Script script = new(
            "envelopes",
            "Envelope shapes on four channels: a slow attack, decay to a sustain level with a sustain rate, the fastest\n" +
            "release, key scaling at a high block, sustain level 15, and attack rate 31 set after the key on (it no longer jumps).");
        script.Patch(0, 7, 0, FmOp.Silent, FmOp.Silent, FmOp.Silent, new FmOp(Ar: 12, Dr: 10, Sl: 6, Sr: 5, Rr: 6))
            .Pan(0, true, false)
            .Note(0, 4, 440)
            .Patch(1, 7, 0, FmOp.Silent, FmOp.Silent, FmOp.Silent, new FmOp(Ar: 31, Dr: 18, Sl: 15, Sr: 0, Rr: 15))
            .Pan(1, false, true)
            .Note(1, 3, 261.6)
            .Patch(2, 7, 0, FmOp.Silent, FmOp.Silent, FmOp.Silent, new FmOp(Ar: 20, Dr: 14, Sl: 3, Sr: 9, Rr: 4, Ks: 3))
            .Note(2, 7, 1400)
            .Patch(3, 7, 0, FmOp.Silent, FmOp.Silent, FmOp.Silent, new FmOp(Ar: 10, Rr: 10))
            .Note(3, 2, 98)
            .Key(0)
            .Key(1)
            .Key(2)
            .Key(3)
            .Seconds(0.05)
            .Operator(3, 4, 0x50, 31)
            .Seconds(0.25)
            .KeyOff(0)
            .KeyOff(1)
            .KeyOff(2)
            .KeyOff(3)
            .Seconds(0.2);
        return script;
    }

    private static Ym3438Script DetuneMultiple()
    {
        Ym3438Script script = new(
            "detune-multiple",
            "Two carriers per note, one plain and one with detune 0-7 and multiple 0-15, low (80 Hz, block 1) and high\n" +
            "(1200 Hz, block 6); then the top of the range (block 7, F-number $7FF, where detune overflows) and the bottom.");
        int[] multiples = [0, 1, 3, 7, 15, 5, 10, 12];
        for (int step = 0; step < 8; step++)
        {
            (int block, double hz) = step % 2 == 0 ? (1, 80.0) : (6, 1200.0);
            script.Patch(0, 7, 0, Carrier with { Tl = 10 }, Carrier with { Tl = 10, Dt = step, Mul = multiples[step] }, FmOp.Silent, FmOp.Silent)
                .Note(0, block, hz)
                .Key(0)
                .Seconds(0.04)
                .KeyOff(0)
                .Seconds(0.005);
        }

        script.Patch(0, 7, 0, Carrier with { Dt = 3, Mul = 15 }, Carrier with { Dt = 7 }, FmOp.Silent, FmOp.Silent)
            .Frequency(0, 7, 0x7ff)
            .Key(0)
            .Seconds(0.04)
            .Frequency(0, 0, 0x001)
            .Seconds(0.02)
            .KeyOff(0)
            .Seconds(0.01);
        return script;
    }

    private static Ym3438Script Lfo()
    {
        Ym3438Script script = new(
            "lfo",
            "Tremolo and vibrato at full depth on a two-operator voice, LFO rates 0-7, then the LFO off while an operator\n" +
            "still has AM on (it stays attenuated).");
        script.Patch(0, 4, 2, Modulator with { Am = 1 }, Carrier with { Am = 1 }, FmOp.Silent, FmOp.Silent)
            .Pan(0, true, true, ams: 3, pms: 7)
            .Note(0, 4, 440)
            .Key(0, 0b0011);
        for (int rate = 0; rate < 8; rate++)
        {
            script.System(0x22, 0x08 | rate).Seconds(0.06);
        }

        script.Pan(0, true, true, ams: 2, pms: 3).Seconds(0.05)
            .System(0x22, 0x00)
            .Seconds(0.05)
            .KeyOff(0)
            .Seconds(0.02);
        return script;
    }

    private static Ym3438Script SsgEg()
    {
        Ym3438Script script = new("ssg-eg", "SSG-EG shapes 0-7 in turn on a carrier with a fast decay, each keyed off while still sounding.");
        for (int shape = 0; shape < 8; shape++)
        {
            script.Patch(0, 7, 0, FmOp.Silent, FmOp.Silent, FmOp.Silent, new FmOp(Ar: 31, Dr: 22, Sl: 4, Sr: 22, Rr: 9, Ssg: 8 | shape))
                .Note(0, 4, 440)
                .Key(0)
                .Seconds(0.06)
                .KeyOff(0)
                .Seconds(0.015);
        }

        return script;
    }

    private static Ym3438Script Ch3Special() =>
        new Ym3438Script(
                "ch3-special",
                "CH3 special mode: each operator of channel 3 at its own frequency, a chord, then back to normal mode and\n" +
                "keyed again (so the four operators start in phase).")
            .Patch(2, 7, 0, Carrier with { Tl = 12 }, Carrier with { Tl = 12 }, Carrier with { Tl = 12 }, Carrier with { Tl = 12 })
            .System(0x27, 0x40)
            .Write(0, 0xad, (4 << 3) | 0x04)
            .Write(0, 0xa9, 0x3b)
            .Write(0, 0xae, (4 << 3) | 0x05)
            .Write(0, 0xaa, 0x50)
            .Write(0, 0xac, (4 << 3) | 0x06)
            .Write(0, 0xa8, 0x5a)
            .Note(2, 4, 440)
            .Key(2)
            .Seconds(0.2)
            .System(0x27, 0x00)
            .KeyOff(2)
            .Wait(1)
            .Key(2)
            .Seconds(0.1)
            .KeyOff(2)
            .Seconds(0.05);

    private static Ym3438Script Dac()
    {
        Ym3438Script script = new(
            "dac",
            "The DAC on channel 6: a 9-bit sawtooth written every other sample (bit 0 through $2C), panned left, mixed with an FM\n" +
            "note on channel 1; then the DAC off, so channel 6's own FM note sounds.");
        script.Patch(0, 7, 0, FmOp.Silent, FmOp.Silent, FmOp.Silent, Carrier with { Tl = 6 })
            .Note(0, 4, 330)
            .Patch(5, 7, 0, FmOp.Silent, FmOp.Silent, FmOp.Silent, Carrier with { Tl = 6 })
            .Note(5, 4, 495)
            .Key(0)
            .Key(5)
            .System(0x2b, 0x80)
            .Pan(5, true, false);
        for (int i = 0; i < 1500; i++)
        {
            int value = (i * 7) & 0x1ff;
            script.System(0x2a, value >> 1).System(0x2c, (value & 1) << 3).Wait(2);
        }

        script.Pan(5, true, true)
            .System(0x2a, 0x40)
            .Seconds(0.02)
            .System(0x2b, 0x00)
            .Seconds(0.1)
            .KeyOff(0)
            .KeyOff(5)
            .Seconds(0.03);
        return script;
    }

    private static Ym3438Script SixChannels()
    {
        Ym3438Script script = new(
            "six-channels",
            "All six channels at once, every operator a full-level carrier (algorithm 7), so each channel clips at the 9-bit\n" +
            "limit; panned left, right, both and none.");
        (bool Left, bool Right)[] pans = [(true, true), (true, false), (false, true), (true, true), (false, false), (true, true)];
        double[] notes = [110, 165, 220, 275, 330, 440];
        for (int channel = 0; channel < 6; channel++)
        {
            script.Patch(channel, 7, 3, Carrier, Carrier with { Mul = 2 }, Carrier with { Mul = 3 }, Carrier with { Mul = 4 })
                .Pan(channel, pans[channel].Left, pans[channel].Right)
                .Note(channel, 4, notes[channel])
                .Key(channel);
        }

        return script.Seconds(0.2)
            .KeyOff(0).KeyOff(1).KeyOff(2).KeyOff(3).KeyOff(4).KeyOff(5)
            .Seconds(0.05);
    }

    private static Ym3438Script WriteQuirks() =>
        new Ym3438Script(
            "write-quirks",
            "The latched F-number (the high byte waits for the low one), changes while a note sounds (level, feedback,\n" +
            "algorithm), a key on without a key off, keying single operators, and key-on writes to the missing channel 3.")
            .Patch(1, 0, 3, Modulator, Modulator with { Mul = 1 }, Modulator with { Mul = 4 }, Carrier)
            .Note(1, 4, 440)
            .Key(1)
            .Seconds(0.05)
            .Channel(1, 0xa4, (5 << 3) | 0x02)
            .Seconds(0.03)
            .Channel(1, 0xa0, 0x10)
            .Seconds(0.03)
            .Operator(1, 4, 0x40, 30)
            .Channel(1, 0xb0, (7 << 3) | 2)
            .Seconds(0.04)
            .Key(1)
            .Seconds(0.02)
            .Key(1, 0b1000)
            .Seconds(0.03)
            .Key(1, 0b0001)
            .Seconds(0.03)
            .System(0x28, 0xf3)
            .System(0x28, 0xf7)
            .Seconds(0.02)
            .KeyOff(1)
            .Seconds(0.05);
}
