using Mod2Xgm.Mapping;
using Mod2Xgm.Tests.Mod;

namespace Mod2Xgm.Tests.Dmf;

/// <summary>
/// The effect probes (plan T5.5, tests/golden/dmf-effect-probes/README.md): small MODs, each built around one group of
/// effects, converted by the real pipeline. David listens to the MOD (rendered by openmpt123) and the DMF (in DefleMask,
/// and rendered by Furnace and DefleMask) side by side; the units in docs/formats/dmf.md#effect-units are right when
/// they sound alike. The MODs are made here, so they carry no one else's copyright.
/// </summary>
internal static class DmfEffectProbes
{
    // ProTracker periods, finetune 0 (docs/formats/mod.md).
    private const int C2 = 428;
    private const int E2 = 339;
    private const int G2 = 285;
    private const int C3 = 214;

    /// <summary>Sample numbers.</summary>
    private const int Lead = 1;

    private const int Drum = 2;
    private const int Hat = 3;

    /// <summary>The lead's loop: one cycle of a sine in 32 frames, so C-2 sounds C-4 (259 Hz) and the FM patch is a sine too.</summary>
    private const int LeadCycle = 32;

    /// <summary>Every probe: its MOD and the mapping options it is converted with.</summary>
    public static IReadOnlyDictionary<string, (Func<ModBuilder> Mod, MappingOptions Options)> All { get; } =
        new Dictionary<string, (Func<ModBuilder>, MappingOptions)>
        {
            ["slide"] = (Slide, MappingOptions.Default),
            ["portamento"] = (Portamento, MappingOptions.Default),
            ["vibrato"] = (Vibrato, MappingOptions.Default),
            ["arpeggio"] = (Arpeggio, MappingOptions.Default),
            ["volume"] = (Volume, MappingOptions.Default),
            ["tremolo"] = (Tremolo, MappingOptions.Default),
            ["retrigger-cut-delay"] = (RetriggerCutDelay, MappingOptions.Default),
            ["panning"] = (Panning, MappingOptions.Default),
            ["dac"] = (Dac, MappingOptions.Default),
            ["jumps"] = (Jumps, MappingOptions.Default),
            ["noise"] = (Noise, MappingOptions.Default with { UseDac = false }),
        };

    /// <summary>White noise from a fixed seed.</summary>
    private static readonly sbyte[] HatData = [.. new Random(7).GetItems<sbyte>([-100, -50, 50, 100], 4000)];

    /// <summary>The three samples every probe has: the sine lead (FM), a decaying-tone drum (DAC), and noise.</summary>
    private static ModBuilder Song(string title) =>
        new ModBuilder { Title = title }
            .WithSample(Lead, "sine lead", LeadCycle, loopStart: 0, loopLength: LeadCycle, data: i => (sbyte)Math.Round(100 * Math.Sin(2 * Math.PI * i / LeadCycle)))
            .WithSample(Drum, "drum", 8000, data: i => (sbyte)Math.Round(110 * Math.Sin(2 * Math.PI * i / 40) * Math.Exp(-i / 1500.0)))
            .WithSample(Hat, "hat", HatData.Length, data: i => (sbyte)Math.Round(HatData[i] * Math.Exp(-i / 600.0)));

    /// <summary>Puts <paramref name="effect"/> <paramref name="parameter"/> on channel 1, rows <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static void Hold(ModBuilder mod, int from, int to, int effect, int parameter, int channel = 0)
    {
        for (int row = from; row <= to; row++)
        {
            mod.WithCell(0, row, channel, 0, 0, effect, parameter);
        }
    }

    /// <summary>Slide up (<c>104</c>) for 8 rows, back to C-2, slide down (<c>204</c>), then four fine slides up (<c>E14</c>).</summary>
    private static ModBuilder Slide()
    {
        ModBuilder mod = Song("slide").WithCell(0, 0, 0, Lead, C2);
        Hold(mod, 8, 15, 0x1, 0x04);
        mod.WithCell(0, 16, 0, Lead, C2);
        Hold(mod, 24, 31, 0x2, 0x04);
        mod.WithCell(0, 32, 0, Lead, C2);
        foreach (int row in (int[])[40, 44, 48, 52])
        {
            mod.WithCell(0, row, 0, 0, 0, 0xE, 0x14);
        }

        return mod.WithCell(0, 60, 0, 0, 0, 0xC);
    }

    /// <summary>C-2 glides up to G-2 (<c>308</c>, then <c>300</c> to keep going), fast down to C-2 (<c>310</c>), then to E-2 while fading (<c>502</c>).</summary>
    private static ModBuilder Portamento()
    {
        ModBuilder mod = Song("portamento").WithCell(0, 0, 0, Lead, C2);
        mod.WithCell(0, 16, 0, 0, G2, 0x3, 0x08);
        Hold(mod, 17, 31, 0x3, 0x00);
        mod.WithCell(0, 32, 0, 0, C2, 0x3, 0x10);
        Hold(mod, 33, 47, 0x3, 0x00);
        mod.WithCell(0, 48, 0, 0, E2, 0x5, 0x02);
        Hold(mod, 49, 59, 0x5, 0x02);
        return mod.WithCell(0, 62, 0, 0, 0, 0xC);
    }

    /// <summary>Gentle vibrato (<c>448</c>), fast and deep (<c>48C</c>), vibrato while fading (<c>602</c>), then a plain note.</summary>
    private static ModBuilder Vibrato()
    {
        ModBuilder mod = Song("vibrato").WithCell(0, 0, 0, Lead, C2, 0x4, 0x48);
        Hold(mod, 1, 15, 0x4, 0x00);
        mod.WithCell(0, 16, 0, 0, 0, 0x4, 0x8C);
        Hold(mod, 17, 31, 0x4, 0x00);
        Hold(mod, 32, 43, 0x6, 0x02);
        mod.WithCell(0, 48, 0, Lead, C2);
        return mod.WithCell(0, 60, 0, 0, 0, 0xC);
    }

    /// <summary>A major (<c>047</c>), a minor (<c>037</c>) and an octave (<c>00C</c>) arpeggio on C, then the plain note.</summary>
    private static ModBuilder Arpeggio()
    {
        ModBuilder mod = Song("arpeggio").WithCell(0, 0, 0, Lead, C2, 0x0, 0x47);
        Hold(mod, 1, 15, 0x0, 0x47);
        mod.WithCell(0, 16, 0, Lead, C2, 0x0, 0x37);
        Hold(mod, 17, 31, 0x0, 0x37);
        mod.WithCell(0, 32, 0, Lead, C2, 0x0, 0x0C);
        Hold(mod, 33, 47, 0x0, 0x0C);
        mod.WithCell(0, 48, 0, Lead, C2);
        return mod.WithCell(0, 60, 0, 0, 0, 0xC);
    }

    /// <summary>Volume steps (<c>C40</c>, <c>C20</c>, <c>C10</c>, <c>C08</c>), a slide down (<c>A02</c>) and up (<c>A20</c>), and fine slides (<c>EA4</c>, <c>EB8</c>).</summary>
    private static ModBuilder Volume()
    {
        ModBuilder mod = Song("volume").WithCell(0, 0, 0, Lead, C2, 0xC, 0x40);
        mod.WithCell(0, 8, 0, 0, 0, 0xC, 0x20);
        mod.WithCell(0, 16, 0, 0, 0, 0xC, 0x10);
        mod.WithCell(0, 24, 0, 0, 0, 0xC, 0x08);
        mod.WithCell(0, 32, 0, 0, 0, 0xC, 0x40);
        Hold(mod, 33, 44, 0xA, 0x02);
        Hold(mod, 45, 52, 0xA, 0x20);
        mod.WithCell(0, 54, 0, 0, 0, 0xE, 0xB8);
        mod.WithCell(0, 56, 0, 0, 0, 0xE, 0xB8);
        mod.WithCell(0, 58, 0, 0, 0, 0xE, 0xA4);
        return mod.WithCell(0, 62, 0, 0, 0, 0xC);
    }

    /// <summary>Slow tremolo (<c>748</c>), fast and deep (<c>78F</c>), then a plain note.</summary>
    private static ModBuilder Tremolo()
    {
        ModBuilder mod = Song("tremolo").WithCell(0, 0, 0, Lead, C2, 0xC, 0x20);
        Hold(mod, 1, 15, 0x7, 0x48);
        Hold(mod, 16, 31, 0x7, 0x8F);
        mod.WithCell(0, 40, 0, 0, 0, 0xC, 0x20);
        return mod.WithCell(0, 60, 0, 0, 0, 0xC);
    }

    /// <summary>
    /// Retrigger every 3 ticks (<c>E93</c>) on FM and on the DAC; notes cut after 2 ticks (<c>EC2</c>); a note delayed by 3
    /// ticks (<c>ED3</c>) against an undelayed one on channel 2.
    /// </summary>
    private static ModBuilder RetriggerCutDelay()
    {
        ModBuilder mod = Song("retrigger cut delay");
        for (int row = 0; row < 8; row += 2)
        {
            mod.WithCell(0, row, 0, Lead, C2, 0xE, 0x93);
        }

        for (int row = 16; row < 24; row += 2)
        {
            mod.WithCell(0, row, 0, Lead, C2, 0xE, 0xC2);
        }

        for (int row = 32; row < 40; row += 4)
        {
            mod.WithCell(0, row, 0, Lead, C2, 0xE, 0xD3);
            mod.WithCell(0, row, 1, Lead, C3);
            mod.WithCell(0, row + 2, 0, 0, 0, 0xC);
            mod.WithCell(0, row + 2, 1, 0, 0, 0xC);
        }

        mod.WithCell(0, 48, 2, Drum, C2, 0xE, 0x93);
        mod.WithCell(0, 52, 2, Drum, C2);
        return mod;
    }

    /// <summary>One note per Amiga channel: 1 (left), 2 (right), 3 (right), 4 (left), each silenced before the next.</summary>
    private static ModBuilder Panning()
    {
        ModBuilder mod = Song("panning");
        int[] periods = [C2, E2, G2, C3];
        for (int channel = 0; channel < 4; channel++)
        {
            mod.WithCell(0, channel * 16, channel, Lead, periods[channel]);
            mod.WithCell(0, (channel * 16) + 12, channel, 0, 0, 0xC);
        }

        mod.WithCell(0, 56, 1, Drum, C2);
        return mod.WithCell(0, 60, 0, Drum, C2);
    }

    /// <summary>
    /// The drum on the DAC at full, half, quarter and eighth volume (four quieter copies), then started 1024 and 2048
    /// frames in (<c>904</c>, <c>908</c>: copies cut there), then an octave up (which the DAC plays at the same pitch).
    /// </summary>
    private static ModBuilder Dac()
    {
        ModBuilder mod = Song("dac");
        int[] volumes = [0x40, 0x20, 0x10, 0x08];
        for (int i = 0; i < volumes.Length; i++)
        {
            mod.WithCell(0, i * 8, 0, Drum, C2, 0xC, volumes[i]);
        }

        mod.WithCell(0, 32, 0, Drum, C2, 0x9, 0x04);
        mod.WithCell(0, 40, 0, Drum, C2, 0x9, 0x08);
        mod.WithCell(0, 48, 0, Drum, C3);
        return mod;
    }

    /// <summary>
    /// A scale that counts its way through jumps: order 0 breaks to row 8 of order 1 (<c>D08</c>); order 1 holds a row
    /// (<c>EE3</c>), speeds up (<c>F03</c>) and jumps to order 2 (<c>B02</c>); order 2 slows the tempo (<c>F64</c>, 100 BPM)
    /// and ends, looping to the start.
    /// </summary>
    private static ModBuilder Jumps()
    {
        int[] scale = [C2, 381, E2, 320, G2, 254, 226, C3];
        ModBuilder mod = Song("jumps").WithOrders(0, 1, 2);
        for (int pattern = 0; pattern < 3; pattern++)
        {
            for (int row = 0; row < 64; row += 4)
            {
                mod.WithCell(pattern, row, 0, Lead, scale[((pattern * 16) + (row / 4)) % scale.Length]);
            }
        }

        mod.WithCell(0, 28, 1, 0, 0, 0xD, 0x08);
        mod.WithCell(1, 20, 1, 0, 0, 0xE, 0xE3);
        mod.WithCell(1, 32, 1, 0, 0, 0xF, 0x03);
        mod.WithCell(1, 48, 1, 0, 0, 0xB, 0x02);
        mod.WithCell(2, 0, 1, 0, 0, 0xF, 0x06);
        return mod.WithCell(2, 16, 1, 0, 0, 0xF, 0x64);
    }

    /// <summary>Noise hits on the PSG noise channel (converted with <c>--no-dac</c>) at full, half and quarter volume.</summary>
    private static ModBuilder Noise()
    {
        ModBuilder mod = Song("noise");
        int[] volumes = [0x40, 0x20, 0x10];
        for (int i = 0; i < 12; i++)
        {
            mod.WithCell(0, i * 4, 0, Hat, C2, 0xC, volumes[i % 3]);
        }

        return mod;
    }
}
