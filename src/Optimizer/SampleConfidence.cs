using Mod2Xgm.Mapping;

namespace Mod2Xgm.Optimizer;

/// <summary>Why a sample's FM confidence does or doesn't decide how it plays (docs/adr/0096-fm-confidence.md).</summary>
internal enum ConfidenceReason
{
    /// <summary>It has a fitted patch and a recording: the confidence chooses between them.</summary>
    Scored,

    /// <summary><c>--require-fm</c> names it: it plays FM whatever the confidence.</summary>
    RequiredFm,

    /// <summary><c>--require-dac</c> names it: it plays its recording.</summary>
    RequiredDac,

    /// <summary><c>--require-psg</c> names it: it plays the PSG.</summary>
    RequiredPsg,

    /// <summary>Its pitch wanders, so no tuned voice plays it (<see cref="SoundMenu.HasSteadyPitch"/>).</summary>
    Unsteady,

    /// <summary>No FM patch was fitted for any of its regions.</summary>
    NoFm,

    /// <summary>It has no recording to fall back on (<c>--no-dac</c>, or none could be made).</summary>
    NoRecording,
}

/// <summary>
/// One sample's FM confidence: the chance, 0–1, that David would pick its FM patch, or not tell it apart, over its
/// recording in the song (docs/adr/0096-fm-confidence.md).
/// </summary>
/// <param name="Sample">Index into <c>Song.Samples</c>.</param>
/// <param name="Probability">The regions' chances, weighted by how much each is heard; <c>null</c> when no region has a patch.</param>
/// <param name="RegionProbability">Each region's chance, low to high; <c>null</c> for a region without a patch.</param>
/// <param name="Reason">Whether the chance decides how it plays, and why not.</param>
/// <param name="Exposure">Its share, 0–1, of the importance of every note the song plays.</param>
/// <param name="BentShare">The share, 0–1, of its importance in notes that slide or wobble.</param>
internal sealed record SampleConfidence(int Sample, double? Probability, IReadOnlyList<double?> RegionProbability, ConfidenceReason Reason, double Exposure, double BentShare)
{
    /// <summary>
    /// The confidence of <paramref name="sounds"/>: each region's chance from <see cref="FmConfidenceModel"/>, weighted by
    /// <paramref name="regionWeights"/> (the importance of its notes in each region); a sample the song never plays weighs
    /// its regions alike.
    /// </summary>
    /// <param name="sounds">What the sample can sound like.</param>
    /// <param name="regionWeights">The importance of its notes in each of its regions, in the order of <see cref="SampleSounds.Regions"/>.</param>
    /// <param name="exposure">Its share of the song's importance.</param>
    /// <param name="bentShare">The share of its importance in bent notes.</param>
    /// <param name="options">The <c>--require-*</c> lists and whether the DAC is used.</param>
    public static SampleConfidence Of(SampleSounds sounds, IReadOnlyList<double> regionWeights, double exposure, double bentShare, MappingOptions options)
    {
        ArgumentNullException.ThrowIfNull(sounds);
        ArgumentNullException.ThrowIfNull(regionWeights);
        ArgumentNullException.ThrowIfNull(options);
        double?[] regions = [.. sounds.Regions.Select(r => r.Features is { } f ? FmConfidenceModel.Probability(f) : (double?)null)];
        double weight = 0;
        double sum = 0;
        bool played = regionWeights.Any(w => w > 0);
        for (int r = 0; r < regions.Length; r++)
        {
            if (regions[r] is { } p)
            {
                double w = played ? (r < regionWeights.Count ? regionWeights[r] : 0) : 1;
                weight += w;
                sum += w * p;
            }
        }

        double? probability = regions.Any(p => p is not null)
            ? weight > 0 ? sum / weight : regions.Where(p => p is not null).Average()
            : null;
        return new SampleConfidence(sounds.Sample, probability, regions, ReasonOf(sounds, probability, options), exposure, bentShare);
    }

    private static ConfidenceReason ReasonOf(SampleSounds sounds, double? probability, MappingOptions options) =>
        options.RequireFm.Contains(sounds.Sample) ? ConfidenceReason.RequiredFm
        : options.RequireDac.Contains(sounds.Sample) ? ConfidenceReason.RequiredDac
        : options.RequirePsg.Contains(sounds.Sample) ? ConfidenceReason.RequiredPsg
        : !sounds.SteadyPitch ? ConfidenceReason.Unsteady
        : probability is null ? ConfidenceReason.NoFm
        : !options.UseDac || sounds.RecordingDb.Count == 0 ? ConfidenceReason.NoRecording
        : ConfidenceReason.Scored;
}
