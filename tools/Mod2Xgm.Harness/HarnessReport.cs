using System.Globalization;
using System.Text;
using Mod2Xgm.Scoring;

namespace Mod2Xgm.Harness;

/// <summary>
/// Writes a run's results as CSV (for tools) and markdown (for people). Nothing in them depends on the time or the
/// machine, so two runs of the same converter give the same files (plan T6.3: stable scores on reruns).
/// </summary>
internal static class HarnessReport
{
    /// <summary>Header of <c>scores.csv</c>.</summary>
    public const string ScoresHeader =
        "song,renderer,combined,mel_db,mfcc,onset_precision,onset_recall,onset_f1,onset_timing_ms,lag_ms,length_difference_s,remark";

    /// <summary>Header of <c>durations.csv</c>.</summary>
    public const string DurationsHeader =
        "song,tick_player_s,openmpt_rules_s,reference_s,difference_s,amiga_difference_s,one_tick_s,within_one_tick,first_row_off,stops_at_f00,conversion_notes,error";

    /// <summary>One line per song and renderer.</summary>
    public static string ScoresCsv(IReadOnlyList<SongResult> results) => ScoresCsv(results, c => c.Score);

    /// <summary>One line per song and renderer, for the first seconds only (<c>--clip</c>); the same columns as <see cref="ScoresCsv(IReadOnlyList{SongResult})"/>.</summary>
    public static string ClipScoresCsv(IReadOnlyList<SongResult> results) => ScoresCsv(results, c => c.Clip);

    private static string ScoresCsv(IReadOnlyList<SongResult> results, Func<CandidateScore, ScoreReport?> pick)
    {
        ArgumentNullException.ThrowIfNull(results);
        StringBuilder csv = new();
        csv.Append(ScoresHeader).Append('\n');
        foreach (SongResult song in results)
        {
            foreach (CandidateScore candidate in song.Scores)
            {
                ScoreReport? s = pick(candidate);
                csv.AppendJoin(
                    ',',
                    Csv(song.Song),
                    Csv(candidate.Renderer),
                    Number(s?.Combined),
                    Number(s?.MelDistanceDb),
                    Number(s?.MfccDistance),
                    Number(s?.OnsetPrecision),
                    Number(s?.OnsetRecall),
                    Number(s?.OnsetF1),
                    Number(s?.OnsetTimingErrorSeconds * 1000),
                    Number(s?.LagSeconds * 1000),
                    Number(s?.LengthDifferenceSeconds),
                    Csv(candidate.Remark ?? string.Empty));
                csv.Append('\n');
            }
        }

        return csv.ToString();
    }

    /// <summary>One line per song.</summary>
    public static string DurationsCsv(IReadOnlyList<SongResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        StringBuilder csv = new();
        csv.Append(DurationsHeader).Append('\n');
        foreach (SongResult song in results)
        {
            DurationCheck? d = song.Duration;
            csv.AppendJoin(
                ',',
                Csv(song.Song),
                Number(d?.TickPlayerSeconds),
                Number(d?.OpenMptRulesSeconds),
                Number(d?.ReferenceSeconds),
                Number(d?.DifferenceSeconds),
                Number(d?.AmigaDifferenceSeconds),
                Number(d?.ToleranceSeconds),
                d is null ? string.Empty : d.Within ? "yes" : "no",
                Position(d?.FirstRowOff),
                d is null ? string.Empty : d.StopsAtF00 ? "yes" : "no",
                song.ConversionNotes.ToString(CultureInfo.InvariantCulture),
                Csv(song.Error ?? string.Empty));
            csv.Append('\n');
        }

        return csv.ToString();
    }

    /// <summary>The run as a markdown page: scores per song, the average per renderer, and the length check.</summary>
    /// <param name="results">The songs.</param>
    /// <param name="reference">The reference player and version.</param>
    /// <param name="converter">The converter's version.</param>
    /// <param name="sampleRate">The rate everything was scored at.</param>
    public static string Markdown(IReadOnlyList<SongResult> results, string reference, string converter, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(results);
        StringBuilder md = new();
        md.Append("# Accuracy harness report\n\n");
        md.Append(CultureInfo.InvariantCulture, $"Reference: {reference}. Converter: Mod2Xgm {converter}. Scored mono at {sampleRate} Hz.\n");
        md.Append("Every number is the conversion against the reference; **lower is closer** except onset F1 (1 = every note start found).\n");
        md.Append("Metrics: docs/adr/0042-scoring-metrics.md. The combined score is the log-mel distance; it agrees only weakly with listening (docs/adr/0049-simple-mapper-baseline.md).\n\n");

        md.Append("## Scores\n\n");
        md.Append("| Song | Renderer | Combined | Log-mel dB | MFCC | Onset F1 | Timing ms | Lag ms | Length Δ s | Remark |\n");
        md.Append("|---|---|---:|---:|---:|---:|---:|---:|---:|---|\n");
        foreach (SongResult song in results)
        {
            foreach (CandidateScore candidate in song.Scores)
            {
                ScoreReport? s = candidate.Score;
                md.Append(CultureInfo.InvariantCulture, $"| `{song.Song}` | {candidate.Renderer} | {Number(s?.Combined, "0.00")} | {Number(s?.MelDistanceDb, "0.00")} | {Number(s?.MfccDistance, "0.00")} | {Number(s?.OnsetF1, "0.00")} | {Number(s?.OnsetTimingErrorSeconds * 1000, "0.0")} | {Number(s?.LagSeconds * 1000, "0")} | {Number(s?.LengthDifferenceSeconds, "0.00")} | {Cell(candidate.Remark)} |\n");
            }
        }

        if (results.SelectMany(r => r.Scores).Any(c => c.Clip is not null))
        {
            md.Append("\n## First seconds only (listening clips)\n\n");
            md.Append("The same scores for the clips in `wav/*.clip.wav` (`--clip`).\n\n");
            md.Append("| Song | Renderer | Combined | Log-mel dB | MFCC | Onset F1 |\n");
            md.Append("|---|---|---:|---:|---:|---:|\n");
            foreach (SongResult song in results)
            {
                foreach (CandidateScore candidate in song.Scores.Where(c => c.Clip is not null))
                {
                    ScoreReport? s = candidate.Clip;
                    md.Append(CultureInfo.InvariantCulture, $"| `{song.Song}` | {candidate.Renderer} | {Number(s?.Combined, "0.00")} | {Number(s?.MelDistanceDb, "0.00")} | {Number(s?.MfccDistance, "0.00")} | {Number(s?.OnsetF1, "0.00")} |\n");
                }
            }
        }

        md.Append("\n## Average per renderer\n\n");
        md.Append("| Renderer | Songs | Combined | Log-mel dB | MFCC | Onset F1 |\n");
        md.Append("|---|---:|---:|---:|---:|---:|\n");
        SortedDictionary<string, List<ScoreReport>> byRenderer = new(StringComparer.Ordinal);
        foreach (CandidateScore candidate in results.SelectMany(r => r.Scores))
        {
            if (candidate.Score is { } score)
            {
                if (!byRenderer.TryGetValue(candidate.Renderer, out List<ScoreReport>? scores))
                {
                    scores = [];
                    byRenderer.Add(candidate.Renderer, scores);
                }

                scores.Add(score);
            }
        }

        foreach ((string renderer, List<ScoreReport> scores) in byRenderer)
        {
            md.Append(CultureInfo.InvariantCulture, $"| {renderer} | {scores.Count} | {scores.Average(s => s.Combined):0.00} | {scores.Average(s => s.MelDistanceDb):0.00} | {scores.Average(s => s.MfccDistance):0.00} | {scores.Average(s => s.OnsetF1):0.00} |\n");
        }

        md.Append("\n## Song timeline: tick player against the reference (±1 tick)\n\n");
        md.Append("The tick player's ticks timed OpenMPT's way must start every row, and end the song, within one tick of the reference.\n");
        md.Append("*Amiga − OpenMPT* is how much a real Amiga's timing (the tick player's own) differs from OpenMPT's rules (docs/harness.md#song-timeline).\n\n");
        md.Append("| Song | Amiga s | OpenMPT rules s | Reference s | Δ s | Amiga − OpenMPT s | 1 tick s | Within | First row off | Conversion notes | Error |\n");
        md.Append("|---|---:|---:|---:|---:|---:|---:|---|---|---:|---|\n");
        foreach (SongResult song in results)
        {
            DurationCheck? d = song.Duration;
            string verdict = d is null ? "–" : (d.Within ? "✅" : "❌") + (d.StopsAtF00 ? " (stops at F00; end not compared)" : string.Empty);
            md.Append(CultureInfo.InvariantCulture, $"| `{song.Song}` | {Number(d?.TickPlayerSeconds, "0.000")} | {Number(d?.OpenMptRulesSeconds, "0.000")} | {Number(d?.ReferenceSeconds, "0.000")} | {Number(d?.DifferenceSeconds, "0.000")} | {Number(d?.AmigaDifferenceSeconds, "0.000")} | {Number(d?.ToleranceSeconds, "0.000")} | {verdict} | {Position(d?.FirstRowOff)} | {song.ConversionNotes} | {Cell(song.Error)} |\n");
        }

        int checkedSongs = results.Count(r => r.Duration is not null);
        int within = results.Count(r => r.Duration?.Within == true);
        md.Append(CultureInfo.InvariantCulture, $"\n{within} of {checkedSongs} songs within one tick; {results.Count(r => r.Error is not null)} songs failed.\n");
        return md.ToString();
    }

    /// <summary>A row as <c>order:row</c>, or nothing.</summary>
    private static string Position(RowTiming? row) =>
        row is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"{row.Order}:{row.Row}");

    /// <summary><paramref name="value"/> in <paramref name="format"/>, without the minus sign of a value that rounds to zero ("-0").</summary>
    private static string Number(double? value, string format = "0.######")
    {
        if (value is not { } v)
        {
            return string.Empty;
        }

        string text = v.ToString(format, CultureInfo.InvariantCulture);
        return text.StartsWith('-') && text.AsSpan(1).Trim("0.").IsEmpty ? text[1..] : text;
    }

    /// <summary>Quotes a CSV field when it needs it (RFC 4180).</summary>
    private static string Csv(string value) =>
        value.AsSpan().IndexOfAny(",\"\n\r") >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;

    /// <summary>Text that can sit in a markdown table cell.</summary>
    private static string Cell(string? value) =>
        value is null ? string.Empty : value.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");
}
