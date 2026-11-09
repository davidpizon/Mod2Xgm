using Mod2Xgm.Analysis;

namespace Mod2Xgm.Optimizer;

/// <summary>
/// Measures <see cref="FmRegionFeatures"/> from a region's spectrum and the spectrum of its fitted patch's render: the mel
/// frames both already have, so no FM is rendered beyond what the menu renders anyway (docs/adr/0096-fm-confidence.md).
/// </summary>
internal static class FmFeatureExtractor
{
    /// <summary>The attack compared by <see cref="FmRegionFeatures.AttackDb"/>: the first 100 ms, where a pluck or a hit is heard.</summary>
    public const double AttackSeconds = 0.1;

    /// <summary>Frames quieter than the sample's loudest by more than this are floored there, as the ruler does (<see cref="NoteScorer.RangeDb"/>).</summary>
    private const double RangeDb = NoteScorer.RangeDb;

    /// <summary>Measures how <paramref name="rendered"/> (the patch) compares with <paramref name="target"/> (the sample), with the fit's own <paramref name="score"/>.</summary>
    public static FmRegionFeatures Measure(RegionSpectrum target, RegionSpectrum rendered, NoteScore score)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(rendered);
        ArgumentNullException.ThrowIfNull(score);
        double[] sample = FrameLevelsDb(target.MelBandsDb);
        double[] patch = FrameLevelsDb(rendered.MelBandsDb);
        int frames = Math.Max(sample.Length, patch.Length);
        double peak = sample.Length == 0 ? SampleAnalyzer.FloorDb : sample.Max();
        if (frames == 0 || peak <= SampleAnalyzer.FloorDb || patch.All(db => db <= SampleAnalyzer.FloorDb))
        {
            return new FmRegionFeatures(score, RangeDb, RangeDb, 0);
        }

        double offset = TotalDb(sample, frames) - TotalDb(patch, frames);
        double floor = peak - RangeDb;
        int attack = Math.Clamp((int)Math.Round(AttackSeconds / target.HopSeconds), 1, frames);
        return new FmRegionFeatures(
            score,
            LevelDistance(sample, patch, offset, floor, attack),
            LevelDistance(sample, patch, offset, floor, frames),
            CentroidDistance(target.MelBandsDb, rendered.MelBandsDb, sample, patch, offset, floor, frames));
    }

    /// <summary>Each frame's loudness: the power of all its mel bands together, in dB.</summary>
    internal static double[] FrameLevelsDb(IReadOnlyList<float[]> bandsDb)
    {
        ArgumentNullException.ThrowIfNull(bandsDb);
        double[] levels = new double[bandsDb.Count];
        for (int t = 0; t < levels.Length; t++)
        {
            double power = 0;
            foreach (float db in bandsDb[t])
            {
                power += Math.Pow(10, db / 10.0);
            }

            levels[t] = Math.Max(SampleAnalyzer.FloorDb, 10 * Math.Log10(Math.Max(power, 1e-30)));
        }

        return levels;
    }

    /// <summary>The mean absolute difference of the first <paramref name="count"/> frames' levels, the patch moved by <paramref name="offset"/>, both floored at <paramref name="floor"/>; frames quiet in both are left out.</summary>
    private static double LevelDistance(double[] sample, double[] patch, double offset, double floor, int count)
    {
        double sum = 0;
        int frames = 0;
        for (int t = 0; t < count; t++)
        {
            double a = Math.Max(Level(sample, t), floor);
            double b = Math.Max(Level(patch, t) + offset, floor);
            if (a > floor || b > floor)
            {
                sum += Math.Abs(a - b);
                frames++;
            }
        }

        return frames == 0 ? 0 : sum / frames;
    }

    /// <summary>The mean absolute difference of the frames' brightness, in mel bands, over frames both sound in (above <paramref name="floor"/>).</summary>
    private static double CentroidDistance(IReadOnlyList<float[]> sampleBands, IReadOnlyList<float[]> patchBands, double[] sample, double[] patch, double offset, double floor, int count)
    {
        double sum = 0;
        int frames = 0;
        for (int t = 0; t < Math.Min(count, Math.Min(sampleBands.Count, patchBands.Count)); t++)
        {
            if (sample[t] > floor && patch[t] + offset > floor)
            {
                sum += Math.Abs(Centroid(sampleBands[t]) - Centroid(patchBands[t]));
                frames++;
            }
        }

        return frames == 0 ? 0 : sum / frames;
    }

    /// <summary>The power-weighted mean band of one frame.</summary>
    internal static double Centroid(float[] bandsDb)
    {
        ArgumentNullException.ThrowIfNull(bandsDb);
        double weighted = 0;
        double total = 0;
        for (int b = 0; b < bandsDb.Length; b++)
        {
            double power = Math.Pow(10, bandsDb[b] / 10.0);
            weighted += b * power;
            total += power;
        }

        return total <= 0 ? 0 : weighted / total;
    }

    /// <summary>The power of the first <paramref name="count"/> frames together, in dB; a missing frame is silence.</summary>
    private static double TotalDb(double[] levels, int count)
    {
        double power = 0;
        for (int t = 0; t < Math.Min(count, levels.Length); t++)
        {
            power += Math.Pow(10, levels[t] / 10.0);
        }

        return 10 * Math.Log10(Math.Max(power, 1e-30));
    }

    private static double Level(double[] levels, int t) => t < levels.Length ? levels[t] : SampleAnalyzer.FloorDb;
}
