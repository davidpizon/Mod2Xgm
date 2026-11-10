using System.Globalization;
using Mod2Xgm.Mapping;
using Mod2Xgm.Output;
using Serilog;

namespace Mod2Xgm.Xgm;

/// <summary>
/// Report section 3 for XGM2 output (docs/cli.md#console-report): what the file holds, a summary and one line per stored
/// recording with the MOD sample it came from. Printed after mapping, before the file is written. The rows are the
/// DMF's <c>output</c> and <c>instrument</c> rows (docs/cli-output.md), with each recording a <c>DAC</c> instrument.
/// </summary>
internal static class Xgm2Report
{
    private const int BytesPerKilobyte = 1024;

    /// <summary>Logs section 3 at <c>Information</c>, one event per line.</summary>
    public static void Log(ILogger logger, Xgm2Output xgm2, GenesisPlan plan)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(xgm2);
        ArgumentNullException.ThrowIfNull(plan);
        IReadOnlyList<string> lines = Lines(xgm2, plan);
        (int fm, int psg) = Voices(plan);
        ReportRow.Write(logger, lines[0], new ReportRow(
            "output",
            ("format", "xgm2"),
            ("fm", fm),
            ("psg", psg),
            ("dac", xgm2.Samples.Count),
            ("pcm_bytes", PcmBytes(xgm2))));
        for (int i = 0; i < xgm2.Samples.Count; i++)
        {
            Xgm2Sample sample = xgm2.Samples[i];
            ReportRow.Write(logger, lines[i + 1], new ReportRow(
                "instrument",
                ("kind", "DAC"),
                ("n", sample.Id),
                ("from", new[] { sample.Sample + 1 }),
                ("detail", sample.HalfSpeed ? "half speed" : null),
                ("rate_hz", sample.RateHz),
                ("bytes", sample.Bytes)));
        }
    }

    /// <summary>The lines of section 3: a summary, then one line per stored recording.</summary>
    public static IReadOnlyList<string> Lines(Xgm2Output xgm2, GenesisPlan plan)
    {
        ArgumentNullException.ThrowIfNull(xgm2);
        ArgumentNullException.ThrowIfNull(plan);
        (int fm, int psg) = Voices(plan);
        string loop = xgm2.LoopFrame is { } frame ? Invariant($"loops to frame {frame}") : "no loop";
        List<string> lines =
        [
            Invariant($"Output (XGM2): {Count(fm, "FM patch", "es")}, {Count(psg, "PSG sound", "s")}, {Count(xgm2.Samples.Count, "PCM sample", "s")}, {Kilobytes(PcmBytes(xgm2))} KB PCM, {Kilobytes(xgm2.FmBytes)} KB FM and {Kilobytes(xgm2.PsgBytes)} KB PSG stream, {xgm2.Frames} frames, {loop}, {Kilobytes(xgm2.Bytes.Length)} KB file"),
        ];
        foreach (Xgm2Sample sample in xgm2.Samples)
        {
            string half = sample.HalfSpeed ? "  half speed" : string.Empty;
            lines.Add(Invariant($" DAC {sample.Id:D3}  ← {sample.Name}  {sample.RateHz,5} Hz  {sample.Bytes} B{half}"));
        }

        return lines;
    }

    /// <summary>The FM patches the plan uses, and its PSG sounds: each sample played as a tone or as noise.</summary>
    private static (int Fm, int Psg) Voices(GenesisPlan plan)
    {
        HashSet<(VoiceKind, int)> psg = [];
        foreach (PlanCell cell in plan.Patterns.SelectMany(p => p).SelectMany(r => r.Cells))
        {
            if (cell is { Voice.Kind: VoiceKind.PsgTone or VoiceKind.PsgNoise, Sample: { } sample })
            {
                psg.Add((cell.Voice.Value.Kind, sample));
            }
        }

        return (plan.FmPatches.Count, psg.Count);
    }

    private static long PcmBytes(Xgm2Output xgm2) => xgm2.Samples.Sum(s => (long)s.Bytes);

    private static string Count(int count, string noun, string plural) => Invariant($"{count} {noun}{(count == 1 ? string.Empty : plural)}");

    /// <summary>Whole kilobytes, rounded up so a small block never shows as 0 KB.</summary>
    private static long Kilobytes(long bytes) => (bytes + BytesPerKilobyte - 1) / BytesPerKilobyte;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
