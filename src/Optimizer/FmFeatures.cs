using Mod2Xgm.Analysis;

namespace Mod2Xgm.Optimizer;

/// <summary>
/// How a region's fitted FM patch compares with the sample there, measured several ways: what the FM confidence
/// (docs/adr/0096-fm-confidence.md) is predicted from. Lower is closer for every distance.
/// </summary>
/// <param name="Score">The fit's own score: log-mel, MFCC and harmonic distances (<see cref="NoteScorer"/>).</param>
/// <param name="AttackDb">How far the patch's loudness over the first <see cref="FmFeatureExtractor.AttackSeconds"/> is from the
/// sample's, as the mean absolute difference in dB per frame after both are matched in level: a soft FM attack on a plucked
/// sample is easy to hear.</param>
/// <param name="EnvelopeDb">The same over the whole region's analysis (<see cref="SampleAnalyzer.RegionSeconds"/>): a decay or
/// sustain FM doesn't follow.</param>
/// <param name="CentroidBands">How far the patch's brightness (the power-weighted mean mel band of each frame) is from the
/// sample's, as the mean absolute difference in mel bands over frames both sound in.</param>
internal sealed record FmRegionFeatures(NoteScore Score, double AttackDb, double EnvelopeDb, double CentroidBands);
