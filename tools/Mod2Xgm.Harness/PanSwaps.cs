using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Mod2Xgm.Cli;
using Mod2Xgm.Mod;
using Mod2Xgm.Model;
using Mod2Xgm.Output;
using Mod2Xgm.Render;
using Serilog;

namespace Mod2Xgm.Harness;

/// <summary>One version's pan swaps: a song converted for one format with one mapper.</summary>
/// <param name="Song">The song's file name.</param>
/// <param name="Format"><c>xgm</c> or <c>xgm2</c>.</param>
/// <param name="Mapping"><c>optimizer</c> or <c>simple</c>.</param>
/// <param name="Count">Its swaps and each instrument's summary.</param>
/// <param name="SampleNames">The song's sample names, by sample (0-based).</param>
internal sealed record PanSwapVersion(string Song, string Format, string Mapping, PanSwapCount Count, IReadOnlyList<string> SampleNames);

/// <summary>
/// The tables <c>--pan-swaps</c> writes (plan T9.4r, docs/adr/0108-pan-swap-count.md): one CSV line per swap, and a
/// report with totals per format, mapper and cause, then one line per version.
/// </summary>
internal static class PanSwapReport
{
    /// <summary>The header of <c>pan-swaps.csv</c>.</summary>
    public const string CsvHeader = "song,format,mapping,sample,name,seconds,original,from,to,kind,distance,cause,lasts_s,gap_s,volume,channel_changed";

    /// <summary>The formats every song is converted for: the two whose recordings share one DAC pan (David).</summary>
    public static readonly string[] Formats = ["xgm", "xgm2"];

    /// <summary>The mappers every song is converted with (David: both, to compare).</summary>
    public static readonly string[] Mappings = ["optimizer", "simple"];

    /// <summary><c>pan-swaps.csv</c>: one line per swap, versions in the order given.</summary>
    public static string Csv(IEnumerable<PanSwapVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        StringBuilder csv = new StringBuilder(CsvHeader).Append('\n');
        foreach (PanSwapVersion v in versions)
        {
            foreach (PanSwap s in v.Count.Swaps)
            {
                csv.Append(CultureInfo.InvariantCulture, $"{v.Song},{v.Format},{v.Mapping},{s.Sample + 1},{Quote(Name(v, s.Sample))},{N(s.Seconds)},{Side(s.Original)},{Side(s.From)},{Side(s.To)},{Kind(s.Kind)},{s.Distance},{Cause(s.Cause)},{N(s.LastsSeconds)},{N(s.GapSeconds)},{s.Volume.ToString("0.###", CultureInfo.InvariantCulture)},{(s.ChannelChanged ? "yes" : "no")}\n");
            }
        }

        return csv.ToString();
    }

    /// <summary>The report: totals per format and mapper, by cause, then one line per version.</summary>
    public static string Markdown(IReadOnlyList<PanSwapVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        StringBuilder md = new();
        md.Append("# Pan swaps the MOD doesn't make\n\n")
            .Append("Plan T9.4r, [ADR 0108](../../docs/adr/0108-pan-swap-count.md). A swap: an instrument changes the side it is heard on while its MOD side stays the same. ")
            .Append("Every line of `pan-swaps.csv` is one.\n\n")
            .Append("## Totals\n\n")
            .Append("| Format | Mapper | Swaps | Shared DAC | Pre-mix | Centred bass | PSG | Songs with swaps | Instruments with swaps | Steadily off side |\n")
            .Append("|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (IGrouping<(string Format, string Mapping), PanSwapVersion> group in versions.GroupBy(v => (v.Format, v.Mapping)))
        {
            PanSwap[] swaps = [.. group.SelectMany(v => v.Count.Swaps)];
            md.Append(CultureInfo.InvariantCulture, $"| {group.Key.Format} | {group.Key.Mapping} | {swaps.Length} | {Of(PanCause.SharedDac)} | {Of(PanCause.PreMix)} | {Of(PanCause.CentredBass)} | {Of(PanCause.Psg)} | ")
                .Append(CultureInfo.InvariantCulture, $"{group.Count(v => v.Count.Swaps.Count > 0)} of {group.Count()} | {group.Sum(v => v.Count.Samples.Count(s => s.Swaps > 0))} | {group.Sum(v => v.Count.Samples.Count(s => s.SteadilyOffSide))} |\n");

            int Of(PanCause cause) => swaps.Count(s => s.Cause == cause);
        }

        md.Append("\n## Versions\n\n")
            .Append("Longest away: the longest an instrument stays off its side after a swap away from it. Loudest away: the highest mean MOD volume over such a stretch.\n\n")
            .Append("| Song | Format | Mapper | Swaps | Instruments with swaps | Longest away (s) | Loudest away | Steadily off side |\n")
            .Append("|---|---|---|---|---|---|---|---|\n");
        foreach (PanSwapVersion v in versions)
        {
            PanSwap[] away = [.. v.Count.Swaps.Where(s => s.Kind == PanSwapKind.Away)];
            string steady = string.Join(", ", v.Count.Samples.Where(s => s.SteadilyOffSide).Select(s => (s.Sample + 1).ToString(CultureInfo.InvariantCulture)));
            md.Append(CultureInfo.InvariantCulture, $"| {v.Song} | {v.Format} | {v.Mapping} | {v.Count.Swaps.Count} | {v.Count.Samples.Count(s => s.Swaps > 0)} | ")
                .Append(away.Length == 0 ? "–" : N(away.Max(s => s.LastsSeconds)))
                .Append(" | ")
                .Append(away.Length == 0 ? "–" : away.Max(s => s.Volume).ToString("0.##", CultureInfo.InvariantCulture))
                .Append(" | ")
                .Append(steady.Length == 0 ? "–" : steady)
                .Append(" |\n");
        }

        return md.ToString();
    }

    /// <summary>A side as a letter: L, C (both) or R.</summary>
    public static string Side(int side) => side < 0 ? "L" : side > 0 ? "R" : "C";

    /// <summary>A cause as a word for the CSV.</summary>
    public static string Cause(PanCause cause) => cause switch
    {
        PanCause.SharedDac => "shared-dac",
        PanCause.PreMix => "pre-mix",
        PanCause.CentredBass => "centred-bass",
        PanCause.Psg => "psg",
        _ => "none",
    };

    /// <summary>A kind as a word for the CSV.</summary>
    public static string Kind(PanSwapKind kind) => kind switch
    {
        PanSwapKind.Away => "away",
        PanSwapKind.Back => "back",
        _ => "across",
    };

    private static string Name(PanSwapVersion version, int sample) =>
        sample < version.SampleNames.Count ? version.SampleNames[sample].Trim() : string.Empty;

    /// <summary>A CSV field: quoted, with quotes doubled, when it holds a comma, a quote or a line break.</summary>
    private static string Quote(string text) =>
        text.AsSpan().IndexOfAny(",\"\n\r") >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;

    private static string N(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// <c>--pan-swaps</c> (docs/harness.md#pan-swaps): converts every song for XGM and XGM2 with the optimizer and the simple
/// mapper, records the side every note plays on in the render, and writes <c>pan-swaps/pan-swaps.csv</c> and
/// <c>pan-swaps/report.md</c>.
/// </summary>
/// <param name="songs">The harness's inputs.</param>
/// <param name="outputDirectory">Where <c>pan-swaps/</c> goes.</param>
/// <param name="logger">Where each version's count is logged.</param>
internal sealed class PanSwapRun(IReadOnlyList<string> songs, string outputDirectory, ILogger logger)
{
    /// <summary>Counts the swaps of every version, <paramref name="jobs"/> songs at a time; 0, or 2 when no version rendered.</summary>
    public int Run(int jobs, IConversionProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        progress.Begin(new RunInfo(songs.Count.ToString(CultureInfo.InvariantCulture) + " songs", RunInfo.HarnessFormat, "normal", [.. songs.Select((s, i) => new PhaseInfo(i + 1, CorpusRun.PhaseId(s), Path.GetFileNameWithoutExtension(s), string.Empty))], Math.Min(jobs, songs.Count)));
        progress.SetSongSize(SongSize.Rows, 1);
        string folder = Path.Combine(outputDirectory, "pan-swaps");
        string work = Path.Combine(folder, "work");
        Directory.CreateDirectory(work);
        ConcurrentDictionary<string, IReadOnlyList<PanSwapVersion>> found = [];
        Parallel.ForEach(songs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, jobs) }, song =>
        {
            IPhase phase = progress.Start(CorpusRun.PhaseId(song));
            found[song] = Song(song, work);
            phase.Done();
        });

        PanSwapVersion[] versions = [.. songs.SelectMany(s => found.TryGetValue(s, out IReadOnlyList<PanSwapVersion>? v) ? v : [])];
        File.WriteAllText(Path.Combine(folder, "pan-swaps.csv"), PanSwapReport.Csv(versions));
        File.WriteAllText(Path.Combine(folder, "report.md"), PanSwapReport.Markdown(versions));
        logger.ForContext(new ReportRow("result", ("output", folder), ("versions", versions.Length), ("swaps", versions.Sum(v => v.Count.Swaps.Count)))).Information(
            "{Swaps} pan swaps in {Versions} versions of {Songs} songs: {Folder}",
            versions.Sum(v => v.Count.Swaps.Count),
            versions.Length,
            songs.Count,
            Path.Combine(folder, "report.md"));
        return versions.Length > 0 ? 0 : 2;
    }

    /// <summary>The song's versions, each format with each mapper; a version that fails is logged and left out.</summary>
    private List<PanSwapVersion> Song(string song, string work)
    {
        string name = Path.GetFileName(song);
        List<PanSwapVersion> versions = [];
        IReadOnlyList<string> sampleNames;
        try
        {
            sampleNames = SampleNames(song);
        }
        catch (Exception ex) when (ex is ConversionException or IOException)
        {
            logger.Warning("{Song:l}: not read ({Reason:l})", name, ex.Message);
            return versions;
        }

        foreach (string format in PanSwapReport.Formats)
        {
            foreach (string mapping in PanSwapReport.Mappings)
            {
                PanLog pans = new();
                string stem = Path.Combine(work, $"{Path.GetFileNameWithoutExtension(song)}.{format}.{mapping}");
                try
                {
                    string wav = ListeningRemake.Render(song, format, "--mapping " + mapping, stem, null, $"{name} {format} {mapping}", pans);
                    File.Delete(wav);
                }
                catch (Exception ex) when (ex is HarnessException or ConversionException or IOException)
                {
                    logger.Warning("{Song:l} {Format:l} {Mapping:l}: no render ({Reason:l})", name, format, mapping, ex.Message);
                    continue;
                }

                PanSwapCount count = PanSwaps.Count(pans);
                versions.Add(new PanSwapVersion(name, format, mapping, count, sampleNames));
                logger.Information("{Song:l} {Format:l} {Mapping:l}: {Swaps} pan swaps", name, format, mapping, count.Swaps.Count);
            }
        }

        return versions;
    }

    /// <summary>The song's sample names, as the converter reads them.</summary>
    private static string[] SampleNames(string song)
    {
        ConversionNotes silent = new(Serilog.Core.Logger.None);
        ConversionOptions options = new() { InputPath = song };
        ModModule module = ModReader.Read(File.ReadAllBytes(song), silent, options.Player);
        Song model = ModToSong.Convert(module, options.AmigaClockHz, silent);
        return [.. model.Samples.Select(s => s.Name)];
    }
}
