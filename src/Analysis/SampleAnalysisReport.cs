using System.Globalization;
using Mod2Xgm.Output;
using Serilog;

namespace Mod2Xgm.Analysis;

/// <summary>
/// Logs the sample analysis at <c>Debug</c> level, so <c>--verbose</c> shows it (docs/adr/0068-sample-analysis.md): two
/// lines per sample, its scores, pitch, envelope and loop, then its pitch regions. The first line carries an
/// <c>analysis</c> <see cref="ReportRow"/> with the same facts as numbers, for <c>--progress json</c>.
/// </summary>
internal static class SampleAnalysisReport
{
    /// <summary>Logs every analysis in <paramref name="analyses"/>.</summary>
    public static void Log(ILogger logger, IReadOnlyList<SampleAnalysis> analyses)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(analyses);
        foreach (SampleAnalysis a in analyses)
        {
            logger.ForContext(Row(a)).Debug(
                "Sample {Sample:l}: {Kind:l} (percussive {Percussive:0.00}, tonal {Tonal:0.00}, noisy {Noisy:0.00}); {Pitch:l}; {Envelope:l}; {Loop:l}",
                Label(a),
                KindName(a.Kind),
                a.Percussive,
                a.Tonal,
                a.Noisy,
                PitchText(a.Pitch),
                EnvelopeText(a.Envelope),
                LoopText(a.Loop));
            logger.Debug(
                "Sample {Sample:l}: {Count} pitch regions: {Regions:l}",
                Label(a),
                a.Regions.Count,
                string.Join("; ", a.Regions.Select(RegionText)));
        }
    }

    /// <summary>The label as words, for logs and the harness's CSV.</summary>
    public static string KindName(SampleKind kind) => kind switch
    {
        SampleKind.Silent => "silent",
        SampleKind.Tone => "tone",
        SampleKind.PluckedTone => "plucked tone",
        SampleKind.Drum => "drum",
        SampleKind.Noise => "noise",
        _ => "unclear",
    };

    /// <summary>The analysis of one sample as a report row: scores, pitch, envelope, loop and the pitch regions' rates.</summary>
    private static ReportRow Row(SampleAnalysis a) => new(
        "analysis",
        ("n", a.Sample + 1),
        ("name", a.Name),
        ("kind", KindName(a.Kind)),
        ("percussive", a.Percussive),
        ("tonal", a.Tonal),
        ("noisy", a.Noisy),
        ("period_frames", a.Pitch?.PeriodFrames),
        ("voiced", a.Pitch?.VoicedShare),
        ("deviation_cents", a.Pitch?.DeviationCents),
        ("drift_cents", a.Pitch?.DriftCents),
        ("peak_db", a.Envelope.PeakDb),
        ("attack_ms", a.Envelope.AttackSeconds * 1000),
        ("decay_ms", a.Envelope.DecaySeconds * 1000),
        ("sustain_db", a.Envelope.SustainDb),
        ("loop_start", a.Loop?.Start),
        ("loop_length", a.Loop?.Length),
        ("regions", a.Regions.Count));

    private static string Label(SampleAnalysis a) => Format($"{a.Sample + 1} \"{a.Name}\"");

    private static string PitchText(PitchStability? pitch) => pitch is { } p
        ? Format($"a cycle of {p.PeriodFrames:0.0} frames, {p.VoicedShare:P0} voiced, ±{p.DeviationCents:0} cents, drifting {p.DriftCents:+0;-0;0} cents")
        : "no clear pitch";

    private static string EnvelopeText(AmplitudeEnvelope e)
    {
        string decay = e.DecaySeconds is { } d ? Format($"20 dB down after {d * 1000:0} ms") : "never 20 dB down";
        string sustain = e.SustainDb is { } s ? Format($", holds at {s:0.0} dB") : string.Empty;
        return Format($"peak {e.PeakDb:0.0} dBFS, attack {e.AttackSeconds * 1000:0} ms, {decay}{sustain} (at {e.RateHz:0} Hz)");
    }

    private static string LoopText(LoopAnalysis? loop) => loop is { } l
        ? Format($"loops {l.Length} frames from {l.Start} at {l.LevelDb:0.0} dB{(l.Cycles is { } c ? Format($" ({c:0.0} cycles)") : string.Empty)}")
        : "no loop";

    private static string RegionText(PitchRegion r)
    {
        // Rates print as whole hertz, so a region narrower than half a hertz prints as one rate.
        string range = r.HighRateHz - r.LowRateHz < 0.5 ? Format($"{r.RateHz:0} Hz") : Format($"{r.LowRateHz:0}–{r.HighRateHz:0} Hz");
        string pitch = r.FundamentalHz is { } f
            ? Format($", sounding {f:0.0} Hz with {r.Spectrum.HarmonicsDb?.FirstOrDefault()?.Length ?? 0} harmonics")
            : string.Empty;
        return Format($"{range} ({r.Notes} notes, analyzed at {r.RateHz:0} Hz{pitch})");
    }

    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
