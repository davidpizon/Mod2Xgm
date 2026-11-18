using Mod2Xgm.Model;

namespace Mod2Xgm.Mapping;

/// <summary>
/// An FM patch built from a looped tone's own harmonics (docs/adr/0118-harmonic-patches.md): four sine carriers
/// (algorithm 7), each at the multiple of the pitch of one of the loop's four loudest harmonics and at that harmonic's
/// level; harmonics more than 40 dB below the loudest are left out. A chip waveform whose second harmonic is louder than its fundamental keeps sounding at the octave heard, where a
/// plain sine would play only its quiet fundamental.
/// </summary>
/// <param name="Harmonics">The harmonics sounded, lowest first (1 = the fundamental).</param>
/// <param name="Capture">The share of the loop's power (without its DC offset) the harmonics hold, 0–1.</param>
/// <param name="Patch">The patch, or <c>null</c> when the harmonics hold less than <see cref="MinCapture"/> of the loop.</param>
internal sealed record HarmonicPatch(IReadOnlyList<int> Harmonics, double Capture, FmPatch? Patch)
{
    /// <summary>The share of the loop's power the harmonics must hold for the patch to stand for it.</summary>
    private const double MinCapture = 0.9;

    /// <summary>Carriers in the patch: all four operators of algorithm 7.</summary>
    private const int Carriers = 4;

    /// <summary>The highest harmonic a carrier can play: the largest MUL value (docs/genesis-audio.md).</summary>
    private const int MaxMultiple = 15;

    /// <summary>The loop must be this close to a whole number of cycles (as a share of one cycle) to be measured.</summary>
    private const double CycleTolerance = 0.05;

    /// <summary>Attenuation of one TL step, in dB (docs/genesis-audio.md).</summary>
    private const double DbPerTl = 0.75;

    /// <summary>The highest TL, which mutes an operator.</summary>
    private const int MuteTl = 127;

    /// <summary>A harmonic this far below the loudest one, in dB, is left out: too quiet to hear beside it.</summary>
    private const double QuietestDb = 40;

    /// <summary>Algorithm 7: every operator is a carrier, so their sines add.</summary>
    private const int AllCarriers = 7;

    /// <summary>
    /// Measures the harmonics of <paramref name="sample"/>'s loop as a tone of <paramref name="periodFrames"/> frames per
    /// cycle; <c>null</c> when the sample doesn't loop, the loop isn't a whole number of cycles, or it is silent.
    /// </summary>
    public static HarmonicPatch? Of(Sample sample, double periodFrames)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.Loop is not { } loop || periodFrames <= 0)
        {
            return null;
        }

        int cycles = (int)Math.Round(loop.Length / periodFrames);
        if (cycles < 1 || Math.Abs(loop.Length - (cycles * periodFrames)) > CycleTolerance * periodFrames)
        {
            return null;
        }

        ReadOnlySpan<float> frames = sample.Data.AsSpan(loop.Start, loop.Length);
        double mean = 0;
        foreach (float v in frames)
        {
            mean += v;
        }

        mean /= frames.Length;
        double total = 0;
        foreach (float v in frames)
        {
            total += (v - mean) * (v - mean);
        }

        total /= frames.Length;
        if (total <= 0)
        {
            return null;
        }

        // The loop repeats exactly at its length, so harmonic k of a loop of `cycles` cycles is DFT bin k·cycles.
        int highest = Math.Min(MaxMultiple, (frames.Length - 1) / 2 / cycles);
        double[] power = new double[highest + 1];
        for (int k = 1; k <= highest; k++)
        {
            power[k] = BinPower(frames, mean, k * cycles);
        }

        double loudest = power.Max();
        double floor = loudest * Math.Pow(10, -QuietestDb / 10);
        int[] chosen = [.. Enumerable.Range(1, highest).Where(k => power[k] > 0 && power[k] >= floor).OrderByDescending(k => power[k]).Take(Carriers).Order()];
        double capture = chosen.Sum(k => power[k]) / total;
        FmPatch? patch = capture >= MinCapture && chosen.Length > 0 ? Build(chosen, power) : null;
        return new HarmonicPatch(chosen, capture, patch);
    }

    /// <summary>The power (mean square) of the sinusoid at DFT bin <paramref name="bin"/> of <paramref name="frames"/>.</summary>
    private static double BinPower(ReadOnlySpan<float> frames, double mean, int bin)
    {
        double re = 0;
        double im = 0;
        double step = 2 * Math.PI * bin / frames.Length;
        for (int n = 0; n < frames.Length; n++)
        {
            double v = frames[n] - mean;
            re += v * Math.Cos(step * n);
            im -= v * Math.Sin(step * n);
        }

        return 2 * ((re * re) + (im * im)) / ((double)frames.Length * frames.Length);
    }

    /// <summary>
    /// Four carriers at the harmonics' multiples and levels, scaled so their amplitudes add up to full scale (the most the
    /// channel swings without clipping); unused carriers are muted. A lone fundamental is the library's
    /// <see cref="FmPatchLibrary.Sine"/>.
    /// </summary>
    private static FmPatch Build(int[] harmonics, double[] power)
    {
        if (harmonics is [1])
        {
            return FmPatchLibrary.Sine;
        }

        double[] amplitude = [.. harmonics.Select(k => Math.Sqrt(power[k]))];
        double sum = amplitude.Sum();
        FmOperator[] operators = new FmOperator[Carriers];
        for (int i = 0; i < Carriers; i++)
        {
            operators[i] = i < harmonics.Length
                ? Carrier(harmonics[i], (int)Math.Clamp(Math.Round(-20 * Math.Log10(amplitude[i] / sum) / DbPerTl), 0, MuteTl))
                : Carrier(1, MuteTl);
        }

        return new FmPatch("Harmonic " + string.Join('+', harmonics), AllCarriers, 0, operators);
    }

    /// <summary>A held sine carrier at <paramref name="multiple"/> times the pitch, like the library's (a soft-start attack, release 8).</summary>
    private static FmOperator Carrier(int multiple, int tl) => new(multiple, 0, tl, 0, FmPatchLibrary.SoftAttackRate, false, 0, 0, 0, 8);
}
