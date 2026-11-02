using System.Globalization;
using Serilog;

namespace Mod2Xgm.Optimizer;

/// <summary>Logs pre-mix candidates at Debug level (shown with <c>--verbose</c>): one line per target, one per candidate.</summary>
internal static class PreMixReport
{
    /// <summary>Logs every fit in <paramref name="fits"/>.</summary>
    public static void Log(ILogger logger, IReadOnlyList<PreMixSongFit> fits)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(fits);
        foreach (PreMixSongFit fit in fits)
        {
            logger.Debug(
                "Pre-mix, {Target:l}: {Candidates} candidates ({Tails} with a tail, {Baked} baked) from {Overlaps} overlaps of {Notes} PCM notes; voice steals {Steals} → {After} on {Voices} voices; {Renders} rendered in {Seconds:0.0} s",
                PcmTargets.Name(fit.Target),
                fit.Candidates.Count,
                fit.Candidates.Count(c => c.End == PreMixEnd.Tail),
                fit.Candidates.Count(c => c.End == PreMixEnd.Baked),
                fit.Overlaps,
                fit.PcmNotes,
                fit.Steals,
                fit.StealsWithPreMix,
                fit.Voices,
                fit.Renders,
                fit.Elapsed.TotalSeconds);
            foreach (PreMixCandidate c in fit.Candidates)
            {
                logger.Debug(
                    "Pre-mix, {Target:l}: samples {Samples:l} (ticks {Offsets:l}) {Occurrences} times, {End:l}: {Bytes} bytes, {Db:0.00} dB{Seam:l}{Clip:l}",
                    PcmTargets.Name(fit.Target),
                    Samples(c),
                    Offsets(c),
                    c.Occurrences,
                    EndName(c.End),
                    c.Bytes,
                    c.Score.MelDb,
                    c.Seam is { } seam ? Format($", seam {seam.MelDb:0.00} dB") : string.Empty,
                    c.ClipDb > 0 ? Format($", turned down {c.ClipDb:0.0} dB") : string.Empty);
            }
        }
    }

    /// <summary>How the outlasting note goes on, as a word, for logs and the harness's CSV.</summary>
    public static string EndName(PreMixEnd end) => end switch
    {
        PreMixEnd.None => "none",
        PreMixEnd.Tail => "tail",
        _ => "baked",
    };

    /// <summary>The parts' sample numbers (1-based) joined by "+", e.g. "1+5".</summary>
    public static string Samples(PreMixCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return string.Join('+', candidate.Parts.Select(p => (p.Sample + 1).ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>The parts' start ticks from the window's start, joined by "+", e.g. "0+2".</summary>
    public static string Offsets(PreMixCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return string.Join('+', candidate.Parts.Select(p => p.OffsetTicks.ToString(CultureInfo.InvariantCulture)));
    }

    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
