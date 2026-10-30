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

    /// <summary>Header of <c>balance.csv</c>.</summary>
    public const string BalanceHeader =
        "song,renderer,balance_db,static_db,mix_relative_db,gain_db,stems,reference_sum_error_db,candidate_sum_error_db";

    /// <summary>Header of <c>balance-instruments.csv</c>.</summary>
    public const string BalanceInstrumentsHeader =
        "song,renderer,sample,name,voices,offset_db,distance_db,frames,reference_level_db,r128_offset_lu";

    /// <summary>Header of <c>loudness.csv</c>.</summary>
    public const string LoudnessHeader =
        "song,renderer,span,reference_lufs,candidate_lufs,difference_lu,reference_lra_lu,candidate_lra_lu,reference_true_peak_dbtp,candidate_true_peak_dbtp";

    /// <summary>
    /// One line per song and renderer, and one more for the clip (<c>--clip</c>): both renderings' EBU R128 loudness,
    /// loudness range and true peak (ADR 0053). <c>span</c> is <c>song</c> or <c>clip</c>.
    /// </summary>
    public static string LoudnessCsv(IReadOnlyList<SongResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        StringBuilder csv = new();
        csv.Append(LoudnessHeader).Append('\n');
        foreach (SongResult song in results)
        {
            foreach (CandidateScore candidate in song.Scores)
            {
                foreach ((string span, LoudnessComparison? l) in new[] { ("song", candidate.Loudness), ("clip", candidate.ClipLoudness) })
                {
                    if (l is null)
                    {
                        continue;
                    }

                    csv.AppendJoin(
                        ',',
                        Csv(song.Song),
                        Csv(candidate.Renderer),
                        span,
                        Number(l.Reference.IntegratedLufs),
                        Number(l.Candidate.IntegratedLufs),
                        Number(l.DifferenceLu),
                        Number(l.Reference.RangeLu),
                        Number(l.Candidate.RangeLu),
                        Number(l.Reference.TruePeakDbtp),
                        Number(l.Candidate.TruePeakDbtp));
                    csv.Append('\n');
                }
            }
        }

        return csv.ToString();
    }

    /// <summary>One line per song and renderer with a balance (<c>--balance</c>): the clip's balance distance and its checks.</summary>
    public static string BalanceCsv(IReadOnlyList<SongResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        StringBuilder csv = new();
        csv.Append(BalanceHeader).Append('\n');
        foreach ((SongResult song, CandidateScore candidate, BalanceResult b) in Balances(results))
        {
            csv.AppendJoin(
                ',',
                Csv(song.Song),
                Csv(candidate.Renderer),
                Number(b.Report.DistanceDb),
                Number(b.Report.StaticDb),
                Number(b.Report.MixRelativeDb),
                Number(b.Report.GainDb),
                b.Stems.Count.ToString(CultureInfo.InvariantCulture),
                Number(b.ReferenceSumErrorDb),
                Number(b.CandidateSumErrorDb));
            csv.Append('\n');
        }

        return csv.ToString();
    }

    /// <summary>One line per instrument of every balance: how far its level is off (positive = too loud in the conversion).</summary>
    public static string BalanceInstrumentsCsv(IReadOnlyList<SongResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        StringBuilder csv = new();
        csv.Append(BalanceInstrumentsHeader).Append('\n');
        foreach ((SongResult song, CandidateScore candidate, BalanceResult b) in Balances(results))
        {
            for (int i = 0; i < b.Stems.Count; i++)
            {
                StemInfo stem = b.Stems[i];
                InstrumentBalance instrument = b.Report.Instruments[i];
                csv.AppendJoin(
                    ',',
                    Csv(song.Song),
                    Csv(candidate.Renderer),
                    instrument.Id.ToString(CultureInfo.InvariantCulture),
                    Csv(stem.Name),
                    Csv(stem.Voices),
                    Number(instrument.OffsetDb),
                    Number(instrument.DistanceDb),
                    instrument.Frames.ToString(CultureInfo.InvariantCulture),
                    Number(instrument.ReferenceLevelDb),
                    Number(b.R128OffsetsLu[i]));
                csv.Append('\n');
            }
        }

        return csv.ToString();
    }

    /// <summary>Every renderer's balance, song by song.</summary>
    private static IEnumerable<(SongResult Song, CandidateScore Candidate, BalanceResult Balance)> Balances(IReadOnlyList<SongResult> results)
    {
        foreach (SongResult song in results)
        {
            foreach (CandidateScore candidate in song.Scores)
            {
                if (candidate.Balance is { } balance)
                {
                    yield return (song, candidate, balance);
                }
            }
        }
    }

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

        if (Balances(results).Any())
        {
            md.Append("\n## Instrument balance (listening clips)\n\n");
            md.Append("Each MOD sample alone in both renderings, its loudness compared every 100 ms after the overall gain (`--balance`, docs/adr/0051-instrument-balance-metric.md).\n");
            md.Append("*Balance*: mean level error per instrument and moment, dB. *Furthest*: the instrument whose mean offset is largest (+ = too loud in the conversion). Per instrument: `balance-instruments.csv`.\n\n");
            md.Append("| Song | Renderer | Balance dB | Gain dB | Stems | Furthest | Offset dB |\n");
            md.Append("|---|---|---:|---:|---:|---|---:|\n");
            foreach ((SongResult song, CandidateScore candidate, BalanceResult b) in Balances(results))
            {
                int furthest = Enumerable.Range(0, b.Stems.Count).Where(i => b.Report.Instruments[i].Frames > 0).DefaultIfEmpty(-1).MaxBy(i => i < 0 ? 0 : Math.Abs(b.Report.Instruments[i].OffsetDb));
                string name = furthest < 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"{b.Stems[furthest].Sample} {Cell(b.Stems[furthest].Name)} ({Cell(b.Stems[furthest].Voices)})");
                double? offset = furthest < 0 ? null : b.Report.Instruments[furthest].OffsetDb;
                md.Append(CultureInfo.InvariantCulture, $"| `{song.Song}` | {candidate.Renderer} | {Number(b.Report.DistanceDb, "0.00")} | {Number(b.Report.GainDb, "0.0")} | {b.Stems.Count} | {name} | {Number(offset, "+0.0;-0.0")} |\n");
            }
        }

        if (results.SelectMany(r => r.Scores).Any(c => c.Loudness is not null))
        {
            md.Append("\n## Loudness (EBU R128)\n\n");
            md.Append("Each whole rendering on a BS.1770 loudness meter (gated; docs/adr/0053-r128-loudness-in-the-harness.md). *Δ LU*: the conversion minus the original (+ = louder).\n");
            md.Append("*LRA*: loudness range, LU. *True peak*: above 0 dBTP clips. Clips and the original's figures: `loudness.csv`.\n\n");
            md.Append("| Song | Renderer | Original LUFS | Conversion LUFS | Δ LU | LRA original | LRA conversion | True peak dBTP |\n");
            md.Append("|---|---|---:|---:|---:|---:|---:|---:|\n");
            foreach (SongResult song in results)
            {
                foreach (CandidateScore candidate in song.Scores)
                {
                    if (candidate.Loudness is not { } l)
                    {
                        continue;
                    }

                    md.Append(CultureInfo.InvariantCulture, $"| `{song.Song}` | {candidate.Renderer} | {Number(l.Reference.IntegratedLufs, "0.0")} | {Number(l.Candidate.IntegratedLufs, "0.0")} | {Number(l.DifferenceLu, "+0.0;-0.0")} | {Number(l.Reference.RangeLu, "0.0")} | {Number(l.Candidate.RangeLu, "0.0")} | {Number(l.Candidate.TruePeakDbtp, "+0.0;-0.0")} |\n");
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

        (string Song, double MelDb)? worst = null;
        int over = 0;
        foreach (SongResult song in results)
        {
            foreach (CandidateScore candidate in song.Scores.Where(c => c.Renderer == ModRendererCheck.RendererName))
            {
                if (candidate.Score is { } score && (worst is null || score.MelDistanceDb > worst.Value.MelDb))
                {
                    worst = (song.Song, score.MelDistanceDb);
                }

                over += ModRendererCheck.OverTheBar(candidate) ? 1 : 0;
            }
        }

        if (worst is { } w)
        {
            md.Append("\n## In-app MOD renderer against the reference (plan T7.1)\n\n");
            md.Append(CultureInfo.InvariantCulture, $"`{ModRendererCheck.RendererName}` plays the original MOD, not the conversion (docs/adr/0054-mod-renderer.md). Bar: whole-song log-mel ≤ {ModRendererCheck.MaxMelDistanceDb:0.00} dB on every song. ");
            md.Append(CultureInfo.InvariantCulture, $"Worst: `{w.Song}`, {w.MelDb:0.00} dB. {(over == 0 ? "✅ Every song is within the bar." : $"❌ {over} songs are over the bar.")}\n");
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
