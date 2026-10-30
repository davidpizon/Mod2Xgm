using Mod2Xgm.Model;

namespace Mod2Xgm.Mapping;

/// <summary>
/// How loud a sample is, and how loud a Genesis voice plays at full volume, on one scale: 1 is a full-scale sample on the
/// DAC, which is also an FM carrier at TL 0 (docs/adr/0050-voice-levels.md). An FM or PSG voice plays a sample as loud as
/// the MOD does when its volume is the MOD volume × <see cref="Sample"/> ÷ the voice's level.
/// </summary>
internal static class VoiceLevel
{
    /// <summary>
    /// White noise on the PSG at volume F, as RMS on the DAC's scale: −12.5 dB, measured from Furnace's render of the
    /// <c>levels</c> probe (tests/golden/dmf-probes): the noise is −20.95 dBFS RMS where a full-scale sine on the DAC
    /// peaks at −8.4 dBFS.
    /// </summary>
    public const double PsgNoise = 0.236;

    /// <summary>Frames per window when finding a sample's loudest moment (about 30 ms at 8 kHz).</summary>
    private const int WindowFrames = 256;

    /// <summary>Attenuation of one TL step, in dB (docs/genesis-audio.md).</summary>
    private const double TlStepDb = 0.75;

    /// <summary>Attenuation of one sustain-level step, in dB; SL 15 is treated as silence.</summary>
    private const double SlStepDb = 3;

    private const int MaxSl = 15;

    /// <summary>The highest TL: the library's way of muting an operator, counted as silent.</summary>
    private const int MaxTl = 127;

    /// <summary>
    /// The sample's level, as RMS (full scale = 1): for a sample that loops, its loop, which is what a held note sounds
    /// like; for one that doesn't, its loudest window of <see cref="WindowFrames"/> frames, which is how hard it hits.
    /// </summary>
    public static double Sample(Sample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return sample.Loop is { } loop ? Rms(sample.Data.AsSpan(loop.Start, loop.Length)) : Loudest(sample);
    }

    /// <summary>The RMS of the sample's loudest window of <see cref="WindowFrames"/> frames (full scale = 1), loop or not.</summary>
    public static double Loudest(Sample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ReadOnlySpan<float> data = sample.Data.AsSpan(0, sample.Length);
        if (data.Length <= WindowFrames)
        {
            return Rms(data);
        }

        double loudest = 0;
        for (int start = 0; start < data.Length; start += WindowFrames / 2)
        {
            int length = Math.Min(WindowFrames, data.Length - start);
            loudest = Math.Max(loudest, Rms(data.Slice(start, length)));
        }

        return loudest;
    }

    /// <summary>
    /// An FM patch's level at full volume (7F). Each carrier is a sine whatever modulates it, so it adds its amplitude
    /// 10^(−0.75·TL/20) (none at TL 127) and an RMS of that over √2; the carriers sound at different pitches or phases, so
    /// their powers add.
    /// </summary>
    /// <param name="patch">The patch.</param>
    /// <param name="sustained">
    /// True for the level a held note settles at (each carrier after its decay to the sustain level), false for the level
    /// of its attack.
    /// </param>
    /// <returns>The RMS, and the peak: the carriers' amplitudes added, the most the channel can swing (above 1 it clips).</returns>
    public static (double Rms, double Peak) Fm(FmPatch patch, bool sustained)
    {
        ArgumentNullException.ThrowIfNull(patch);
        double power = 0;
        double peak = 0;
        foreach (int op in Carriers(patch.Algorithm))
        {
            FmOperator carrier = patch.Operators[op];
            double amplitude = carrier.Tl >= MaxTl ? 0 : Math.Pow(10, -TlStepDb * carrier.Tl / 20);
            peak += amplitude;
            if (sustained && carrier.Dr > 0)
            {
                amplitude = carrier.Sl >= MaxSl ? 0 : amplitude * Math.Pow(10, -SlStepDb * carrier.Sl / 20);
            }

            power += amplitude * amplitude / 2;
        }

        return (Math.Sqrt(power), peak);
    }

    /// <summary>The carriers of an algorithm, as indexes of OP1–OP4 in Yamaha order (docs/genesis-audio.md).</summary>
    public static IReadOnlyList<int> Carriers(int algorithm) => algorithm switch
    {
        < 4 => [3],
        4 => [1, 3],
        5 or 6 => [1, 2, 3],
        _ => [0, 1, 2, 3],
    };

    private static double Rms(ReadOnlySpan<float> data)
    {
        if (data.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        foreach (float value in data)
        {
            sum += (double)value * value;
        }

        return Math.Sqrt(sum / data.Length);
    }
}
