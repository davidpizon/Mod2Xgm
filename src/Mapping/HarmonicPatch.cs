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
    /// A loop that isn't a whole number of cycles is measured only when its loudest harmonic band holds at least this share
    /// of it: otherwise its power lies off the cycle's harmonics, and the cycle doesn't describe it.
    /// </summary>
    private const double MinBandShare = 0.01;

    /// <summary>The level given a harmonic with no power at all, in dB: far below anything heard.</summary>
    private const float SilentDb = -200;

    /// <summary>
    /// Measures the harmonics of <paramref name="sample"/>'s loop as a tone of <paramref name="periodFrames"/> frames per
    /// cycle; <c>null</c> when the sample doesn't loop, it is silent, or its loop isn't a whole number of cycles and holds
    /// next to nothing at the cycle's harmonics. A loop that isn't a whole number of cycles (a chord, or a chorused tone) is
    /// measured by the power near each harmonic (docs/adr/0125-chord-samples.md).
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
            return Banded(sample, periodFrames);
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

        return Choose(power, total, highest);
    }

    /// <summary>
    /// The harmonics of a loop that isn't a whole number of <paramref name="periodFrames"/> cycles (a chord, or a chorused
    /// tone; docs/adr/0125-chord-samples.md): each harmonic's power is what lies within <see cref="SamplePitch.ChordCents"/>
    /// of it in the loop's spectrum; <c>null</c> when the loop is silent or its power lies off the harmonics.
    /// </summary>
    private static HarmonicPatch? Banded(Sample sample, double periodFrames)
    {
        if (LoopSpectrum.Of(sample) is not { } spectrum)
        {
            return null;
        }

        int highest = Math.Min(MaxMultiple, (int)Math.Floor(periodFrames / 2));
        double[] power = new double[highest + 1];
        for (int k = 1; k <= highest; k++)
        {
            power[k] = spectrum.Share(k / periodFrames, SamplePitch.ChordCents);
        }

        return highest < 1 || power.Max() < MinBandShare ? null : Choose(power, 1, highest);
    }

    /// <summary>The four loudest of harmonics 1 to <paramref name="highest"/>, and the patch of them when they hold enough of <paramref name="total"/>.</summary>
    private static HarmonicPatch Choose(double[] power, double total, int highest)
    {
        double loudest = power.Max();
        double floor = loudest * Math.Pow(10, -QuietestDb / 10);
        int[] chosen = [.. Enumerable.Range(1, highest).Where(k => power[k] > 0 && power[k] >= floor).OrderByDescending(k => power[k]).Take(Carriers).Order()];
        double capture = chosen.Sum(k => power[k]) / total;
        FmPatch? patch = capture >= MinCapture && chosen.Length > 0 ? Build(chosen, power) : null;
        double strong = loudest * Math.Pow(10, -SamplePitch.StrongDb / 10);
        int lowest = Enumerable.Range(1, highest).FirstOrDefault(k => power[k] > 0 && power[k] >= strong, 1);
        float[] levels = [.. Enumerable.Range(1, highest).Select(k => power[k] > 0 ? (float)(10 * Math.Log10(power[k])) : SilentDb)];
        return new HarmonicPatch(chosen, capture, patch) { Lowest = lowest, LevelsDb = levels };
    }

    /// <summary>
    /// The lowest harmonic within <see cref="SamplePitch.StrongDb"/> of the loudest: the bottom note heard. 1 for a tone; above 2 for
    /// a chord, whose common cycle sits below every note in it (docs/adr/0125-chord-samples.md).
    /// </summary>
    public int Lowest { get; init; } = 1;

    /// <summary>
    /// Each harmonic's level, in dB, from the fundamental up to the highest measured (index 0 is harmonic 1); a harmonic
    /// with no power is <see cref="SilentDb"/>. A chord's notes are read from it (docs/adr/0133-simple-fm-chords.md).
    /// </summary>
    public IReadOnlyList<float> LevelsDb { get; private init; } = [];

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
