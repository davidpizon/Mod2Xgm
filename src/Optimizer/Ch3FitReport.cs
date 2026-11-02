using System.Globalization;
using Mod2Xgm.Analysis;
using Mod2Xgm.Mapping;
using Serilog;

namespace Mod2Xgm.Optimizer;

/// <summary>Describes CH3 candidates for logs and the harness (docs/adr/0073-ch3-locked-pairs.md).</summary>
internal static class Ch3FitReport
{
    /// <summary>One <c>Debug</c> line per candidate, then a line with how many there are and how often each source won.</summary>
    public static void Log(ILogger logger, Ch3SongFit fit)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(fit);
        foreach (Ch3Candidate c in fit.Candidates)
        {
            logger.Debug(
                "CH3 {Pair:l}: {Onsets} onsets, {Mel:l} dB (algorithm 4: {Algorithm4:l} dB, cut down: {CutDown:l} dB); {Settings:l}",
                Pair(c),
                c.Occurrences,
                Db(c.Score.MelDb),
                Db(c.Algorithm4Score.MelDb),
                Db(c.CutDownScore.MelDb),
                Settings(c));
        }

        logger.Debug(
            "{Pairs} locked pairs, {Candidates} CH3 candidates ({Algorithm4} closer as new algorithm-4 fits, {CutDown} as cut-down fits)",
            fit.Pairs.Count,
            fit.Candidates.Count,
            fit.Candidates.Count(c => c.Algorithm4Score.MelDb <= c.CutDownScore.MelDb),
            fit.Candidates.Count(c => c.Algorithm4Score.MelDb > c.CutDownScore.MelDb));
    }

    /// <summary>The pair and its regions, 1-based, e.g. "3.1 (ch 1) + 5.2 (ch 2)".</summary>
    private static string Pair(Ch3Candidate c)
    {
        ArgumentNullException.ThrowIfNull(c);
        LockedPair p = c.Pair;
        return Format($"{p.SampleA + 1}.{c.RegionA + 1} (ch {p.ChannelA + 1}) + {p.SampleB + 1}.{c.RegionB + 1} (ch {p.ChannelB + 1})");
    }

    /// <summary>The source's name in reports.</summary>
    public static string SourceName(Ch3VoiceSource source) => source == Ch3VoiceSource.Algorithm4 ? "algorithm4" : "cut-down";

    /// <summary>The channel's settings in a few words: which track has the feedback half, the feedback and each operator's multiple.</summary>
    public static string Settings(Ch3Candidate c)
    {
        ArgumentNullException.ThrowIfNull(c);
        FmPatch p = c.Patch;
        string mults = string.Join(' ', p.Operators.Select(o => o.Mult.ToString(CultureInfo.InvariantCulture)));
        string ssg = p.Operators.Any(o => o.SsgEg != 0) ? ", SSG-EG" : string.Empty;
        return Format($"OP1-2 {(c.AOnOps12 ? "A" : "B")} FB {p.Feedback} MULT {mults}{ssg}");
    }

    private static string Db(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
