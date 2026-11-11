namespace Mod2Xgm.Scoring;

/// <summary>How far a rendering is from the original by the four perceptual features (docs/adr/0100-perceptual-features.md).</summary>
/// <param name="NoiseToMaskDb">
/// PEAQ-style total noise-to-mask ratio, in dB: how far the difference rises above what the original itself masks.
/// <see cref="PerceptualMetrics.SilenceDb"/> when nothing differs; above 0 dB the difference is likely heard.
/// </param>
/// <param name="NsimDistance">1 − NSIM on the two ERB spectrograms (as in ViSQOL): 0 when identical, up to about 2.</param>
/// <param name="ModulationDistanceDb">Mean absolute difference, in dB, of each ERB band's envelope modulation (0.5–32 Hz).</param>
/// <param name="HarmonicityDistanceDb">Mean absolute difference of the harmonics-to-noise ratio per frame, in dB.</param>
internal sealed record PerceptualReport(double NoiseToMaskDb, double NsimDistance, double ModulationDistanceDb, double HarmonicityDistanceDb);

/// <summary>
/// Perceptual features for the learned listening score (plan T9.4k1, docs/adr/0100-perceptual-features.md): measures
/// that model a part of hearing log-mel leaves out. Masking (a difference the original covers isn't heard), local
/// structure (NSIM, which ViSQOL uses), how each band's loudness moves (envelope modulation, the roughness and tremolo
/// the ear follows) and how tonal the sound is (harmonicity, so a pitched note turned to noise counts). Each is a
/// distance from the original: lower is closer. None is a ruler on its own; the learned score (T9.4k2) weighs them.
/// </summary>
internal static class PerceptualMetrics
{
    /// <summary>The level of silence, in dB relative to a full-scale sine; also what <see cref="PerceptualReport.NoiseToMaskDb"/> reads when nothing differs.</summary>
    public const double SilenceDb = -80;

    /// <summary>Frames this far below the loudest frame (of either side) are not counted: too quiet to matter.</summary>
    private const double RangeDb = 60;

    /// <summary>Analysis frame length in seconds; the FFT size is the power of two nearest to it.</summary>
    private const double FrameSeconds = 0.046;

    /// <summary>ERB bands from 50 Hz to 16 kHz: about one per ERB at 44.1 kHz.</summary>
    private const int Bands = 40;

    /// <summary>Masking spreads to lower bands at this many dB per ERB (Bark slopes of PEAQ, 1 Bark ≈ 1.6 ERB).</summary>
    private const double LowerSlopeDbPerErb = 15;

    /// <summary>Masking spreads to higher bands at this many dB per ERB.</summary>
    private const double UpperSlopeDbPerErb = 6;

    /// <summary>How far below the spread excitation the masked threshold sits, in dB (PEAQ uses 3–6 dB).</summary>
    private const double MaskOffsetDb = 5;

    /// <summary>The dynamic range the NSIM constants are set for, in dB (the floor to 0 dB).</summary>
    private const double NsimRangeDb = -SilenceDb;

    /// <summary>The spread of NSIM's 3×3 Gaussian window, in cells.</summary>
    private const double NsimSigma = 0.5;

    /// <summary>The edges of the modulation bands, in Hz: octaves from 0.5 to 32 Hz.</summary>
    private static readonly double[] ModulationEdgesHz = [0.5, 1, 2, 4, 8, 16, 32];

    /// <summary>At this sample rate or above, harmonicity works at half the rate.</summary>
    private const int DecimateAboveHz = 32_000;

    /// <summary>The highest pitch harmonicity looks for, in Hz.</summary>
    private const double MaxPitchHz = 1000;

    /// <summary>Harmonics-to-noise ratios are kept in this range, in dB: below it is noise or silence, above it a pure tone.</summary>
    private const double MinHnrDb = -10;

    /// <summary>The top of the harmonics-to-noise range, in dB.</summary>
    private const double MaxHnrDb = 30;

    /// <summary>Measures all four features of <paramref name="candidate"/> against <paramref name="reference"/>.</summary>
    /// <param name="reference">The original rendering.</param>
    /// <param name="candidate">The rendering being judged, already aligned; the same sample rate.</param>
    public static PerceptualReport Compare(AudioSignal reference, AudioSignal candidate)
    {
        SpectralMetrics.CheckSameRate(reference, candidate);
        int fftSize = FftSize(reference.SampleRate);
        int total = Math.Max(reference.Length, candidate.Length);
        ErbSpectrogram erb = new(reference.SampleRate, fftSize, fftSize / 4, Bands);
        (double[][] a, double[][] b) = erb.Power(reference.Samples, candidate.Samples, total);
        bool[] audible = Audible(a, b);
        double frameRate = (double)reference.SampleRate / erb.HopSize;
        return new PerceptualReport(
            NoiseToMaskDb(a, b, audible, erb.CentresErb),
            NsimDistance(a, b, audible),
            ModulationDistanceDb(a, b, frameRate),
            HarmonicityDistanceDb(reference, candidate));
    }

    /// <summary>The power-of-two frame length nearest to <see cref="FrameSeconds"/> at <paramref name="sampleRate"/>.</summary>
    internal static int FftSize(int sampleRate) => 1 << (int)Math.Round(Math.Log2(sampleRate * FrameSeconds));

    /// <summary>
    /// PEAQ-style total NMR: per frame and band, the difference's power (between the two magnitude spectra, since a chip
    /// rendering isn't in phase with the original) over the original's masked threshold (its excitation spread over
    /// neighbouring bands, <see cref="MaskOffsetDb"/> down); the mean over bands and counted frames, in dB.
    /// </summary>
    private static double NoiseToMaskDb(double[][] reference, double[][] candidate, bool[] audible, double[] centresErb)
    {
        int bands = centresErb.Length;
        double[,] spread = new double[bands, bands];
        double offset = Math.Pow(10, -MaskOffsetDb / 10);
        for (int k = 0; k < bands; k++)
        {
            for (int j = 0; j < bands; j++)
            {
                double d = centresErb[j] - centresErb[k];
                double db = d >= 0 ? -UpperSlopeDbPerErb * d : LowerSlopeDbPerErb * d;
                spread[k, j] = Math.Pow(10, db / 10) * offset;
            }
        }

        double floor = Math.Pow(10, SilenceDb / 10);
        double sum = 0;
        int frames = 0;
        for (int t = 0; t < reference.Length; t++)
        {
            if (!audible[t])
            {
                continue;
            }

            double frame = 0;
            for (int j = 0; j < bands; j++)
            {
                double mask = 0;
                for (int k = 0; k < bands; k++)
                {
                    mask += reference[t][k] * spread[k, j];
                }

                double noise = Math.Sqrt(candidate[t][j]) - Math.Sqrt(reference[t][j]);
                frame += noise * noise / Math.Max(mask, floor);
            }

            sum += frame / bands;
            frames++;
        }

        double ratio = frames == 0 ? 0 : sum / frames;
        return ratio <= floor ? SilenceDb : 10 * Math.Log10(ratio);
    }

    /// <summary>
    /// 1 − NSIM (Hines et al., used by ViSQOL) over the two spectrograms in dB above <see cref="SilenceDb"/>: at each
    /// counted cell, the intensity term times the structure term over a 3×3 Gaussian neighbourhood; the mean, from 1.
    /// </summary>
    private static double NsimDistance(double[][] reference, double[][] candidate, bool[] audible)
    {
        double[][] r = Levels(reference);
        double[][] c = Levels(candidate);
        double c1 = Math.Pow(0.01 * NsimRangeDb, 2);
        double c3 = Math.Pow(0.03 * NsimRangeDb, 2) / 2;
        double[] weight = [Math.Exp(-1 / (2 * NsimSigma * NsimSigma)), 1, Math.Exp(-1 / (2 * NsimSigma * NsimSigma))];
        int frames = r.Length;
        int bands = frames == 0 ? 0 : r[0].Length;
        double sum = 0;
        int count = 0;
        for (int t = 0; t < frames; t++)
        {
            if (!audible[t])
            {
                continue;
            }

            for (int b = 0; b < bands; b++)
            {
                double w = 0, mr = 0, mc = 0;
                for (int dt = -1; dt <= 1; dt++)
                {
                    for (int db = -1; db <= 1; db++)
                    {
                        int tt = t + dt, bb = b + db;
                        if (tt < 0 || tt >= frames || bb < 0 || bb >= bands)
                        {
                            continue;
                        }

                        double g = weight[dt + 1] * weight[db + 1];
                        w += g;
                        mr += g * r[tt][bb];
                        mc += g * c[tt][bb];
                    }
                }

                mr /= w;
                mc /= w;
                double vr = 0, vc = 0, cov = 0;
                for (int dt = -1; dt <= 1; dt++)
                {
                    for (int db = -1; db <= 1; db++)
                    {
                        int tt = t + dt, bb = b + db;
                        if (tt < 0 || tt >= frames || bb < 0 || bb >= bands)
                        {
                            continue;
                        }

                        double g = weight[dt + 1] * weight[db + 1] / w;
                        double xr = r[tt][bb] - mr;
                        double xc = c[tt][bb] - mc;
                        vr += g * xr * xr;
                        vc += g * xc * xc;
                        cov += g * xr * xc;
                    }
                }

                double intensity = ((2 * mr * mc) + c1) / ((mr * mr) + (mc * mc) + c1);
                double structure = (cov + c3) / ((Math.Sqrt(vr) * Math.Sqrt(vc)) + c3);
                sum += intensity * structure;
                count++;
            }
        }

        return count == 0 ? 0 : 1 - (sum / count);
    }

    /// <summary>
    /// How differently each band's loudness moves: per ERB band with sound, the modulation spectrum of its amplitude
    /// envelope over the whole signal, as power in octave bands from 0.5 to 32 Hz relative to the envelope's mean (so
    /// it measures depth, not level), in dB; the mean absolute difference over bands and octaves.
    /// </summary>
    private static double ModulationDistanceDb(double[][] reference, double[][] candidate, double frameRate)
    {
        int frames = reference.Length;
        int bands = frames == 0 ? 0 : reference[0].Length;
        int size = 2;
        while (size < frames)
        {
            size <<= 1;
        }

        double binHz = frameRate / size;
        List<(int First, int Last)> octaves = [];
        for (int i = 0; i + 1 < ModulationEdgesHz.Length && ModulationEdgesHz[i + 1] <= frameRate / 2; i++)
        {
            int first = Math.Max(1, (int)Math.Ceiling(ModulationEdgesHz[i] / binHz));
            int last = (int)Math.Ceiling(ModulationEdgesHz[i + 1] / binHz) - 1;
            if (last >= first)
            {
                octaves.Add((first, last));
            }
        }

        double[] meanPower = new double[bands];
        double loudest = 0;
        for (int b = 0; b < bands; b++)
        {
            for (int t = 0; t < frames; t++)
            {
                meanPower[b] += Math.Max(reference[t][b], candidate[t][b]);
            }

            meanPower[b] /= frames;
            loudest = Math.Max(loudest, meanPower[b]);
        }

        if (octaves.Count == 0 || loudest <= Math.Pow(10, SilenceDb / 10))
        {
            return 0;
        }

        FftPlan fft = new(size);
        double[] window = new double[frames];
        for (int t = 0; t < frames; t++)
        {
            window[t] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * (t + 0.5) / frames));
        }

        double windowSum = window.Sum();
        double threshold = loudest * Math.Pow(10, -RangeDb / 10);
        double sum = 0;
        int count = 0;
        double[] dbA = new double[octaves.Count];
        double[] dbB = new double[octaves.Count];
        double[] real = new double[size];
        double[] imag = new double[size];
        for (int b = 0; b < bands; b++)
        {
            if (meanPower[b] < threshold)
            {
                continue;
            }

            Depths(reference, b, window, windowSum, fft, real, imag, octaves, dbA);
            Depths(candidate, b, window, windowSum, fft, real, imag, octaves, dbB);
            for (int o = 0; o < octaves.Count; o++)
            {
                sum += Math.Abs(dbA[o] - dbB[o]);
                count++;
            }
        }

        return count == 0 ? 0 : sum / count;
    }

    /// <summary>
    /// How differently tonal the two are: per frame (no overlap), the harmonics-to-noise ratio from the highest peak of the
    /// normalized autocorrelation between 1 kHz and a third of the frame (Boersma's window correction), in dB within
    /// <see cref="MinHnrDb"/>…<see cref="MaxHnrDb"/>; the mean absolute difference over frames where either side has sound.
    /// Pitches up to 1 kHz need no more than a quarter of 44.1 kHz, so above <see cref="DecimateAboveHz"/> both signals are
    /// first halved in rate (pairs of samples averaged), which halves the work.
    /// </summary>
    private static double HarmonicityDistanceDb(AudioSignal reference, AudioSignal candidate)
    {
        SpectralMetrics.CheckSameRate(reference, candidate);
        bool halve = reference.SampleRate >= DecimateAboveHz;
        int rate = halve ? reference.SampleRate / 2 : reference.SampleRate;
        float[] first = halve ? Halved(reference.Samples) : reference.Samples.ToArray();
        float[] second = halve ? Halved(candidate.Samples) : candidate.Samples.ToArray();
        int frameSize = FftSize(rate);
        int total = Math.Max(first.Length, second.Length);
        int padded = frameSize * 2;
        FftPlan fft = new(padded);
        double[] window = new double[frameSize];
        for (int i = 0; i < frameSize; i++)
        {
            window[i] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / frameSize));
        }

        double[] real = new double[padded];
        double[] imag = new double[padded];
        double[] scratchA = new double[padded];
        double[] scratchB = new double[padded];
        double[] windowAcf = new double[frameSize];
        double[] unused = new double[frameSize];
        Autocorrelations(window, window, fft, real, imag, scratchA, scratchB, windowAcf, unused);
        int minLag = Math.Max(1, (int)Math.Floor(rate / MaxPitchHz));
        int maxLag = frameSize / 3;
        int frames = Math.Max(1, (total + frameSize - 1) / frameSize);
        double[] hnrA = new double[frames];
        double[] hnrB = new double[frames];
        double[] powerA = new double[frames];
        double[] powerB = new double[frames];
        double[] frameA = new double[frameSize];
        double[] frameB = new double[frameSize];
        double[] acfA = new double[frameSize];
        double[] acfB = new double[frameSize];
        for (int t = 0; t < frames; t++)
        {
            powerA[t] = Prepare(first, t * frameSize, frameA, window);
            powerB[t] = Prepare(second, t * frameSize, frameB, window);
            Autocorrelations(frameA, frameB, fft, real, imag, scratchA, scratchB, acfA, acfB);
            hnrA[t] = Hnr(powerA[t], acfA, windowAcf, minLag, maxLag);
            hnrB[t] = Hnr(powerB[t], acfB, windowAcf, minLag, maxLag);
        }

        double loudest = Math.Max(powerA.Max(), powerB.Max());
        double threshold = Math.Max(loudest * Math.Pow(10, -RangeDb / 10), Math.Pow(10, SilenceDb / 10));
        double sum = 0;
        int count = 0;
        for (int t = 0; t < frames; t++)
        {
            if (powerA[t] >= threshold || powerB[t] >= threshold)
            {
                sum += Math.Abs(hnrA[t] - hnrB[t]);
                count++;
            }
        }

        return count == 0 ? 0 : sum / count;
    }

    /// <summary>Frames where either side is within <see cref="RangeDb"/> of the loudest frame and above silence.</summary>
    private static bool[] Audible(double[][] reference, double[][] candidate)
    {
        double[] a = [.. reference.Select(f => f.Sum())];
        double[] b = [.. candidate.Select(f => f.Sum())];
        double loudest = Math.Max(a.DefaultIfEmpty(0).Max(), b.DefaultIfEmpty(0).Max());
        double threshold = Math.Max(loudest * Math.Pow(10, -RangeDb / 10), Math.Pow(10, SilenceDb / 10));
        return [.. a.Select((p, t) => p >= threshold || b[t] >= threshold)];
    }

    /// <summary>Band powers as dB above <see cref="SilenceDb"/> (0 = silence).</summary>
    private static double[][] Levels(double[][] power) =>
        [.. power.Select(f => f.Select(p => p <= 0 ? 0 : Math.Max(SilenceDb, 10 * Math.Log10(p)) - SilenceDb).ToArray())];

    /// <summary>
    /// Band <paramref name="band"/>'s modulation depth per octave, in dB: the envelope's power in the octave over its
    /// mean's, so a level change alone leaves it alone. The mean is taken out before the window, so it can't leak into
    /// the slowest octave. <see cref="SilenceDb"/> for a band with no sound at all.
    /// </summary>
    private static void Depths(double[][] power, int band, double[] window, double windowSum, FftPlan fft, double[] real, double[] imag, List<(int First, int Last)> octaves, double[] result)
    {
        Array.Clear(real);
        Array.Clear(imag);
        double mean = 0;
        foreach (double[] frame in power)
        {
            mean += Math.Sqrt(frame[band]);
        }

        mean /= power.Length;
        for (int t = 0; t < power.Length; t++)
        {
            real[t] = (Math.Sqrt(power[t][band]) - mean) * window[t];
        }

        fft.Forward(real, imag);
        double dc = mean * windowSum * mean * windowSum;
        for (int o = 0; o < octaves.Count; o++)
        {
            double energy = 0;
            for (int k = octaves[o].First; k <= octaves[o].Last; k++)
            {
                energy += (real[k] * real[k]) + (imag[k] * imag[k]);
            }

            double depth = dc <= 0 ? 0 : energy / dc;
            result[o] = depth <= 0 ? SilenceDb : Math.Max(SilenceDb, 10 * Math.Log10(depth));
        }
    }

    /// <summary>The signal at half the rate: each pair of samples averaged (a gentle low-pass before dropping every other one).</summary>
    private static float[] Halved(ReadOnlySpan<float> samples)
    {
        float[] result = new float[samples.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (samples[2 * i] + samples[(2 * i) + 1]) * 0.5f;
        }

        return result;
    }

    /// <summary>
    /// Copies one frame of <paramref name="samples"/> from <paramref name="start"/> into <paramref name="frame"/>, mean
    /// removed and windowed (Boersma, 1993), and returns its mean power before that.
    /// </summary>
    private static double Prepare(ReadOnlySpan<float> samples, int start, double[] frame, double[] window)
    {
        double mean = 0;
        double power = 0;
        for (int i = 0; i < frame.Length; i++)
        {
            int index = start + i;
            frame[i] = index < samples.Length ? samples[index] : 0;
            mean += frame[i];
            power += frame[i] * frame[i];
        }

        mean /= frame.Length;
        for (int i = 0; i < frame.Length; i++)
        {
            frame[i] = (frame[i] - mean) * window[i];
        }

        return power / frame.Length;
    }

    /// <summary>
    /// One frame's harmonics-to-noise ratio in dB from its normalized autocorrelation: the highest peak between the lags,
    /// divided by the window's own autocorrelation (Boersma, 1993). A silent frame reads <see cref="MinHnrDb"/>.
    /// </summary>
    private static double Hnr(double power, double[] acf, double[] windowAcf, int minLag, int maxLag)
    {
        if (power <= Math.Pow(10, SilenceDb / 10))
        {
            return MinHnrDb;
        }

        double best = 0;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            if (windowAcf[lag] > 0)
            {
                best = Math.Max(best, acf[lag] / windowAcf[lag]);
            }
        }

        double h = Math.Clamp(best, 1e-6, 0.999_999);
        return Math.Clamp(10 * Math.Log10(h / (1 - h)), MinHnrDb, MaxHnrDb);
    }

    /// <summary>
    /// The autocorrelations of two frames (zero-padded to the FFT's size), each divided by its value at lag 0, from two
    /// transforms in all: both frames go in as one complex signal, and both power spectra, being real and even, come
    /// back from the second transform as its real and imaginary parts.
    /// </summary>
    private static void Autocorrelations(
        double[] first,
        double[] second,
        FftPlan fft,
        double[] real,
        double[] imag,
        double[] powerA,
        double[] powerB,
        double[] acfA,
        double[] acfB)
    {
        Array.Clear(real);
        Array.Clear(imag);
        first.CopyTo(real, 0);
        second.CopyTo(imag, 0);
        fft.Forward(real, imag);
        int n = real.Length;
        ErbSpectrogram.Unpack(real, imag, powerA, powerB, 1);
        Array.Copy(powerA, real, n);
        Array.Copy(powerB, imag, n);
        fft.Forward(real, imag);
        for (int lag = 0; lag < acfA.Length; lag++)
        {
            acfA[lag] = real[0] <= 0 ? 0 : real[lag] / real[0];
            acfB[lag] = imag[0] <= 0 ? 0 : imag[lag] / imag[0];
        }
    }
}
