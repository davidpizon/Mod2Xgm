using System.Globalization;
using System.Text;
using Mod2Xgm.Analysis;
using Mod2Xgm.Optimizer;
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

    /// <summary>Header of <c>sample-analysis.csv</c>.</summary>
    public const string SampleAnalysisHeader =
        "song,sample,name,kind,percussive,tonal,noisy,period_frames,voiced_share,deviation_cents,drift_cents,peak_db,attack_ms,decay_ms,sustain_db,loop_frames,loop_cycles,regions,region_rates_hz,fundamentals_hz,notes";

    /// <summary>Header of <c>fm-fit.csv</c>.</summary>
    public const string FmFitHeader =
        "song,sample,name,region,library,library_db,exhaustive_db,genetic_db,best,best_db,best_mfcc,best_harmonic_db,no_lfo_db,lfo_rate,patch,fitted_beats_library,renders,seconds";

    /// <summary>Header of <c>psg-fit.csv</c>.</summary>
    public const string PsgFitHeader =
        "song,sample,name,region,most_at_once,sound,noise_rate,period,pitch_hz,cents_off,channels,room,mel_db,mfcc,harmonic_db,best,best_with_room";

    /// <summary>Header of <c>pcm-fit.csv</c>.</summary>
    public const string PcmFitHeader =
        "song,sample,name,target,most_at_once,kind,savings,room,copies,bytes,notes,mel_db,harmonic_db,smallest,start_frame,seconds,plain_mel_db,bent_notes";

    /// <summary>Header of <c>pre-mix.csv</c>.</summary>
    public const string PreMixHeader =
        "song,target,samples,offsets_ticks,occurrences,saves_steal,window_s,switch_s,end,head_bytes,tail_bytes,slots,tail_sample,tail_start_frame,tail_id,clip_db,mel_db,seam_mel_db";

    /// <summary>Header of <c>voice-steals.csv</c>.</summary>
    public const string VoiceStealsHeader =
        "song,target,voices,pcm_notes,overlaps,candidates,steals,steals_with_pre_mix,steals_removed,seconds";

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

    /// <summary>One line per song and sample: the sample analysis's scores, pitch, envelope, loop and regions (see <see cref="SampleAnalysisHeader"/>).</summary>
    public static string SampleAnalysisCsv(IReadOnlyList<SongResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        StringBuilder csv = new();
        csv.Append(SampleAnalysisHeader).Append('\n');
        foreach (SongResult song in results)
        {
            foreach (SampleAnalysis a in song.Samples)
            {
                csv.AppendJoin(
                    ',',
                    Csv(song.Song),
                    (a.Sample + 1).ToString(CultureInfo.InvariantCulture),
                    Csv(a.Name),
                    SampleAnalysisReport.KindName(a.Kind),
                    Number(a.Percussive, "0.###"),
                    Number(a.Tonal, "0.###"),
                    Number(a.Noisy, "0.###"),
                    Number(a.Pitch?.PeriodFrames, "0.###"),
                    Number(a.Pitch?.VoicedShare, "0.###"),
                    Number(a.Pitch?.DeviationCents, "0.#"),
                    Number(a.Pitch?.DriftCents, "0.#"),
                    Number(a.Envelope.PeakDb, "0.##"),
                    Number(a.Envelope.AttackSeconds * 1000, "0.#"),
                    Number(a.Envelope.DecaySeconds * 1000, "0.#"),
                    Number(a.Envelope.SustainDb, "0.##"),
                    a.Loop is { } loop ? loop.Length.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    Number(a.Loop?.Cycles, "0.##"),
                    a.Regions.Count.ToString(CultureInfo.InvariantCulture),
                    string.Join(' ', a.Regions.Select(r => Number(r.RateHz, "0"))),
                    string.Join(' ', a.Regions.Select(r => Number(r.FundamentalHz, "0.#"))),
                    string.Join(' ', a.Regions.Select(r => r.Notes.ToString(CultureInfo.InvariantCulture))));
                csv.Append('\n');
            }
        }

        return csv.ToString();
    }

    /// <summary>
    /// One line per song, sample and pitch region (<c>--fm-fit</c>): the closest library patch, each search's fit and the
    /// best, their log-mel distances, and the work it took (see <see cref="FmFitHeader"/>).
    /// </summary>
    public static string FmFitCsv(IReadOnlyList<SongResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        StringBuilder csv = new();
        csv.Append(FmFitHeader).Append('\n');
        foreach (SongResult song in results)
        {
            foreach (FmSampleFit fit in song.FmFits)
            {
                foreach (FmRegionFit r in fit.Regions)
                {
                    csv.AppendJoin(
                        ',',
                        Csv(song.Song),
                        (fit.Sample + 1).ToString(CultureInfo.InvariantCulture),
                        Csv(fit.Name),
                        (r.Region + 1).ToString(CultureInfo.InvariantCulture),
                        Csv(r.Library.Patch.Name),
                        Number(r.Library.Score.MelDb, "0.###"),
                        Number(r.Exhaustive?.Score.MelDb, "0.###"),
                        Number(r.Genetic?.Score.MelDb, "0.###"),
                        FmFitReport.SourceName(r.Best.Source),
                        Number(r.Best.Score.MelDb, "0.###"),
                        Number(r.Best.Score.MfccDistance, "0.##"),
                        Number(r.Best.Score.HarmonicDb, "0.###"),
                        Number(r.NoLfo.Score.MelDb, "0.###"),
                        r.Best.LfoRate?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                        Csv(FmFitReport.Settings(r.Best)),
                        r.FittedBeatsLibrary ? "yes" : "no",
                        r.Renders.ToString(CultureInfo.InvariantCulture),
                        Number(r.Elapsed.TotalSeconds, "0.#"));
                    csv.Append('\n');
                }
            }
        }

        return csv.ToString();
    }

    /// <summary>
    /// One line per song, sample, pitch region and PSG try (<c>--psg-fit</c>): the try's settings, its pitch, the channels
    /// it takes and whether the song has room for it, its distances, and whether it is the region's closest, and its
    /// closest with room (see <see cref="PsgFitHeader"/>).
    /// </summary>
    public static string PsgFitCsv(IReadOnlyList<SongResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        StringBuilder csv = new();
        csv.Append(PsgFitHeader).Append('\n');
        foreach (SongResult song in results)
        {
            foreach (PsgSampleFit fit in song.PsgFits)
            {
                foreach (PsgRegionFit r in fit.Regions)
                {
                    foreach (PsgCandidate c in r.Candidates)
                    {
                        csv.AppendJoin(
                            ',',
                            Csv(song.Song),
                            (fit.Sample + 1).ToString(CultureInfo.InvariantCulture),
                            Csv(fit.Name),
                            (r.Region + 1).ToString(CultureInfo.InvariantCulture),
                            fit.MostAtOnce.ToString(CultureInfo.InvariantCulture),
                            PsgFitReport.SoundName(c.Setting.Sound),
                            c.Setting.UsesNoise ? c.Setting.NoiseRate.ToString(CultureInfo.InvariantCulture) : string.Empty,
                            c.Setting.UsesToneChannel ? c.Setting.Period.ToString(CultureInfo.InvariantCulture) : string.Empty,
                            Number(c.Setting.PitchHz, "0.##"),
                            Number(c.CentsOff, "0.#"),
                            c.Setting.Channels.ToString(CultureInfo.InvariantCulture),
                            PsgFitReport.RoomName(c.Room),
                            Number(c.Score.MelDb, "0.###"),
                            Number(c.Score.MfccDistance, "0.##"),
                            Number(c.Score.HarmonicDb, "0.###"),
                            ReferenceEquals(c, r.Best) ? "yes" : "no",
                            ReferenceEquals(c, r.BestThatFits) ? "yes" : "no");
                        csv.Append('\n');
                    }
                }
            }
        }

        return csv.ToString();
    }

    /// <summary>
    /// One line per song, sample, target and PCM candidate (<c>--pcm-fit</c>): each combination of savings (kind
    /// <c>copies</c>) with its copies, bytes, room, notes and note-weighted distances, whether it is the smallest and the sample's bent notes; then
    /// each baked note (kind <c>baked</c>) with its notes, start frame, length, distance and the plain copy's distance (see
    /// <see cref="PcmFitHeader"/>).
    /// </summary>
    public static string PcmFitCsv(IReadOnlyList<SongResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        StringBuilder csv = new();
        csv.Append(PcmFitHeader).Append('\n');
        foreach (SongResult song in results)
        {
            foreach (PcmSampleFit fit in song.PcmFits)
            {
                string head = string.Join(
                    ',',
                    Csv(song.Song),
                    (fit.Sample + 1).ToString(CultureInfo.InvariantCulture),
                    Csv(fit.Name),
                    PcmTargets.Name(fit.Target),
                    fit.MostAtOnce.ToString(CultureInfo.InvariantCulture));
                foreach (PcmOption o in fit.Options)
                {
                    csv.Append(head).Append(',').AppendJoin(
                        ',',
                        "copies",
                        PcmTargets.Name(o.Savings),
                        PcmFitReport.RoomName(o.Room),
                        o.Slots.ToString(CultureInfo.InvariantCulture),
                        o.Bytes.ToString(CultureInfo.InvariantCulture),
                        o.Pitches.Sum(p => p.Notes).ToString(CultureInfo.InvariantCulture),
                        Number(o.MelDb, "0.###"),
                        Number(o.HarmonicDb, "0.###"),
                        ReferenceEquals(o, fit.Smallest) ? "yes" : "no",
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        fit.BentNotes.ToString(CultureInfo.InvariantCulture));
                    csv.Append('\n');
                }

                foreach (PcmBaked b in fit.Baked)
                {
                    csv.Append(head).Append(',').AppendJoin(
                        ',',
                        "baked",
                        string.Empty,
                        string.Empty,
                        "1",
                        b.Bytes.ToString(CultureInfo.InvariantCulture),
                        b.Notes.ToString(CultureInfo.InvariantCulture),
                        Number(b.Score.MelDb, "0.###"),
                        Number(b.Score.HarmonicDb, "0.###"),
                        string.Empty,
                        b.StartFrame.ToString(CultureInfo.InvariantCulture),
                        Number(b.Seconds, "0.###"),
                        Number(b.PlainScore.MelDb, "0.###"),
                        string.Empty);
                    csv.Append('\n');
                }
            }
        }

        return csv.ToString();
    }

    /// <summary>
    /// One line per song, target and pre-mix candidate (<c>--pre-mix</c>): its samples and their start ticks in the window,
    /// how often the song plays it, whether it saves a voice steal, the window and switch, how the outlasting note goes on,
    /// its bytes and slots, the tail's sample, start frame and id, how far the mix was turned down, and the distances of the
    /// whole and of the seam (see <see cref="PreMixHeader"/>).
    /// </summary>
    public static string PreMixCsv(IReadOnlyList<SongResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        StringBuilder csv = new();
        csv.Append(PreMixHeader).Append('\n');
        foreach (SongResult song in results)
        {
            foreach (PreMixSongFit fit in song.PreMixFits)
            {
                foreach (PreMixCandidate c in fit.Candidates)
                {
                    csv.AppendJoin(
                        ',',
                        Csv(song.Song),
                        PcmTargets.Name(fit.Target),
                        PreMixReport.Samples(c),
                        PreMixReport.Offsets(c),
                        c.Occurrences.ToString(CultureInfo.InvariantCulture),
                        c.SavesSteal ? "yes" : "no",
                        Number(c.WindowSeconds, "0.###"),
                        Number(c.SwitchSeconds, "0.###"),
                        PreMixReport.EndName(c.End),
                        c.HeadBytes.ToString(CultureInfo.InvariantCulture),
                        c.Tail is { } tail ? tail.Bytes.ToString(CultureInfo.InvariantCulture) : string.Empty,
                        c.Slots.ToString(CultureInfo.InvariantCulture),
                        c.Tail is { } s ? (s.Sample + 1).ToString(CultureInfo.InvariantCulture) : string.Empty,
                        c.Tail is { } f ? f.StartFrame.ToString(CultureInfo.InvariantCulture) : string.Empty,
                        c.Tail is { } i ? i.Id.ToString(CultureInfo.InvariantCulture) : string.Empty,
                        Number(c.ClipDb, "0.##"),
                        Number(c.Score.MelDb, "0.###"),
                        Number(c.Seam?.MelDb, "0.###"));
                    csv.Append('\n');
                }
            }
        }

        return csv.ToString();
    }

    /// <summary>
    /// One line per song and target (<c>--pre-mix</c>): its PCM voices, the notes that may be pre-mixed, the overlaps found,
    /// the candidates, and the notes that start while every voice is busy without and with the pre-mixes (see
    /// <see cref="VoiceStealsHeader"/>).
    /// </summary>
    public static string VoiceStealsCsv(IReadOnlyList<SongResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        StringBuilder csv = new();
        csv.Append(VoiceStealsHeader).Append('\n');
        foreach (SongResult song in results)
        {
            foreach (PreMixSongFit fit in song.PreMixFits)
            {
                csv.AppendJoin(
                    ',',
                    Csv(song.Song),
                    PcmTargets.Name(fit.Target),
                    fit.Voices.ToString(CultureInfo.InvariantCulture),
                    fit.PcmNotes.ToString(CultureInfo.InvariantCulture),
                    fit.Overlaps.ToString(CultureInfo.InvariantCulture),
                    fit.Candidates.Count.ToString(CultureInfo.InvariantCulture),
                    fit.Steals.ToString(CultureInfo.InvariantCulture),
                    fit.StealsWithPreMix.ToString(CultureInfo.InvariantCulture),
                    fit.StealsRemoved.ToString(CultureInfo.InvariantCulture),
                    Number(fit.Elapsed.TotalSeconds, "0.#"));
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
