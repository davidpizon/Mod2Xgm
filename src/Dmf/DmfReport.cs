using System.Globalization;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;
using Serilog;

namespace Mod2Xgm.Dmf;

/// <summary>
/// Report section 3 for DMF output (docs/cli.md#console-report): what the file will contain, each instrument and DAC
/// sample with the MOD samples it came from. Printed after mapping, before the file is written.
/// </summary>
internal static class DmfReport
{
    // Widest label: a two-digit number, a space and a quoted 22-byte name (docs/formats/mod.md#sample-header).
    private const int LabelColumnWidth = 2 + 1 + 22 + 2;

    /// <summary>The Genesis DAC plays 8-bit PCM: one byte per frame once DefleMask exports the song (docs/genesis-audio.md).</summary>
    private const int GenesisBytesPerFrame = 1;

    private const int BytesPerKilobyte = 1024;

    /// <summary>Logs section 3 at <c>Information</c>, one event per line.</summary>
    public static void Log(ILogger logger, DmfSong dmf, Song song)
    {
        ArgumentNullException.ThrowIfNull(logger);

        // The line is a property, not the template: names may hold '{' and '}', which a template would read as holes.
        foreach (string line in Lines(dmf, song))
        {
            logger.Information("{ReportLine:l}", line);
        }
    }

    /// <summary>The lines of section 3, without the level prefix: a summary, then one line per instrument and DAC sample.</summary>
    public static IReadOnlyList<string> Lines(DmfSong dmf, Song song)
    {
        ArgumentNullException.ThrowIfNull(dmf);
        ArgumentNullException.ThrowIfNull(song);

        int fm = dmf.Instruments.Count(i => i.Kind == VoiceKind.Fm);
        int noise = dmf.Instruments.Count - fm;
        long pcmBytes = dmf.File.Samples.Sum(s => (long)s.Data.Count * GenesisBytesPerFrame);
        List<string> lines =
        [
            Invariant($"Output (DMF): {Count(fm, "FM instrument")}, {Count(noise, "PSG noise instrument")}, {Count(dmf.File.Samples.Count, "DAC sample")}, {Kilobytes(pcmBytes)} KB PCM"),
        ];

        for (int i = 0; i < dmf.Instruments.Count; i++)
        {
            DmfInstrumentSource source = dmf.Instruments[i];
            string from = string.Join(", ", source.Samples.Select(s => Label(s, song)));
            string what = source.Kind == VoiceKind.Fm ? Invariant($"({dmf.File.Instruments[i].Name} patch)") : "(noise)";
            string kind = source.Kind == VoiceKind.Fm ? "FM " : "PSG";
            lines.Add(from.Length == 0 ? Invariant($" {kind} {i + 1:D2}  {what}") : Invariant($" {kind} {i + 1:D2}  ← {from} {what}"));
        }

        for (int i = 0; i < dmf.Samples.Count; i++)
        {
            DmfSampleSource source = dmf.Samples[i];
            DmfSample sample = dmf.File.Samples[i];
            string line = Invariant($" DAC {i + 1:D2}  ← {Label(source.Sample, song),-LabelColumnWidth}  {sample.RateHz,5} Hz  {sample.Data.Count * GenesisBytesPerFrame} B");
            if (source.Offset > 0)
            {
                line += Invariant($"  from frame {source.Offset}");
            }

            if (source.VolumeDb > 0)
            {
                line += Invariant($"  -{source.VolumeDb:0.#} dB");
            }

            lines.Add(line);
        }

        return lines;
    }

    /// <summary>"04 "strings"": the number trackers show (two digits, 1-based), and the quoted name.</summary>
    private static string Label(int sample, Song song) =>
        Invariant($"{sample + 1:D2} {Quoted(song.Samples[sample].Name)}");

    /// <summary>Quoted so leading and trailing spaces show; control characters become <c>·</c>, as in sections 1–2.</summary>
    private static string Quoted(string text) => "\"" + string.Concat(text.Select(c => char.IsControl(c) ? '·' : c)) + "\"";

    private static string Count(int count, string noun) => Invariant($"{count} {noun}{(count == 1 ? string.Empty : "s")}");

    /// <summary>Whole kilobytes, rounded up so a small sample never shows as 0 KB.</summary>
    private static long Kilobytes(long bytes) => (bytes + BytesPerKilobyte - 1) / BytesPerKilobyte;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
