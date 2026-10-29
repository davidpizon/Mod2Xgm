using System.Globalization;
using Mod2Xgm.Cli;
using Mod2Xgm.Mod;
using Mod2Xgm.Model;
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
internal sealed class CorpusRun(
    string outputDirectory,
    int sampleRate,
    bool saveReferenceWavs,
    IReferenceRenderer reference,
    IReadOnlyList<ICandidateRenderer> candidates,
    ILogger logger)
{
    /// <summary>
    /// Measures every song in <paramref name="modPaths"/>, <paramref name="jobs"/> at a time; the results are in the
    /// order given, whatever order the songs finish in. A song that fails is reported and skipped.
    /// </summary>
    public IReadOnlyList<SongResult> Run(IReadOnlyList<string> modPaths, int jobs = 1)
    {
        ArgumentNullException.ThrowIfNull(modPaths);
        ArgumentOutOfRangeException.ThrowIfLessThan(jobs, 1);
        foreach (string folder in new[] { "dmf", "logs", "wav" })
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

            duration = CheckLength(song, mod, original.DurationSeconds);
            notes = Convert(song);
            AudioSignal referenceMono = original.Audio.ToMono();
            List<CandidateScore> scores = [];
            foreach (ICandidateRenderer candidate in candidates)
            {
                if (Score(candidate, song, referenceMono) is { } score)
                {
                    scores.Add(score);
                }
            }

            return new SongResult(name, duration, notes, scores, null);
        }
        catch (Exception ex) when (ex is HarnessException or ConversionException or IOException)
        {
            logger.Error("{Song}: {Message}", name, ex.Message);
            return new SongResult(name, duration, notes, [], ex.Message);
        }
    }

    /// <summary>
    /// Plays the song with the converter's tick player (PAL, the reference's assumption too), compares when each row
    /// starts with the reference (every row to <c>logs/name.rows.csv</c>) and checks the length.
    /// </summary>
    private DurationCheck CheckLength(SongFiles song, byte[] mod, double referenceSeconds)
    {
        ConversionNotes silent = new(Logger.None);
        ModModule module = ModReader.Read(mod, silent);
        Playback playback = ModTickPlayer.Play(module, AmigaPitch.PalClockHz, 1.0, silent);
        IReadOnlyList<RowTiming> rows = DurationCheck.Rows(playback, r => reference.RowTimes(mod, r));
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

    /// <summary>Converts the MOD with the default options, its whole console output going to <c>logs/name.log</c>; returns the number of conversion notes.</summary>
    private int Convert(SongFiles song)
    {
        LogFileSink log = new();
        using (Logger songLogger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(log).CreateLogger())
        {
            try
            {
                ConversionCommand.Execute(new ConversionOptions { InputPath = song.ModPath, OutputPath = song.DmfPath, Force = true }, songLogger);
            }
            finally
            {
                File.WriteAllText(Path.Combine(outputDirectory, "logs", song.Name + ".log"), log.Text);
            }
        }

        logger.Information("{Song}: converted to {Dmf} ({Notes} conversion notes)", song.Name, song.DmfPath, log.Warnings);
        return log.Warnings;
    }

    private CandidateScore? Score(ICandidateRenderer candidate, SongFiles song, AudioSignal original)
    {
        try
        {
            if (candidate.Render(song) is not { } render)
            {
                logger.Debug("{Song}: nothing from {Renderer}", song.Name, candidate.Name);
                return null;
            }

            ScoreReport score = SongScorer.Compare(original, render.Audio.At(sampleRate).ToMono(), ScoreOptions.Default, logger);
            logger.Information(
                "{Song}: {Renderer} scores {Combined} (log-mel {Mel} dB, onset F1 {F1}){Remark}",
                song.Name,
                candidate.Name,
                score.Combined.ToString("0.00", CultureInfo.InvariantCulture),
                score.MelDistanceDb.ToString("0.00", CultureInfo.InvariantCulture),
                score.OnsetF1.ToString("0.00", CultureInfo.InvariantCulture),
                render.Remark is null ? string.Empty : "; " + render.Remark);
            return new CandidateScore(candidate.Name, score, render.Remark);
        }
        catch (HarnessException ex)
        {
            logger.Error("{Song}: {Renderer}: {Message}", song.Name, candidate.Name, ex.Message);
            return new CandidateScore(candidate.Name, null, ex.Message);
        }
    }

    private static string Seconds(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

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
