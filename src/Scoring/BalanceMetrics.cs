namespace Mod2Xgm.Scoring;

/// <summary>One instrument heard alone on both sides: the original's stem and the rendering's stem.</summary>
/// <param name="Id">What the caller calls the instrument (e.g. the MOD sample number); reported back as is.</param>
/// <param name="Reference">The instrument alone in the original.</param>
/// <param name="Candidate">The same instrument alone in the rendering, already aligned with the reference.</param>
internal sealed record InstrumentStem(int Id, AudioSignal Reference, AudioSignal Candidate);

/// <summary>How far one instrument's loudness is from the original's.</summary>
/// <param name="Id">The <see cref="InstrumentStem.Id"/>.</param>
/// <param name="OffsetDb">Mean level difference over its counted frames, after the overall gain; positive = too loud in the rendering.</param>
/// <param name="DistanceDb">Mean absolute level difference over its counted frames.</param>
/// <param name="Frames">Frames in which either side is audible (the ones counted).</param>
/// <param name="ReferenceLevelDb">The reference stem's mean power level over the counted frames, in dB (K-weighted).</param>
internal sealed record InstrumentBalance(int Id, double OffsetDb, double DistanceDb, int Frames, double ReferenceLevelDb);

/// <summary>How well a rendering keeps the original's loudness balance between instruments (docs/adr/0051-instrument-balance-metric.md).</summary>
/// <param name="DistanceDb">
/// The balance distance: the mean absolute level difference over every counted (instrument, frame) pair, after the overall
/// gain, in dB. 0 = every instrument at the right level at every moment. The main number.
/// </param>
/// <param name="StaticDb">Only each instrument's mean offset: the mean of |<see cref="InstrumentBalance.OffsetDb"/>|, weighted by frames.</param>
/// <param name="MixRelativeDb">Like <paramref name="DistanceDb"/>, but each level taken relative to its own mix at that moment instead of the overall gain.</param>
/// <param name="GainDb">The candidate mix's mean level minus the reference mix's, in dB: the overall gain taken out.</param>
/// <param name="Instruments">Per instrument, in the order given.</param>
internal sealed record BalanceReport(double DistanceDb, double StaticDb, double MixRelativeDb, double GainDb, IReadOnlyList<InstrumentBalance> Instruments);

/// <summary>
/// Compares the loudness of each instrument over time between an original and a rendering, from stems (each instrument
/// alone), after taking out the overall gain (docs/adr/0051-instrument-balance-metric.md). A whole-mix spectrum hardly
/// sees one instrument too loud and another too quiet; this does.
/// </summary>
internal static class BalanceMetrics
{
    /// <summary>Loudness window: ITU-R BS.1770's momentary loudness, 400 ms.</summary>
    public const double WindowSeconds = 0.4;

    /// <summary>A level every 100 ms.</summary>
    public const double HopSeconds = 0.1;

    /// <summary>A stem frame counts when it is louder than the reference mix's mean level minus this, and each side is clamped there.</summary>
    public const double AudibleBelowMixDb = 30;

    /// <summary>Level of digital silence, so the logarithm stays finite.</summary>
    public const double SilenceDb = -150;

    /// <summary>
    /// Compares every stem's level with the reference's, frame by frame, after the overall gain between the two mixes.
    /// Candidate signals are cut or padded with silence to the reference's length.
    /// </summary>
    /// <param name="referenceMix">The whole original.</param>
    /// <param name="candidateMix">The whole rendering, aligned with the reference.</param>
    /// <param name="stems">One per instrument; the same sample rate as the mixes.</param>
    public static BalanceReport Compare(AudioSignal referenceMix, AudioSignal candidateMix, IReadOnlyList<InstrumentStem> stems)
    {
        ArgumentNullException.ThrowIfNull(stems);
        SpectralMetrics.CheckSameRate(referenceMix, candidateMix);
        int length = referenceMix.Length;
        double[] referenceMixLevels = Levels(referenceMix, length);
        double[] candidateMixLevels = Levels(candidateMix, length);
        double referenceMean = MeanPowerDb(referenceMixLevels);
        double gain = MeanPowerDb(candidateMixLevels) - referenceMean;
        double floor = referenceMean - AudibleBelowMixDb;

        List<InstrumentBalance> instruments = [];
        double sum = 0;
        double relativeSum = 0;
        double staticSum = 0;
        int count = 0;
        foreach (InstrumentStem stem in stems)
        {
            SpectralMetrics.CheckSameRate(referenceMix, stem.Reference);
            SpectralMetrics.CheckSameRate(referenceMix, stem.Candidate);
            double[] reference = Levels(stem.Reference, length);
            double[] candidate = Levels(stem.Candidate, length);
            double offset = 0;
            double distance = 0;
            double power = 0;
            int frames = 0;
            for (int t = 0; t < reference.Length; t++)
            {
                double candidateLevel = candidate[t] - gain;
                if (reference[t] <= floor && candidateLevel <= floor)
                {
                    continue;
                }

                double difference = Math.Max(candidateLevel, floor) - Math.Max(reference[t], floor);
                double relative = (Math.Max(candidateLevel, floor) - Math.Max(candidateMixLevels[t] - gain, floor))
                    - (Math.Max(reference[t], floor) - Math.Max(referenceMixLevels[t], floor));
                offset += difference;
                distance += Math.Abs(difference);
                relativeSum += Math.Abs(relative);
                power += Math.Pow(10, reference[t] / 10);
                frames++;
            }

            if (frames > 0)
            {
                offset /= frames;
                sum += distance;
                staticSum += Math.Abs(offset) * frames;
                count += frames;
                distance /= frames;
            }

            instruments.Add(new InstrumentBalance(stem.Id, offset, distance, frames, frames > 0 ? Db(power / frames) : SilenceDb));
        }

        return count == 0
            ? new BalanceReport(0, 0, 0, gain, instruments)
            : new BalanceReport(sum / count, staticSum / count, relativeSum / count, gain, instruments);
    }

    /// <summary>
    /// How far the <paramref name="parts"/>' loudness, added up, is from the <paramref name="mix"/>'s: the mean absolute
    /// difference in dB between the power sum of the parts' levels and the mix's level, over the frames where the mix is
    /// audible. A check that stems are what they claim to be. It compares levels, not waveforms: a synthesizer's render
    /// need not repeat its phases from one run to the next, but its loudness does. Parts that play the same notes in
    /// unison can add up to 3 dB more than their power sum.
    /// </summary>
    /// <param name="mix">The whole song.</param>
    /// <param name="parts">Its stems; each is cut or padded with silence to the mix's length.</param>
    public static double SumErrorDb(AudioSignal mix, IEnumerable<AudioSignal> parts)
    {
        ArgumentNullException.ThrowIfNull(mix);
        ArgumentNullException.ThrowIfNull(parts);
        double[] mixLevels = Levels(mix, mix.Length);
        double[] power = new double[mixLevels.Length];
        foreach (AudioSignal part in parts)
        {
            SpectralMetrics.CheckSameRate(mix, part);
            double[] levels = Levels(part, mix.Length);
            for (int t = 0; t < power.Length; t++)
            {
                power[t] += Math.Pow(10, levels[t] / 10);
            }
        }

        double floor = MeanPowerDb(mixLevels) - AudibleBelowMixDb;
        double sum = 0;
        int count = 0;
        for (int t = 0; t < power.Length; t++)
        {
            if (mixLevels[t] > floor)
            {
                sum += Math.Abs(Math.Max(Db(power[t]), floor) - mixLevels[t]);
                count++;
            }
        }

        return count == 0 ? 0 : sum / count;
    }

    /// <summary>
    /// K-weighted power level in dB (ITU-R BS.1770, without its −0.691 dB offset: only differences are used) of each
    /// 400 ms window, every 100 ms, over the first <paramref name="length"/> samples (silence past the signal's end).
    /// </summary>
    /// <param name="signal">The audio.</param>
    /// <param name="length">How many samples to measure; the frame count follows from it.</param>
    public static double[] Levels(AudioSignal signal, int length)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        double[] weighted = KWeighted(signal, length);
        int window = Math.Max(1, (int)Math.Round(WindowSeconds * signal.SampleRate));
        int hop = Math.Max(1, (int)Math.Round(HopSeconds * signal.SampleRate));
        int frames = length <= window ? 1 : ((length - window) / hop) + 1;

        // Running sums of the squares, so each window costs two lookups.
        double[] cumulative = new double[length + 1];
        for (int i = 0; i < length; i++)
        {
            cumulative[i + 1] = cumulative[i] + (weighted[i] * weighted[i]);
        }

        double[] levels = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            int start = f * hop;
            int end = Math.Min(length, start + window);
            levels[f] = end > start ? Db((cumulative[end] - cumulative[start]) / (end - start)) : SilenceDb;
        }

        return levels;
    }

    /// <summary>
    /// The signal through BS.1770's K-weighting: a high shelf (+4 dB above about 1.7 kHz, the head's effect) and a high
    /// pass at 38 Hz, at any sample rate (the filters' analog parameters, by the audio-EQ-cookbook formulas).
    /// </summary>
    /// <param name="signal">The audio.</param>
    /// <param name="length">How many samples to return; silence past the signal's end.</param>
    public static double[] KWeighted(AudioSignal signal, int length)
    {
        ArgumentNullException.ThrowIfNull(signal);
        double[] x = new double[length];
        ReadOnlySpan<float> samples = signal.Samples;
        for (int i = 0; i < Math.Min(length, samples.Length); i++)
        {
            x[i] = samples[i];
        }

        Biquad.HighShelf(signal.SampleRate, ShelfHz, ShelfQ, ShelfGainDb).Run(x);
        Biquad.HighPass(signal.SampleRate, HighPassHz, HighPassQ).Run(x);
        return x;
    }

    // BS.1770's K-weighting filters as analog parameters (as derived in pyloudnorm), so they work at any rate.
    private const double ShelfHz = 1681.974450955533;
    private const double ShelfQ = 0.7071752369554196;
    private const double ShelfGainDb = 3.999843853973347;
    private const double HighPassHz = 38.13547087602444;
    private const double HighPassQ = 0.5003270373238773;

    /// <summary>The power average of levels in dB, as a level in dB.</summary>
    private static double MeanPowerDb(double[] levels) => Db(levels.Average(l => Math.Pow(10, l / 10)));

    private static double Db(double power) => power > 0 ? Math.Max(SilenceDb, 10 * Math.Log10(power)) : SilenceDb;

    /// <summary>A second-order IIR filter (normalized so a0 = 1), run in place.</summary>
    private readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2)
    {
        /// <summary>The audio-EQ-cookbook high shelf.</summary>
        public static Biquad HighShelf(int rate, double hertz, double q, double gainDb)
        {
            double a = Math.Pow(10, gainDb / 40);
            double w = 2 * Math.PI * hertz / rate;
            double cos = Math.Cos(w);
            double alpha = Math.Sin(w) / (2 * q);
            double root = 2 * Math.Sqrt(a) * alpha;
            double a0 = (a + 1) - ((a - 1) * cos) + root;
            return new Biquad(
                a * ((a + 1) + ((a - 1) * cos) + root) / a0,
                -2 * a * ((a - 1) + ((a + 1) * cos)) / a0,
                a * ((a + 1) + ((a - 1) * cos) - root) / a0,
                2 * ((a - 1) - ((a + 1) * cos)) / a0,
                ((a + 1) - ((a - 1) * cos) - root) / a0);
        }

        /// <summary>The audio-EQ-cookbook high pass.</summary>
        public static Biquad HighPass(int rate, double hertz, double q)
        {
            double w = 2 * Math.PI * hertz / rate;
            double cos = Math.Cos(w);
            double alpha = Math.Sin(w) / (2 * q);
            double a0 = 1 + alpha;
            return new Biquad((1 + cos) / 2 / a0, -(1 + cos) / a0, (1 + cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
        }

        /// <summary>Filters <paramref name="x"/> in place (direct form I).</summary>
        public void Run(double[] x)
        {
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < x.Length; i++)
            {
                double input = x[i];
                double output = (B0 * input) + (B1 * x1) + (B2 * x2) - (A1 * y1) - (A2 * y2);
                x2 = x1;
                x1 = input;
                y2 = y1;
                y1 = output;
                x[i] = output;
            }
        }
    }
}
