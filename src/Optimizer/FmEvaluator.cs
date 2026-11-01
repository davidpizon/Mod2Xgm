using System.Globalization;
using System.Text;
using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;

namespace Mod2Xgm.Optimizer;

/// <summary>What an FM fit aims at: one pitch region of one sample, as the sample analysis measured it (plan T8.1).</summary>
/// <param name="NoteHz">The pitch the FM note plays at: the region's fundamental, or without one, where the mapper would put it.</param>
/// <param name="FundamentalHz">The region's fundamental, or <c>null</c> when the sample has no pitch (then only the mel bands are compared).</param>
/// <param name="Spectrum">The sample's sound in the region.</param>
internal sealed record FmTarget(double NoteHz, double? FundamentalHz, RegionSpectrum Spectrum)
{
    /// <summary>C-4, where the mapper puts an FM note for an unpitched sample played at C-2 (<c>PlanVoicing</c>'s fallback).</summary>
    private const double C4Hz = 261.625_565;

    /// <summary>ProTracker's C-2 period (docs/formats/mod.md#pitch).</summary>
    private const int C2Period = 428;

    /// <summary>The target for <paramref name="region"/>, or <c>null</c> when the region has no sound to compare.</summary>
    public static FmTarget? Of(PitchRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (region.Spectrum.MelBandsDb.Count == 0 || region.RateHz <= 0)
        {
            return null;
        }

        double hz = region.FundamentalHz ?? C4Hz * region.RateHz / (AmigaPitch.PalClockHz / C2Period);
        return new FmTarget(hz, region.FundamentalHz, region.Spectrum);
    }
}

/// <summary>How close one FM note is to its target (docs/adr/0069-fm-patch-fitting.md); every distance is 0 when identical.</summary>
/// <param name="MelDb">The log-mel distance in dB after matching the overall level: the number that picks the winner.</param>
/// <param name="MfccDistance">The MFCC distance from the same mel bands (tone colour only, as in the scorer).</param>
/// <param name="HarmonicDb">The harmonic-level distance in dB after matching the level; <c>null</c> without a pitch.</param>
internal sealed record FmScore(double MelDb, double MfccDistance, double? HarmonicDb)
{
    /// <summary>The number the searches minimize: the harmonic distance, or the mel distance without a pitch.</summary>
    public double Search => HarmonicDb ?? MelDb;
}

/// <summary>
/// Renders FM notes for one <see cref="FmTarget"/> and scores them, remembering every score so a patch is rendered only
/// once (docs/adr/0069-fm-patch-fitting.md). Two scores: <see cref="Quick"/> renders the first quarter second, for the
/// structure searches; <see cref="Judge"/> renders the whole second the sample was analyzed over.
/// </summary>
internal sealed class FmEvaluator
{
    /// <summary>Levels this far below the target's loudest are not compared: too quiet to matter next to it.</summary>
    public const double RangeDb = 60;

    /// <summary>How much of the note the quick score renders: enough for the attack and the harmonic frames after it.</summary>
    private const double QuickSeconds = 0.25;

    /// <summary>Cepstral coefficients compared (c1…c13; c0 is loudness), as the scorer does.</summary>
    private const int MfccCount = 13;

    private readonly Dictionary<string, double> _quick = [];
    private readonly Dictionary<string, double> _searched = [];
    private readonly Dictionary<string, FmScore> _judged = [];
    private readonly double[][] _mfccBasis;

    /// <summary>Scores notes against <paramref name="target"/>.</summary>
    public FmEvaluator(FmTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Target = target;
        _mfccBasis = DctBasis(SampleAnalyzer.MelBands, MfccCount);
    }

    /// <summary>The note the scores aim at.</summary>
    public FmTarget Target { get; }

    /// <summary>Notes rendered so far (scores remembered from before don't count).</summary>
    public int Renders { get; private set; }

    /// <summary>
    /// The quick score: the first quarter second's harmonics (mel bands without a pitch) against the target's first
    /// frames, in dB. <paramref name="lfoRate"/> is the LFO's rate, or <c>null</c> for off.
    /// </summary>
    public double Quick(FmPatch patch, int? lfoRate)
    {
        ArgumentNullException.ThrowIfNull(patch);
        string key = Key(patch, lfoRate);
        if (_quick.TryGetValue(key, out double known))
        {
            return known;
        }

        Renders++;
        float[] sound = FmNoteRenderer.Render(patch, Lfo(patch, lfoRate), Target.NoteHz, QuickSeconds);
        double score;
        if (Target is { FundamentalHz: { } f, Spectrum.HarmonicsDb: { } harmonics } && SampleAnalyzer.Harmonics(sound, f) is { } rendered)
        {
            score = Distance(harmonics, rendered, rendered.Length).Db;
        }
        else
        {
            RegionSpectrum spectrum = SampleAnalyzer.Spectrum(sound, null);
            score = Distance(Target.Spectrum.MelBandsDb, spectrum.MelBandsDb, spectrum.MelBandsDb.Count).Db;
        }

        _quick[key] = score;
        return score;
    }

    /// <summary>
    /// The level fit's score: the whole second's harmonics (mel bands without a pitch), in dB; the same number as
    /// <see cref="FmScore.Search"/> of <see cref="Judge"/>, without the mel bands it doesn't need.
    /// </summary>
    public double Search(FmPatch patch, int? lfoRate)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (Target.FundamentalHz is not { } f || Target.Spectrum.HarmonicsDb is not { } target)
        {
            return Judge(patch, lfoRate).Search;
        }

        string key = Key(patch, lfoRate);
        if (_judged.TryGetValue(key, out FmScore? judged))
        {
            return judged.Search;
        }

        if (_searched.TryGetValue(key, out double known))
        {
            return known;
        }

        Renders++;
        float[] sound = FmNoteRenderer.Render(patch, Lfo(patch, lfoRate), Target.NoteHz, SampleAnalyzer.RegionSeconds);
        double score = SampleAnalyzer.Harmonics(sound, f) is { } rendered
            ? Distance(target, rendered, Math.Max(target.Count, rendered.Length)).Db
            : RangeDb;
        _searched[key] = score;
        return score;
    }

    /// <summary>The full score of <paramref name="patch"/> with the LFO at <paramref name="lfoRate"/> (or off): the whole second, mel bands and harmonics.</summary>
    public FmScore Judge(FmPatch patch, int? lfoRate)
    {
        ArgumentNullException.ThrowIfNull(patch);
        string key = Key(patch, lfoRate);
        if (_judged.TryGetValue(key, out FmScore? known))
        {
            return known;
        }

        Renders++;
        float[] sound = FmNoteRenderer.Render(patch, Lfo(patch, lfoRate), Target.NoteHz, SampleAnalyzer.RegionSeconds);
        RegionSpectrum spectrum = SampleAnalyzer.Spectrum(sound, Target.FundamentalHz);
        int frames = Math.Max(Target.Spectrum.MelBandsDb.Count, spectrum.MelBandsDb.Count);
        Comparison mel = Distance(Target.Spectrum.MelBandsDb, spectrum.MelBandsDb, frames);
        double mfcc = Mfcc(Target.Spectrum.MelBandsDb, spectrum.MelBandsDb, frames, mel);
        double? harmonic = Target.Spectrum.HarmonicsDb is { } target && spectrum.HarmonicsDb is { } rendered
            ? Distance(target, rendered, Math.Max(target.Count, rendered.Count)).Db
            : null;
        FmScore score = new(mel.Db, mfcc, harmonic);
        _judged[key] = score;
        return score;
    }

    /// <summary>The LFO rate a render uses: none when the patch doesn't take the LFO, so such a patch scores once whatever rate is asked.</summary>
    public static int? Lfo(FmPatch patch, int? lfoRate)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return patch.UsesLfo ? lfoRate : null;
    }

    /// <summary>A patch and LFO rate as text: equal for equal settings (the patch's name is left out).</summary>
    internal static string Key(FmPatch patch, int? lfoRate)
    {
        StringBuilder key = new();
        key.Append(CultureInfo.InvariantCulture, $"{patch.Algorithm},{patch.Feedback},{patch.Fms},{patch.Ams},{Lfo(patch, lfoRate)?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
        foreach (FmOperator o in patch.Operators)
        {
            key.Append(CultureInfo.InvariantCulture, $"|{o.Mult},{o.Detune},{o.Tl},{o.Rs},{o.Ar},{(o.Am ? 1 : 0)},{o.Dr},{o.D2R},{o.Sl},{o.Rr},{o.SsgEg}");
        }

        return key.ToString();
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
