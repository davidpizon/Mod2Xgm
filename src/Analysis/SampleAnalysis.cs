namespace Mod2Xgm.Analysis;

/// <summary>
/// What one sample sounds like, measured once per sample for the optimizer's candidates (plan T8.1, pass 2 of
/// docs/multi-pass.md, docs/adr/0068-sample-analysis.md): three independent scores, its pitch and how steady it is, its
/// loudness over time, its loop, and its spectrum in each pitch region the song plays it in.
/// </summary>
/// <param name="Sample">Index into <c>Song.Samples</c>.</param>
/// <param name="Name">The sample's name, for reports.</param>
/// <param name="Kind">A label for logs, from the scores (<see cref="SampleKind"/>).</param>
/// <param name="Percussive">0–1: how much it hits and fades, from a fast attack and a fall in the first half second.</param>
/// <param name="Tonal">0–1: how clearly it repeats a cycle (a note), from YIN's aperiodicity, loud frames counting most.</param>
/// <param name="Noisy">0–1: how much its spectrum is flat like hiss, from the spectral flatness (white noise is 1).</param>
/// <param name="Pitch">Its pitch and steadiness, or <c>null</c> when it has no clear pitch.</param>
/// <param name="Envelope">Its loudness over time, played at the most-played region's rate.</param>
/// <param name="Loop">Its loop, or <c>null</c> when it plays once.</param>
/// <param name="Regions">The pitch regions the song plays it in, low to high; one region at C-2 when the song never plays it.</param>
internal sealed record SampleAnalysis(
    int Sample,
    string Name,
    SampleKind Kind,
    double Percussive,
    double Tonal,
    double Noisy,
    PitchStability? Pitch,
    AmplitudeEnvelope Envelope,
    LoopAnalysis? Loop,
    IReadOnlyList<PitchRegion> Regions);

/// <summary>A sample's label for logs; the optimizer uses the scores themselves.</summary>
internal enum SampleKind
{
    /// <summary>No sound at all.</summary>
    Silent,

    /// <summary>A held note: tonal, not percussive (a pad, a lead, an organ).</summary>
    Tone,

    /// <summary>A note that hits and fades: tonal and percussive (a pluck, a piano, a bass).</summary>
    PluckedTone,

    /// <summary>A hit without a clear note: percussive, not tonal (a kick, a snare, a hi-hat).</summary>
    Drum,

    /// <summary>Hiss that neither hits nor holds a note (wind, a noise sweep).</summary>
    Noise,

    /// <summary>None of the scores is high enough to say.</summary>
    Unclear,
}

/// <summary>A sample's pitch and how steady it is over time.</summary>
/// <param name="PeriodFrames">Frames per cycle: the one pitch the mapper places FM notes at (<c>Mapping.SamplePitch</c>).</param>
/// <param name="VoicedShare">0–1: the share of its sound (by energy) in frames with a clear cycle.</param>
/// <param name="DeviationCents">How far the voiced frames' pitch strays from their median, as an energy-weighted RMS, in cents.</param>
/// <param name="DriftCents">The pitch of the last voiced frame against the first, in cents (a kick's falling pitch is negative).</param>
internal sealed record PitchStability(double PeriodFrames, double VoicedShare, double DeviationCents, double DriftCents);

/// <summary>A sample's loudness over time, as RMS in windows of <see cref="HopSeconds"/>, played at <see cref="RateHz"/>.</summary>
/// <param name="RateHz">The playback rate it was measured at (the most-played region's).</param>
/// <param name="HopSeconds">Length of each window.</param>
/// <param name="LevelsDb">Each window's RMS in dB of full scale (at least <see cref="SampleAnalyzer.FloorDb"/>).</param>
/// <param name="PeakDb">The loudest window.</param>
/// <param name="AttackSeconds">From the start to the first window within 3 dB of the peak.</param>
/// <param name="DecaySeconds">From the peak until it is 20 dB quieter (a one-shot's end counts as silence), or <c>null</c> when it never gets there.</param>
/// <param name="SustainDb">A looped sample's loop level against the peak, or <c>null</c> for a one-shot.</param>
internal sealed record AmplitudeEnvelope(
    double RateHz,
    double HopSeconds,
    IReadOnlyList<double> LevelsDb,
    double PeakDb,
    double AttackSeconds,
    double? DecaySeconds,
    double? SustainDb);

/// <summary>A sample's forward loop.</summary>
/// <param name="Start">First frame.</param>
/// <param name="Length">Frames in the loop.</param>
/// <param name="LevelDb">The loop's RMS against the envelope's peak.</param>
/// <param name="Cycles">How many cycles of the sample's pitch the loop holds, or <c>null</c> without a pitch.</param>
internal sealed record LoopAnalysis(int Start, int Length, double LevelDb, double? Cycles);

/// <summary>
/// A group of the notes the song plays one sample at, at most an octave wide, and the sample's spectrum there: a
/// sample's timbre moves with how fast it is played, so FM fitting (plan T8.2) fits each region on its own.
/// </summary>
/// <param name="LowRateHz">The lowest playback rate in the region.</param>
/// <param name="HighRateHz">The highest.</param>
/// <param name="RateHz">The rate it is analyzed at: the region's median note.</param>
/// <param name="Notes">Notes the song starts in the region (0 when the song never plays the sample).</param>
/// <param name="FundamentalHz">The pitch heard at <see cref="RateHz"/>, or <c>null</c> without a pitch.</param>
/// <param name="Spectrum">The sound at <see cref="RateHz"/>, over time.</param>
internal sealed record PitchRegion(double LowRateHz, double HighRateHz, double RateHz, int Notes, double? FundamentalHz, RegionSpectrum Spectrum);

/// <summary>
/// One region's sound over time, at <see cref="SampleAnalyzer.AnalysisRateHz"/>, in frames <see cref="HopSeconds"/> apart.
/// </summary>
/// <param name="HopSeconds">Time between frames.</param>
/// <param name="MelBandsDb">Per frame, the energy in each of <see cref="SampleAnalyzer.MelBands"/> mel bands, in dB of a full-scale sine.</param>
/// <param name="HarmonicsDb">Per frame, the level of each harmonic of the fundamental, in dB of a full-scale sine, up to
/// 64 or the last below 20 kHz; <c>null</c> without a pitch.</param>
internal sealed record RegionSpectrum(double HopSeconds, IReadOnlyList<float[]> MelBandsDb, IReadOnlyList<float[]>? HarmonicsDb);
