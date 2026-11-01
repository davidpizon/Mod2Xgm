using System.Globalization;
using Serilog;

namespace Mod2Xgm.Optimizer;

/// <summary>Logs FM fits at Debug level (shown with <c>--verbose</c>): one line per pitch region.</summary>
internal static class FmFitReport
{
    /// <summary>Logs every region of every fit in <paramref name="fits"/>.</summary>
    public static void Log(ILogger logger, IReadOnlyList<FmSampleFit> fits)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(fits);
        foreach (FmSampleFit fit in fits)
        {
            foreach (FmRegionFit r in fit.Regions)
            {
                logger.Debug(
                    "Sample {Sample:l}, region {Region}: library {Library:l} {LibraryDb:0.00} dB; exhaustive {Exhaustive:l}; genetic {Genetic:l}; best {Best:l} ({Patch:l}); {Renders} notes rendered in {Seconds:0.0} s",
                    Format($"{fit.Sample + 1} \"{fit.Name}\""),
                    r.Region + 1,
                    r.Library.Patch.Name,
                    r.Library.Score.MelDb,
                    Db(r.Exhaustive),
                    Db(r.Genetic),
                    Describe(r.Best),
                    Settings(r.Best),
                    r.Renders,
                    r.Elapsed.TotalSeconds);
            }
        }
    }

    /// <summary>A search's distance, or "not run".</summary>
    private static string Db(FmFit? fit) => fit is null ? "not run" : Format($"{fit.Score.MelDb:0.00} dB");

    private static string Describe(FmFit fit) => Format($"{SourceName(fit.Source)} {fit.Score.MelDb:0.00} dB, MFCC {fit.Score.MfccDistance:0.0}");

    /// <summary>The source as a word, for logs and the harness's CSV.</summary>
    public static string SourceName(FmFitSource source) => source switch
    {
        FmFitSource.Library => "library",
        FmFitSource.Exhaustive => "exhaustive",
        _ => "genetic",
    };

    /// <summary>The patch's structure in short: algorithm, feedback, multiples, LFO.</summary>
    public static string Settings(FmFit fit)
    {
        ArgumentNullException.ThrowIfNull(fit);
        string mults = string.Join(' ', fit.Patch.Operators.Select(o => o.Mult.ToString(CultureInfo.InvariantCulture)));
        string lfo = fit.LfoRate is { } rate ? Format($"LFO {rate} FMS {fit.Patch.Fms} AMS {fit.Patch.Ams}") : "no LFO";
        string ssg = fit.Patch.Operators.Any(o => o.SsgEg != 0) ? ", SSG-EG" : string.Empty;
        return Format($"ALG {fit.Patch.Algorithm} FB {fit.Patch.Feedback} MULT {mults}, {lfo}{ssg}");
    }

    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
