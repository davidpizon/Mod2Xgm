namespace Mod2Xgm.Audio;

/// <summary>
/// How a sample is played at a new rate (<c>--resampler</c>, docs/cli.md). Both are band-limited to the target rate,
/// so neither adds aliasing of its own; they differ in what they treat as the source sound.
/// </summary>
internal enum ResamplingMode
{
    /// <summary>The smooth sound between the sample points, as OpenMPT plays it (windowed sinc). The default.</summary>
    Smooth,

    /// <summary>
    /// The steps an Amiga's Paula chip plays (each point held until the next, no smoothing), with the fizzy overtones
    /// that gives, up to the target rate's limit. The A500's analog filters are not modeled.
    /// </summary>
    Amiga,
}
