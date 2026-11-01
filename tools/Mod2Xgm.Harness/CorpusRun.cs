using System.Globalization;
using Mod2Xgm.Analysis;
using Mod2Xgm.Cli;
using Mod2Xgm.Mod;
using Mod2Xgm.Model;
using Mod2Xgm.Optimizer;
using Mod2Xgm.Render;
using Mod2Xgm.Scoring;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Mod2Xgm.Harness;

/// <summary>
/// Measures each song (docs/harness.md): renders the MOD with the reference player, converts it in-process exactly as
/// <c>mod2xgm</c> does, has each renderer play the conversion, scores every rendering with <see cref="SongScorer"/>,
/// and checks the tick player's song length against the reference's.
/// </summary>
/// <param name="outputDirectory">Where DMFs, logs and renders go (<c>dmf/</c>, <c>logs/</c>, <c>wav/</c>).</param>
/// <param name="sampleRate">The rate everything is rendered at or resampled to before scoring.</param>
/// <param name="saveReferenceWavs">Also write each reference render to <c>wav/name.reference.wav</c>, for listening.</param>
/// <param name="reference">Plays the original.</param>
/// <param name="candidates">Play the conversion.</param>
/// <param name="logger">Progress, one line per step.</param>
/// <param name="clipSeconds">
/// Also score the song's first this many seconds on their own, and write them as <c>wav/name.reference.clip.wav</c> and
/// <c>wav/name.renderer.clip.wav</c> for listening (plan T6.4); <c>null</c> for whole songs only.
/// </param>
/// <param name="balance">
/// Also measure the clip's instrument balance from stems, for renderers that can play any DMF
/// (docs/adr/0051-instrument-balance-metric.md); needs <paramref name="clipSeconds"/>.
/// </param>
/// <param name="fmFit">
/// Also fit FM patches to every sample the conversion analyzed, the song's samples at once
/// (docs/adr/0069-fm-patch-fitting.md); <c>null</c> to skip.
/// </param>
internal sealed class CorpusRun(
    string outputDirectory,
    int sampleRate,
    bool saveReferenceWavs,
    IReferenceRenderer reference,
    IReadOnlyList<ICandidateRenderer> candidates,
    ILogger logger,
    double? clipSeconds = null,
    bool balance = false,
    FmFitOptions? fmFit = null)
{
    /// <summary>
    /// Measures every song in <paramref name="modPaths"/>, <paramref name="jobs"/> at a time; the results are in the
    /// order given, whatever order the songs finish in. A song that fails is reported and skipped.
    /// </summary>
    public IReadOnlyList<SongResult> Run(IReadOnlyList<string> modPaths, int jobs = 1)
    {
        ArgumentNullException.ThrowIfNull(modPaths);
        ArgumentOutOfRangeException.ThrowIfLessThan(jobs, 1);
        foreach (string folder in new[] { "dmf", "logs", "wav", Path.Combine("dmf", "stems"), Path.Combine("wav", "stems") })
        {
            Directory.CreateDirectory(Path.Combine(outputDirectory, folder));
        }

        SongResult[] results = new SongResult[modPaths.Count];
        Parallel.For(0, modPaths.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i => results[i] = Measure(modPaths[i]));
        return results;
    }

    private SongResult Measure(string modPath)
    {
        string name = Path.GetFileNameWithoutExtension(modPath);
        SongFiles song = new(name, modPath, Path.Combine(outputDirectory, "dmf", name + ".dmf"), Path.Combine(outputDirectory, "wav"));
        logger.Information("{Song}: rendering the MOD with {Reference}", name, reference.Name);
        DurationCheck? duration = null;
        int notes = 0;
        try
        {
            byte[] mod = File.ReadAllBytes(modPath);
            ReferenceRender original = reference.Render(mod, sampleRate);
            if (saveReferenceWavs)
            {
                WavFile.Write(Path.Combine(song.WavDirectory, name + ".reference.wav"), original.Audio);
            }

            StereoAudio? referenceClip = null;
            Loudness? referenceClipLoudness = null;
            if (clipSeconds is { } clip)
            {
                referenceClip = original.Audio.Head(clip);
                referenceClipLoudness = Loudness.Of(referenceClip);
                WavFile.Write(Path.Combine(song.WavDirectory, name + ".reference.clip.wav"), referenceClip);
            }

            duration = CheckLength(song, mod, original.DurationSeconds, out IReadOnlyList<RowTiming> rows);
            notes = Convert(song, out IReadOnlyList<SampleAnalysis> analyses);
            AudioSignal referenceMono = original.Audio.ToMono();
            Loudness referenceLoudness = Loudness.Of(original.Audio);
            ReferenceStems? stems = balance && referenceClip is not null && candidates.Any(c => c is IDmfRenderer)
                ? MakeReferenceStems(song, mod, rows, referenceClip)
                : null;
            List<CandidateScore> scores = [];
            foreach (ICandidateRenderer candidate in candidates)
            {
                if (Score(candidate, song, referenceMono, referenceLoudness, referenceClip, referenceClipLoudness, stems) is { } score)
                {
                    scores.Add(score);
                }
            }

            return new SongResult(name, duration, notes, scores, null) { Samples = analyses, FmFits = FitFm(name, analyses) };
        }
        catch (Exception ex) when (ex is HarnessException or ConversionException or IOException)
        {
            logger.Error("{Song}: {Message}", name, ex.Message);
            return new SongResult(name, duration, notes, [], ex.Message);
        }
    }

    /// <summary>The FM fits of every analyzed sample, in the analyses' order, fitted at once; none unless asked for.</summary>
    private IReadOnlyList<FmSampleFit> FitFm(string song, IReadOnlyList<SampleAnalysis> analyses)
    {
        if (fmFit is null)
        {
            return [];
        }

        logger.Information("{Song}: fitting FM patches to {Count} samples", song, analyses.Count);
        FmSampleFit[] fits = new FmSampleFit[analyses.Count];
        Parallel.For(0, analyses.Count, i => fits[i] = FmFitter.Fit(analyses[i], fmFit));
        FmFitReport.Log(logger, fits);
        int regions = fits.Sum(f => f.Regions.Count);
        logger.Information(
            "{Song}: fitted FM patches beat the library in {Better} of {Regions} pitch regions",
            song,
            fits.Sum(f => f.Regions.Count(r => r.FittedBeatsLibrary)),
            regions);
        return fits;
    }

    /// <summary>
    /// Plays the song with the converter's tick player (PAL, the reference's assumption too), compares when each row
    /// starts with the reference (every row to <c>logs/name.rows.csv</c>) and checks the length.
    /// </summary>
    private DurationCheck CheckLength(SongFiles song, byte[] mod, double referenceSeconds, out IReadOnlyList<RowTiming> rows)
    {
        ConversionNotes silent = new(Logger.None);
        ModModule module = ModReader.Read(mod, silent);
        Playback playback = ModTickPlayer.Play(module, AmigaPitch.PalClockHz, 1.0, silent);
        rows = DurationCheck.Rows(playback, r => reference.RowTimes(mod, r));
        System.Text.StringBuilder csv = new("order,row,tick_player_s,openmpt_rules_s,reference_s,difference_s\n");
        foreach (RowTiming row in rows)
        {
            csv.Append(CultureInfo.InvariantCulture, $"{row.Order},{row.Row},{row.TickPlayerSeconds:0.######},{row.OpenMptRulesSeconds:0.######},{row.ReferenceSeconds:0.######},{row.DifferenceSeconds:0.######}\n");
        }

        File.WriteAllText(Path.Combine(outputDirectory, "logs", song.Name + ".rows.csv"), csv.ToString());
        DurationCheck check = DurationCheck.Of(playback, rows, referenceSeconds, DurationCheck.EndsAtF00(module, playback));
        logger.Information(
            "{Song}: tick player {TickPlayer} s on an Amiga, {OpenMptRules} s by OpenMPT's rules; {Reference} {ReferenceSeconds} s ({Verdict})",
            song.Name,
            Seconds(check.TickPlayerSeconds),
            Seconds(check.OpenMptRulesSeconds),
            reference.Name,
            Seconds(check.ReferenceSeconds),
            check.Within ? "within one tick" : "MORE THAN ONE TICK APART");
        if (check.FirstRowOff is { } first)
        {
            logger.Information(
                "{Song}: the players first part at order {Order} row {Row} ({OpenMptRules} s against {Reference} s); every row is in {RowsFile}",
                song.Name,
                first.Order,
                first.Row,
                Seconds(first.OpenMptRulesSeconds),
                Seconds(first.ReferenceSeconds),
                $"logs/{song.Name}.rows.csv");
        }

        if (check.StopsAtF00)
        {
            logger.Information("{Song}: the song stops at F00, which OpenMPT plays past; its end isn't compared", song.Name);
        }

        return check;
    }

    /// <summary>
    /// Converts the MOD with the default options, its whole console output going to <c>logs/name.log</c>; returns the
    /// number of conversion notes, and the conversion's own sample analysis in <paramref name="analyses"/>
    /// (<c>sample-analysis.csv</c>).
    /// </summary>
    private int Convert(SongFiles song, out IReadOnlyList<SampleAnalysis> analyses)
    {
        IReadOnlyList<SampleAnalysis> found = [];
        LogFileSink log = new();
        using (Logger songLogger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(log).CreateLogger())
        {
            try
            {
                ConversionCommand.Execute(new ConversionOptions { InputPath = song.ModPath, OutputPath = song.DmfPath, Force = true }, songLogger, a => found = a);
            }
            finally
            {
                File.WriteAllText(Path.Combine(outputDirectory, "logs", song.Name + ".log"), log.Text);
            }
        }

        analyses = found;
        logger.Information("{Song}: converted to {Dmf} ({Notes} conversion notes)", song.Name, song.DmfPath, log.Warnings);
        return log.Warnings;
    }

    private CandidateScore? Score(
        ICandidateRenderer candidate,
        SongFiles song,
        AudioSignal original,
        Loudness originalLoudness,
        StereoAudio? originalClip,
        Loudness? originalClipLoudness,
        ReferenceStems? stems)
    {
        try
        {
            if (candidate.Render(song) is not { } render)
            {
                logger.Debug("{Song}: nothing from {Renderer}", song.Name, candidate.Name);
                return null;
            }

            StereoAudio audio = render.Audio.At(sampleRate);
            ScoreOptions options = render.InReferenceTime ? ScoreOptions.Default with { Align = false } : ScoreOptions.Default;
            ScoreReport score = SongScorer.Compare(original, audio.ToMono(), options, logger);
            LoudnessComparison loudness = new(originalLoudness, Loudness.Of(audio));
            ScoreReport? clip = null;
            LoudnessComparison? clipLoudness = null;
            BalanceResult? balanced = null;
            if (originalClip is not null && originalClipLoudness is not null)
            {
                StereoAudio candidateClip = audio.Head(originalClip.Seconds);
                WavFile.Write(Path.Combine(song.WavDirectory, $"{song.Name}.{candidate.Name}.clip.wav"), candidateClip);
                clip = SongScorer.Compare(originalClip.ToMono(), candidateClip.ToMono(), options, logger);
                clipLoudness = new LoudnessComparison(originalClipLoudness, Loudness.Of(candidateClip));
                logger.Information(
                    "{Song}: {Renderer} scores {Combined} on the first {Seconds} s",
                    song.Name,
                    candidate.Name,
                    clip.Combined.ToString("0.00", CultureInfo.InvariantCulture),
                    originalClip.Seconds.ToString("0.##", CultureInfo.InvariantCulture));
                if (stems is not null && candidate is IDmfRenderer player)
                {
                    balanced = Balance(candidate.Name, player, song, stems, audio, clip.LagSeconds);
                }
            }

            logger.Information(
                "{Song}: {Renderer} scores {Combined} (log-mel {Mel} dB, onset F1 {F1}, loudness {Loudness} LU){Remark}",
                song.Name,
                candidate.Name,
                score.Combined.ToString("0.00", CultureInfo.InvariantCulture),
                score.MelDistanceDb.ToString("0.00", CultureInfo.InvariantCulture),
                score.OnsetF1.ToString("0.00", CultureInfo.InvariantCulture),
                loudness.DifferenceLu?.ToString("+0.0;-0.0", CultureInfo.InvariantCulture) ?? "–",
                render.Remark is null ? string.Empty : "; " + render.Remark);
            return new CandidateScore(candidate.Name, score, render.Remark, clip, balanced, loudness, clipLoudness);
        }
        catch (HarnessException ex)
        {
            logger.Error("{Song}: {Renderer}: {Message}", song.Name, candidate.Name, ex.Message);
            return new CandidateScore(candidate.Name, null, ex.Message);
        }
    }

    /// <summary>
    /// The reference's stems for the clip: which orders the clip reaches (with a second to spare for the alignment), the
    /// samples heard in them, and each sample alone rendered by the reference player (docs/adr/0051-instrument-balance-metric.md).
    /// </summary>
    private ReferenceStems MakeReferenceStems(SongFiles song, byte[] mod, IReadOnlyList<RowTiming> rows, StereoAudio referenceClip)
    {
        StemSource source = StemSource.Prepare(mod, File.ReadAllBytes(song.DmfPath));
        double until = referenceClip.Seconds + ScoreOptions.Default.MaxLagSeconds;
        int orders = Math.Min(source.Orders, rows.Where(r => r.ReferenceSeconds < until).Select(r => r.Order + 1).DefaultIfEmpty(1).Max());
        IReadOnlyList<int> samples = source.SamplesIn(orders);
        logger.Information("{Song}: rendering {Count} stems of the first {Orders} orders with {Reference}", song.Name, samples.Count, orders, reference.Name);
        AudioSignal[] audio = [.. samples.Select(s => reference.Render(source.SoloMod(s), sampleRate).Audio.Head(referenceClip.Seconds).ToMono())];
        StemInfo[] stems = [.. samples.Select(s => new StemInfo(s + 1, source.Name(s), source.Voices(s, orders)))];
        return new ReferenceStems(source, orders, samples, stems, referenceClip.ToMono(), audio);
    }

    /// <summary>
    /// Renders the conversion's stems with <paramref name="player"/>, lines them up by the clip's lag, and measures the
    /// balance against the reference's stems.
    /// </summary>
    private BalanceResult Balance(string renderer, IDmfRenderer player, SongFiles song, ReferenceStems stems, StereoAudio audio, double lagSeconds)
    {
        int lag = (int)Math.Round(lagSeconds * sampleRate);
        int length = stems.Mix.Length;
        AudioSignal Clip(StereoAudio render)
        {
            AudioSignal moved = render.At(sampleRate).ToMono().Shifted(-lag);
            return moved.Length <= length ? moved : new AudioSignal(moved.Samples[..length].ToArray(), sampleRate);
        }

        List<InstrumentStem> pairs = [];
        for (int i = 0; i < stems.Samples.Count; i++)
        {
            string stem = FormattableString.Invariant($"{song.Name}.s{stems.Stems[i].Sample:00}");
            string dmf = Path.Combine(outputDirectory, "dmf", "stems", stem + ".dmf");
            File.WriteAllBytes(dmf, stems.Source.SoloDmf(stems.Samples[i], stems.Orders));
            string wav = Path.Combine(song.WavDirectory, "stems", $"{stem}.{renderer}.wav");
            AudioSignal candidate = Clip(player.RenderDmf(dmf, wav));
            File.Delete(wav); // Hundreds of stems; the DMF stays, to render again by hand.
            pairs.Add(new InstrumentStem(stems.Stems[i].Sample, stems.Audio[i], candidate));
        }

        AudioSignal mix = Clip(audio);
        BalanceReport report = BalanceMetrics.Compare(stems.Mix, mix, pairs);
        double? referenceMix = Loudness.GatedLufs(stems.Mix);
        double? candidateMix = Loudness.GatedLufs(mix);
        BalanceResult result = new(
            report,
            stems.Stems,
            BalanceMetrics.SumErrorDb(stems.Mix, stems.Audio),
            BalanceMetrics.SumErrorDb(mix, pairs.Select(p => p.Candidate)),
            [.. pairs.Select(p => Loudness.GatedLufs(p.Candidate) - candidateMix - (Loudness.GatedLufs(p.Reference) - referenceMix))]);
        logger.Information(
            "{Song}: {Renderer} balance {Balance} dB over {Count} instruments (stems add up to the mix's loudness within {ReferenceSum} / {CandidateSum} dB)",
            song.Name,
            renderer,
            report.DistanceDb.ToString("0.00", CultureInfo.InvariantCulture),
            pairs.Count,
            result.ReferenceSumErrorDb.ToString("0.00", CultureInfo.InvariantCulture),
            result.CandidateSumErrorDb.ToString("0.00", CultureInfo.InvariantCulture));
        return result;
    }

    private static string Seconds(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>One song's reference stems, made once and compared with each renderer's.</summary>
    /// <param name="Source">Makes the conversion's stems.</param>
    /// <param name="Orders">Orders the clip reaches.</param>
    /// <param name="Samples">The samples (0-based) with a stem.</param>
    /// <param name="Stems">What each stem is, in the same order.</param>
    /// <param name="Mix">The reference clip, mono.</param>
    /// <param name="Audio">Each sample alone in the reference clip, mono, in the same order.</param>
    private sealed record ReferenceStems(StemSource Source, int Orders, IReadOnlyList<int> Samples, IReadOnlyList<StemInfo> Stems, AudioSignal Mix, IReadOnlyList<AudioSignal> Audio);

    /// <summary>Keeps a conversion's console output as text, in the console's format, and counts its conversion notes.</summary>
    private sealed class LogFileSink : ILogEventSink
    {
        private readonly MessageTemplateTextFormatter _format = new(Mod2Xgm.Program.ConsoleOutputTemplate, CultureInfo.InvariantCulture);
        private readonly System.Text.StringBuilder _text = new();

        /// <summary>Conversion notes are the Warning events (AGENTS.md).</summary>
        public int Warnings { get; private set; }

        public string Text => _text.ToString();

        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Level == LogEventLevel.Warning)
            {
                Warnings++;
            }

            using StringWriter writer = new(_text, CultureInfo.InvariantCulture);
            _format.Format(logEvent, writer);
        }
    }
}
