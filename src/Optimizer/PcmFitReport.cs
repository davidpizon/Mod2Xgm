using System.Globalization;
using Serilog;

namespace Mod2Xgm.Optimizer;

/// <summary>Logs PCM candidates at Debug level (shown with <c>--verbose</c>): one line per sample and target.</summary>
internal static class PcmFitReport
{
    /// <summary>Logs every fit in <paramref name="fits"/>.</summary>
    public static void Log(ILogger logger, IReadOnlyList<PcmSampleFit> fits)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(fits);
        foreach (PcmSampleFit fit in fits)
        {
            if (fit is not { Full: { } full, Smallest: { } smallest })
            {
                continue;
            }

            logger.Debug(
                "Sample {Sample:l}, {Target:l}: {Copies} copies, {Bytes} bytes, {Db:0.00} dB; smallest {Smallest:l}; {Baked} baked notes of {Bent} bent; {Renders} copies rendered in {Seconds:0.0} s",
                Format($"{fit.Sample + 1} \"{fit.Name}\""),
                PcmTargets.Name(fit.Target),
                full.Slots,
                full.Bytes,
                full.MelDb,
                Describe(smallest),
                fit.Baked.Sum(b => b.Notes),
                fit.BentNotes,
                fit.Renders,
                fit.Elapsed.TotalSeconds);
        }
    }

    /// <summary>The room as words, for logs and the harness's CSV.</summary>
    public static string RoomName(PcmRoom room) => room switch
    {
        PcmRoom.Fits => "fits",
        PcmRoom.SampleTooOften => "sample too often",
        _ => "too many copies",
    };

    /// <summary>An option in short, e.g. "half speed+trim tail: 3 copies, 4096 bytes, 9.20 dB".</summary>
    private static string Describe(PcmOption option) =>
        Format($"{PcmTargets.Name(option.Savings)}: {option.Slots} copies, {option.Bytes} bytes, {option.MelDb:0.00} dB");

    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
