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
internal sealed record NoteTarget(double NoteHz, double? FundamentalHz, RegionSpectrum Spectrum)
{
    /// <summary>C-4, where the mapper puts an FM note for an unpitched sample played at C-2 (<c>PlanVoicing</c>'s fallback).</summary>
    private const double C4Hz = 261.625_565;

    /// <summary>ProTracker's C-2 period (docs/formats/mod.md#pitch).</summary>
    private const int C2Period = 428;

    /// <summary>The target for <paramref name="region"/>, or <c>null</c> when the region has no sound to compare.</summary>
    public static NoteTarget? Of(PitchRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (region.Spectrum.MelBandsDb.Count == 0 || region.RateHz <= 0)
        {
            return null;
        }

        double hz = region.FundamentalHz ?? C4Hz * region.RateHz / (AmigaPitch.PalClockHz / C2Period);
        return new NoteTarget(hz, region.FundamentalHz, region.Spectrum);
    }
}

/// <summary>How close one FM note is to its target (docs/adr/0069-fm-patch-fitting.md); every distance is 0 when identical.</summary>
/// <param name="MelDb">The log-mel distance in dB after matching the overall level: the number that picks the winner.</param>
/// <param name="MfccDistance">The MFCC distance from the same mel bands (tone colour only, as in the scorer).</param>
/// <param name="HarmonicDb">The harmonic-level distance in dB after matching the level; <c>null</c> without a pitch.</param>
internal sealed record NoteScore(double MelDb, double MfccDistance, double? HarmonicDb)
{
    /// <summary>The number the searches minimize: the harmonic distance, or the mel distance without a pitch.</summary>
    public double Search => HarmonicDb ?? MelDb;
}

/// <summary>
/// Renders FM notes for one <see cref="NoteTarget"/> and scores them, remembering every score so a patch is rendered only
/// once (docs/adr/0069-fm-patch-fitting.md). Two scores: <see cref="Quick"/> renders the first quarter second, for the
/// structure searches; <see cref="Judge"/> renders the whole second the sample was analyzed over.
/// </summary>
internal sealed class FmEvaluator
{
    /// <summary>How much of the note the quick score renders: enough for the attack and the harmonic frames after it.</summary>
    private const double QuickSeconds = 0.25;

    private readonly Dictionary<string, double> _quick = [];
    private readonly Dictionary<string, double> _searched = [];
    private readonly Dictionary<string, NoteScore> _judged = [];
    private readonly NoteScorer _scorer;

    /// <summary>Scores notes against <paramref name="target"/>.</summary>
    public FmEvaluator(NoteTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Target = target;
        _scorer = new NoteScorer(target);
    }

    /// <summary>The note the scores aim at.</summary>
    public NoteTarget Target { get; }

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
            score = NoteScorer.Distance(harmonics, rendered, rendered.Length).Db;
        }
        else
        {
            RegionSpectrum spectrum = SampleAnalyzer.Spectrum(sound, null);
            score = NoteScorer.Distance(Target.Spectrum.MelBandsDb, spectrum.MelBandsDb, spectrum.MelBandsDb.Count).Db;
        }

        _quick[key] = score;
        return score;
    }

    /// <summary>
    /// The level fit's score: the whole second's harmonics (mel bands without a pitch), in dB; the same number as
    /// <see cref="NoteScore.Search"/> of <see cref="Judge"/>, without the mel bands it doesn't need.
    /// </summary>
    public double Search(FmPatch patch, int? lfoRate)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (Target.FundamentalHz is not { } f || Target.Spectrum.HarmonicsDb is not { } target)
        {
            return Judge(patch, lfoRate).Search;
        }

        string key = Key(patch, lfoRate);
        if (_judged.TryGetValue(key, out NoteScore? judged))
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
            ? NoteScorer.Distance(target, rendered, Math.Max(target.Count, rendered.Length)).Db
            : NoteScorer.RangeDb;
        _searched[key] = score;
        return score;
    }

    /// <summary>The full score of <paramref name="patch"/> with the LFO at <paramref name="lfoRate"/> (or off): the whole second, mel bands and harmonics.</summary>
    public NoteScore Judge(FmPatch patch, int? lfoRate)
    {
        ArgumentNullException.ThrowIfNull(patch);
        string key = Key(patch, lfoRate);
        if (_judged.TryGetValue(key, out NoteScore? known))
        {
            return known;
        }

        Renders++;
        NoteScore score = _scorer.Score(FmNoteRenderer.Render(patch, Lfo(patch, lfoRate), Target.NoteHz, SampleAnalyzer.RegionSeconds));
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
}
