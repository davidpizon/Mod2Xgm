using Mod2Xgm.Analysis;

namespace Mod2Xgm.Optimizer;

/// <summary>
/// Scores a rendered note against one <see cref="NoteTarget"/>: the ruler every candidate is measured with, FM
/// (docs/adr/0069-fm-patch-fitting.md) and PSG (docs/adr/0070-psg-candidates.md) alike, so the allocator can weigh one
/// kind against the other.
/// </summary>
internal sealed class NoteScorer
{
    /// <summary>Levels this far below the target's loudest are not compared: too quiet to matter next to it.</summary>
    public const double RangeDb = 60;

    /// <summary>Cepstral coefficients compared (c1…c13; c0 is loudness), as the scorer does.</summary>
    private const int MfccCount = 13;

    private readonly double[][] _mfccBasis;

    /// <summary>Scores notes against <paramref name="target"/>.</summary>
    public NoteScorer(NoteTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Target = target;
        _mfccBasis = DctBasis(SampleAnalyzer.MelBands, MfccCount);
    }

    /// <summary>The note the scores aim at.</summary>
    public NoteTarget Target { get; }

    /// <summary>The full score of <paramref name="sound"/> (at <see cref="SampleAnalyzer.AnalysisRateHz"/>): mel bands and harmonics.</summary>
    public NoteScore Score(float[] sound)
    {
        ArgumentNullException.ThrowIfNull(sound);
        RegionSpectrum spectrum = SampleAnalyzer.Spectrum(sound, Target.FundamentalHz);
        int frames = Math.Max(Target.Spectrum.MelBandsDb.Count, spectrum.MelBandsDb.Count);
        Comparison mel = Distance(Target.Spectrum.MelBandsDb, spectrum.MelBandsDb, frames);
        double mfcc = Mfcc(Target.Spectrum.MelBandsDb, spectrum.MelBandsDb, frames, mel);
        double? harmonic = Target.Spectrum.HarmonicsDb is { } target && spectrum.HarmonicsDb is { } rendered
            ? Distance(target, rendered, Math.Max(target.Count, rendered.Count)).Db
            : null;
        return new NoteScore(mel.Db, mfcc, harmonic);
    }

    /// <summary>
    /// The mean absolute difference, in dB, of two spectrograms over <paramref name="frames"/> frames (a missing frame is
    /// silence), after the candidate is moved to the target's overall level (by power), with both floored
    /// <see cref="RangeDb"/> below the target's peak; cells quiet in both are left out. The mapper sets each note's
    /// level (docs/adr/0050-voice-levels.md), so only the shape of the sound is judged. A silent candidate scores
    /// <see cref="RangeDb"/>.
    /// </summary>
    internal static Comparison Distance(IReadOnlyList<float[]> target, IReadOnlyList<float[]> candidate, int frames)
    {
        int width = target.Count > 0 ? target[0].Length : candidate.Count > 0 ? candidate[0].Length : 0;
        double targetPeak = Peak(target, frames);
        double candidatePeak = Peak(candidate, frames);
        double floor = targetPeak - RangeDb;
        if (width == 0 || candidatePeak <= SampleAnalyzer.FloorDb || targetPeak <= SampleAnalyzer.FloorDb)
        {
            return new Comparison(RangeDb, 0, floor);
        }

        double offset = PowerDb(target, frames) - PowerDb(candidate, frames);
        double sum = 0;
        long cells = 0;
        for (int t = 0; t < frames; t++)
        {
            for (int k = 0; k < width; k++)
            {
                double a = Math.Max(Cell(target, t, k), floor);
                double b = Math.Max(Cell(candidate, t, k) + offset, floor);
                if (a > floor || b > floor)
                {
                    sum += Math.Abs(a - b);
                    cells++;
                }
            }
        }

        return new Comparison(cells == 0 ? 0 : sum / cells, offset, floor);
    }

    /// <summary>The MFCC distance of the two mel spectrograms, after the level match and floor of <paramref name="mel"/>.</summary>
    private double Mfcc(IReadOnlyList<float[]> target, IReadOnlyList<float[]> candidate, int frames, Comparison mel)
    {
        if (frames == 0)
        {
            return 0;
        }

        double[] diff = new double[SampleAnalyzer.MelBands];
        double sum = 0;
        for (int t = 0; t < frames; t++)
        {
            for (int b = 0; b < diff.Length; b++)
            {
                diff[b] = Math.Max(Cell(target, t, b), mel.FloorDb) - Math.Max(Cell(candidate, t, b) + mel.OffsetDb, mel.FloorDb);
            }

            double squares = 0;
            foreach (double[] row in _mfccBasis)
            {
                double c = 0;
                for (int b = 0; b < diff.Length; b++)
                {
                    c += row[b] * diff[b];
                }

                squares += c * c;
            }

            sum += Math.Sqrt(squares);
        }

        return sum / frames;
    }

    private static double Cell(IReadOnlyList<float[]> frames, int t, int k) =>
        t < frames.Count && k < frames[t].Length ? frames[t][k] : SampleAnalyzer.FloorDb;

    private static double Peak(IReadOnlyList<float[]> frames, int count)
    {
        double peak = SampleAnalyzer.FloorDb;
        for (int t = 0; t < Math.Min(count, frames.Count); t++)
        {
            foreach (float v in frames[t])
            {
                peak = Math.Max(peak, v);
            }
        }

        return peak;
    }

    /// <summary>The total power of the first <paramref name="count"/> frames, in dB.</summary>
    private static double PowerDb(IReadOnlyList<float[]> frames, int count)
    {
        double power = 0;
        for (int t = 0; t < Math.Min(count, frames.Count); t++)
        {
            foreach (float v in frames[t])
            {
                power += Math.Pow(10, v / 10.0);
            }
        }

        return 10 * Math.Log10(Math.Max(power, 1e-30));
    }

    /// <summary>The orthonormal DCT-II rows for coefficients 1…<paramref name="count"/> over <paramref name="bands"/> inputs.</summary>
    private static double[][] DctBasis(int bands, int count)
    {
        double[][] basis = new double[count][];
        for (int c = 0; c < count; c++)
        {
            basis[c] = new double[bands];
            for (int n = 0; n < bands; n++)
            {
                basis[c][n] = Math.Sqrt(2.0 / bands) * Math.Cos(Math.PI * (c + 1) * (n + 0.5) / bands);
            }
        }

        return basis;
    }

    /// <summary>One spectrogram comparison.</summary>
    /// <param name="Db">The distance.</param>
    /// <param name="OffsetDb">The gain the candidate was moved by.</param>
    /// <param name="FloorDb">The level both were floored at.</param>
    internal readonly record struct Comparison(double Db, double OffsetDb, double FloorDb);
}
