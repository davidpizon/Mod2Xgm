using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Mod2Xgm.Optimizer;
using Mod2Xgm.Output;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Mod2Xgm.Harness;

/// <summary>
/// The accuracy harness's command line (docs/harness.md). Exit codes: 0 every song measured and within one tick,
/// 1 a song failed or its length is off by more than a tick, 2 the harness couldn't run (a tool is missing, bad options).
/// Its output goes through the converter's reporters, chosen by <c>--progress</c> (docs/cli-output.md#the-harness).
/// </summary>
internal static class Program
{
    /// <summary>The exit code when the harness couldn't run: a tool is missing, an input isn't there, bad options.</summary>
    internal const int CannotRun = 2;

    /// <summary>Default corpus, relative to the repository root (plan T6.1).</summary>
    internal const string DefaultCorpus = "tests/data/corpus";

    /// <summary>Default report folder, git-ignored.</summary>
    internal const string DefaultOutput = "artifacts/harness";

    /// <summary>Songs measured at once by default: half the processors (renders and scoring are heavy), at most 4.</summary>
    internal static int DefaultJobs => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>DefleMask exports made for plan T5.7, used when present.</summary>
    private const string DefaultDefleMaskWavs = "tests/golden/corpus-check/out/wav";

    internal static int Main(string[] args)
    {
        TerminalProbe terminal = TerminalProbe.Current();
        using TextWriter? output = terminal.OutputRedirected ? Utf8(Console.OpenStandardOutput()) : null;
        using TextWriter? error = terminal.ErrorRedirected ? Utf8(Console.OpenStandardError()) : null;
        return Run(args, output ?? Console.Out, error ?? Console.Error, terminal);
    }

    /// <summary>
    /// Makes the reporter <c>--progress</c> asks for (found before parsing, so parse errors use it too), logs through it,
    /// runs the harness and reports how it ended. A <see cref="HarnessException"/> exits 2; any other exception is a bug,
    /// reported with its stack, and exits 2 too. Never reads stdin.
    /// </summary>
    internal static int Run(string[] args, TextWriter output, TextWriter error, TerminalProbe terminal)
    {
        ArgumentNullException.ThrowIfNull(args);
        LoggingLevelSwitch level = new(); // starts at Information
        Stopwatch clock = Stopwatch.StartNew();
        using IConversionReporter reporter = Reporters.Create(ProgressModeScan.Find(args), output, error, terminal);
        Log.Logger = Mod2Xgm.Program.CreateLogger(level, reporter);
        PhaseTracker tracker = new(reporter, TimeProvider.System, new EtaEstimator());
        try
        {
            int code = Invoke(args, options =>
            {
                level.MinimumLevel = options.Verbose ? LogEventLevel.Debug : LogEventLevel.Information;
                if (!options.NoCache)
                {
                    // Earlier runs' song times, next to the converter's (docs/cli-output.md#time-estimate).
                    tracker.Estimator.History = TimingHistory.Load(options.CacheDirectory ?? CandidateCache.DefaultRoot, Log.Logger);
                }

                return Execute(options, Log.Logger, tracker);
            });
            tracker.Estimator.History?.Save();
            Log.CloseAndFlush();
            reporter.Completed(new RunOutcome((ExitCode)code, tracker.Timings, clock.Elapsed));
            return code;
        }
        catch (HarnessException ex)
        {
            return Failed(ex.Message, ex.Kind, null);
        }
        catch (Exception ex)
        {
            return Failed(ex.Message, "internal", ex);
        }

        int Failed(string message, string kind, Exception? exception)
        {
            tracker.Estimator.History?.Save();
            Log.CloseAndFlush();
            reporter.Failed(new RunFailure((ExitCode)CannotRun, message, exception, null, tracker.Timings, clock.Elapsed) { Kind = kind });
            return CannotRun;
        }
    }

    /// <summary>
    /// A redirected stream is written in UTF-8 without a byte-order mark, as the converter's (docs/cli-output.md#streams).
    /// </summary>
    private static StreamWriter Utf8(Stream stream) => new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };

    /// <summary>
    /// Parses <paramref name="args"/> and hands the options to <paramref name="execute"/>; a bad command line throws a
    /// usage <see cref="HarnessException"/>, which exits 2.
    /// </summary>
    internal static int Invoke(string[] args, Func<HarnessOptions, int> execute)
    {
        Argument<string[]> inputs = new("inputs")
        {
            Description = "MOD files or folders of them (default: " + DefaultCorpus + ").",
            Arity = ArgumentArity.ZeroOrMore,
        };

        // An argument that takes any number of values would otherwise take a mistyped option as a file name.
        inputs.Validators.Add(result =>
        {
            foreach (System.CommandLine.Parsing.Token token in result.Tokens.Where(t => t.Value.StartsWith('-')))
            {
                result.AddError($"Unknown option: {token.Value}");
            }
        });
        Option<string> output = new("--output", "-o") { Description = "Folder for the report, DMFs, logs and renders.", DefaultValueFactory = _ => DefaultOutput };
        Option<int> rate = new("--rate") { Description = "Sample rate to render and score at, in Hz.", DefaultValueFactory = _ => 44_100 };
        Option<string?> libopenmpt = new("--libopenmpt") { Description = "Folder holding libopenmpt (default: the system's search path)." };
        Option<string?> furnace = new("--furnace") { Description = "Path to Furnace (default: furnace on PATH, or its Windows install folder)." };
        Option<bool> noFurnace = new("--no-furnace") { Description = "Don't render the DMFs with Furnace." };
        Option<string?> deflemask = new("--deflemask-wavs") { Description = "Folder of DefleMask WAV exports named song.wav (default: " + DefaultDefleMaskWavs + " when it exists)." };
        Option<bool> noDefleMask = new("--no-deflemask") { Description = "Don't score DefleMask exports." };
        Option<bool> saveReference = new("--save-reference-wavs") { Description = "Also write each reference render to wav/song.reference.wav." };
        Option<double?> clip = new("--clip") { Description = "Also score each song's first this many seconds, and write them as wav/*.clip.wav for listening." };
        Option<bool> balance = new("--balance") { Description = "Also measure each clip's instrument balance from stems (needs --clip and Furnace)." };
        Option<bool> fmFit = new("--fm-fit") { Description = "Also fit FM patches to every sample and write fm-fit.csv (slow; docs/adr/0069-fm-patch-fitting.md)." };
        Option<int?> fmRenders = new("--fm-max-renders") { Description = "Most notes each step of a region's FM fit renders (default: no limit)." };
        Option<bool> psgFit = new("--psg-fit") { Description = "Also make PSG candidates for every sample and write psg-fit.csv (docs/adr/0070-psg-candidates.md)." };
        Option<bool> pcmFit = new("--pcm-fit") { Description = "Also make PCM candidates for every sample and target and write pcm-fit.csv (docs/adr/0071-pcm-candidates.md)." };
        Option<bool> preMix = new("--pre-mix") { Description = "Also make pre-mixed PCM candidates for every song and target and write pre-mix.csv and voice-steals.csv (docs/adr/0072-pre-mix-candidates.md)." };
        Option<bool> ch3 = new("--ch3") { Description = "Also find locked pairs and make CH3 special-mode candidates, writing locked-pairs.csv and ch3-fit.csv (docs/adr/0073-ch3-locked-pairs.md); --fm-max-renders limits its fits." };
        Option<bool> dropClues = new("--drop-clues") { Description = "Also find which tracks are cheap to drop or merge and write drop-clues.csv and track-levels.csv (docs/adr/0084-drop-clues.md)." };
        Option<bool> noCache = new("--no-cache") { Description = "Don't read or write the candidate cache (docs/adr/0074-candidate-cache.md)." };
        Option<string?> cacheDir = new("--cache-dir") { Description = "Folder for the candidate cache (default: the user's cache folder, as mod2xgm's)." };
        Option<int> jobs = new("--jobs", "-j") { Description = "Songs measured at once.", DefaultValueFactory = _ => DefaultJobs };
        Option<bool> verbose = new("--verbose", "-v") { Description = "Log every scoring step." };
        Option<string?> remake = new("--remake") { Description = "Instead of scoring, make every row's listening clips again from an answers file and check them byte for byte (docs/harness.md#listening-answers)." };
        Option<string?> fit = new("--fit") { Description = "Instead of scoring, remake an answers file's clips and fit the learned listening score on them, rewriting src/Scoring/ListeningWeights.cs after a fit (docs/harness.md#the-fit)." };
        Option<string?> pick = new("--pick") { Description = "Instead of scoring, pick the next listening round's pairs, those the learned score is least sure of, given an answers file (docs/harness.md#picking-a-round)." };
        Option<int> pairs = new("--pairs") { Description = "How many pairs --pick picks.", DefaultValueFactory = _ => 10 };
        Option<string?> kinds = new("--kinds") { Description = "With --pick: the kinds of change to pick from, comma-separated (simple, budget, no-dac, half-speed, no-layers, no-pre-mix); every kind when not given." };
        Option<int> maxPerKind = new("--max-per-kind") { Description = "With --pick: the most pairs of one kind.", DefaultValueFactory = _ => PairPicker.MaxPerKind };
        Option<string?> embeddings = new("--embeddings") { Description = "Instead of scoring, remake an answers file's clips and compare pretrained listening models (PANNs, VGGish, CLAP, CDPAM) with log-mel and the learned score on them (docs/harness.md#pretrained-models)." };
        Option<string?> holdout = new("--holdout") { Description = "Instead of scoring, remake an answers file's hold-out rows (songs in " + HoldoutSongs.DefaultFolder + ") and score the learned listening score on them against the bar of docs/adr/0105-hold-out-songs.md (docs/harness.md#the-hold-out)." };
        Option<string?> models = new("--models") { Description = "Folder of the exported .onnx models --embeddings uses (default: the user's local data folder, Mod2Xgm/models; tools/onnx-export/README.md)." };
        Option<string> progress = new(ProgressModeScan.OptionName)
        {
            Description = "How progress and results are shown: fancy (the default), text, json or silent (docs/cli-output.md).",
            DefaultValueFactory = _ => "fancy",
        };
        progress.AcceptOnlyFromAmong([.. ProgressModeScan.Names.Keys]);

        RootCommand root = new("Mod2Xgm accuracy harness: scores conversions against OpenMPT (docs/harness.md).")
        {
            inputs, output, rate, libopenmpt, furnace, noFurnace, deflemask, noDefleMask, saveReference, clip, balance, fmFit, fmRenders, psgFit, pcmFit, preMix, ch3, dropClues, noCache, cacheDir, jobs, verbose, progress, remake, fit, pick, pairs, kinds, maxPerKind, embeddings, models, holdout,
        };
        root.SetAction(result =>
        {
            int sampleRate = result.GetValue(rate);
            if (sampleRate is < 8_000 or > 192_000)
            {
                throw HarnessException.Usage(string.Create(CultureInfo.InvariantCulture, $"--rate {sampleRate}: between 8000 and 192000 Hz, please"));
            }

            int songsAtOnce = result.GetValue(jobs);
            if (songsAtOnce is < 1 or > 64)
            {
                throw HarnessException.Usage(string.Create(CultureInfo.InvariantCulture, $"--jobs {songsAtOnce}: between 1 and 64, please"));
            }

            double? clipSeconds = result.GetValue(clip);
            if (clipSeconds is < 1 or > 600)
            {
                throw HarnessException.Usage(string.Create(CultureInfo.InvariantCulture, $"--clip {clipSeconds}: between 1 and 600 seconds, please"));
            }

            bool measureBalance = result.GetValue(balance);
            if (measureBalance && clipSeconds is null)
            {
                throw HarnessException.Usage("--balance measures the listening clips: give --clip too");
            }

            int? maxRenders = result.GetValue(fmRenders);
            if (maxRenders is < 1)
            {
                throw HarnessException.Usage(string.Create(CultureInfo.InvariantCulture, $"--fm-max-renders {maxRenders}: at least 1, please"));
            }

            if (result.GetValue(noCache) && result.GetValue(cacheDir) is not null)
            {
                throw HarnessException.Usage("--no-cache and --cache-dir both given; --no-cache doesn't use a cache folder");
            }

            if (result.GetValue(remake) is not null && result.GetValue(fit) is not null)
            {
                throw HarnessException.Usage("--remake and --fit both given; --fit remakes the clips too");
            }

            if (result.GetValue(pick) is not null && (result.GetValue(remake) is not null || result.GetValue(fit) is not null))
            {
                throw HarnessException.Usage("--pick with --remake or --fit; pick the round first, then remake its rows");
            }

            if (result.GetValue(embeddings) is not null && (result.GetValue(remake) is not null || result.GetValue(fit) is not null || result.GetValue(pick) is not null))
            {
                throw HarnessException.Usage("--embeddings with --remake, --fit or --pick; --embeddings remakes the clips itself");
            }

            if (result.GetValue(holdout) is not null && (result.GetValue(remake) is not null || result.GetValue(fit) is not null || result.GetValue(pick) is not null || result.GetValue(embeddings) is not null))
            {
                throw HarnessException.Usage("--holdout with --remake, --fit, --pick or --embeddings; --holdout remakes its rows itself");
            }

            if (result.GetValue(models) is not null && result.GetValue(embeddings) is null)
            {
                throw HarnessException.Usage("--models without --embeddings; only --embeddings runs the models");
            }

            int pairCount = result.GetValue(pairs);
            if (pairCount is < 1 or > 100)
            {
                throw HarnessException.Usage(string.Create(CultureInfo.InvariantCulture, $"--pairs {pairCount}: between 1 and 100, please"));
            }

            int perKind = result.GetValue(maxPerKind);
            if (perKind is < 1 or > 100)
            {
                throw HarnessException.Usage(string.Create(CultureInfo.InvariantCulture, $"--max-per-kind {perKind}: between 1 and 100, please"));
            }

            string? kindList = result.GetValue(kinds);
            if ((kindList is not null || result.GetResult(maxPerKind) is { Implicit: false }) && result.GetValue(pick) is null)
            {
                throw HarnessException.Usage("--kinds or --max-per-kind without --pick; only --pick picks pairs");
            }

            string[] given = result.GetValue(inputs) ?? [];
            bool listening = result.GetValue(remake) is not null || result.GetValue(fit) is not null || result.GetValue(embeddings) is not null || result.GetValue(holdout) is not null;
            return execute(new HarnessOptions(
                given.Length > 0 ? given : listening ? ListeningInputs() : [DefaultCorpus],
                result.GetValue(output) ?? DefaultOutput,
                sampleRate,
                result.GetValue(libopenmpt),
                result.GetValue(furnace),
                result.GetValue(noFurnace),
                result.GetValue(deflemask),
                result.GetValue(saveReference),
                result.GetValue(verbose),
                songsAtOnce,
                result.GetValue(noDefleMask),
                clipSeconds,
                measureBalance,
                result.GetValue(fmFit),
                maxRenders,
                result.GetValue(psgFit),
                result.GetValue(pcmFit),
                result.GetValue(preMix),
                result.GetValue(ch3),
                result.GetValue(noCache),
                result.GetValue(cacheDir),
                result.GetValue(dropClues),
                result.GetValue(remake),
                result.GetValue(fit),
                result.GetValue(pick),
                pairCount,
                result.GetValue(embeddings),
                result.GetValue(models),
                result.GetValue(holdout),
                kindList is null ? null : PickKinds(kindList),
                perKind));
        });

        ParseResult parsed = root.Parse(args);
        return parsed.Errors.Count > 0 ? Errors(parsed) : parsed.Invoke(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
    }

    /// <summary>
    /// The listening commands' default inputs: the corpus, and the hold-out songs when their folder exists, so every row of the
    /// answers file finds its song.
    /// </summary>
    internal static string[] ListeningInputs() =>
        Directory.Exists(HoldoutSongs.DefaultFolder) ? [DefaultCorpus, HoldoutSongs.DefaultFolder] : [DefaultCorpus];

    /// <summary>The kinds a <c>--kinds</c> list names; a usage error for a name that is no kind.</summary>
    private static IReadOnlySet<PairKind> PickKinds(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => PairPicker.Parse(name) ?? throw HarnessException.Usage($"--kinds: {name} is no kind (simple, budget, no-dac, half-speed, no-layers, no-pre-mix)"))
            .ToHashSet() is { Count: > 0 } set
            ? set
            : throw HarnessException.Usage("--kinds: name at least one kind");

    private static int Errors(ParseResult parsed) =>
        throw HarnessException.Usage(string.Join("; ", parsed.Errors.Select(e => e.Message)));

    /// <summary>
    /// Sets up the renderers from <paramref name="options"/>, measures every song (one step each, reported to
    /// <paramref name="progress"/>), writes the reports and returns the exit code.
    /// </summary>
    internal static int Execute(HarnessOptions options, ILogger logger, IConversionProgress? progress = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyList<string> songs = FindSongs(options.Inputs);
        progress ??= NoProgress.Instance;
        if (options.Remake is { } answers)
        {
            if (!File.Exists(answers))
            {
                throw new HarnessException($"Not found: {answers}");
            }

            LibOpenMpt.UseDirectory(options.LibOpenMptDirectory);
            return new ListeningRemake(songs, options.OutputDirectory, new OpenMptRenderer(), logger).Run(answers, progress);
        }

        if (options.Fit is { } fitAnswers)
        {
            if (!File.Exists(fitAnswers))
            {
                throw new HarnessException($"Not found: {fitAnswers}");
            }

            LibOpenMpt.UseDirectory(options.LibOpenMptDirectory);
            IReadOnlySet<string> holdoutSongs = HoldoutSongs.In(HoldoutSongs.DefaultFolder);
            RemakeResult remade = new ListeningRemake(songs, options.OutputDirectory, new OpenMptRenderer(), logger).Remake(fitAnswers, progress, r => !holdoutSongs.Contains(r.Song));
            string? weights = ListeningFit.FindWeightsFile(Environment.CurrentDirectory);
            return ListeningFitRun.Run(remade, options.OutputDirectory, weights, DateOnly.FromDateTime(DateTime.Now), logger);
        }

        if (options.Embeddings is { } embeddingAnswers)
        {
            if (!File.Exists(embeddingAnswers))
            {
                throw new HarnessException($"Not found: {embeddingAnswers}");
            }

            LibOpenMpt.UseDirectory(options.LibOpenMptDirectory);
            IReadOnlySet<string> holdoutSongs = HoldoutSongs.In(HoldoutSongs.DefaultFolder);
            RemakeResult remade = new ListeningRemake(songs, options.OutputDirectory, new OpenMptRenderer(), logger).Remake(embeddingAnswers, progress, r => !holdoutSongs.Contains(r.Song));
            string folder = options.ModelsDirectory ?? EmbeddingModel.DefaultFolder;
            return EmbeddingsRun.Run(remade, options.OutputDirectory, folder, m => new OnnxListeningModel(m, folder), logger);
        }

        if (options.Holdout is { } holdoutAnswers)
        {
            if (!File.Exists(holdoutAnswers))
            {
                throw new HarnessException($"Not found: {holdoutAnswers}");
            }

            LibOpenMpt.UseDirectory(options.LibOpenMptDirectory);
            IReadOnlySet<string> holdoutSongs = HoldoutSongs.In(HoldoutSongs.DefaultFolder);
            RemakeResult remade = new ListeningRemake(songs, options.OutputDirectory, new OpenMptRenderer(), logger).Remake(holdoutAnswers, progress, r => holdoutSongs.Contains(r.Song));
            return HoldoutRun.Run(remade, options.OutputDirectory, Scoring.ListeningWeights.Weights, logger);
        }

        if (options.Pick is { } pickAnswers)
        {
            if (!File.Exists(pickAnswers))
            {
                throw new HarnessException($"Not found: {pickAnswers}");
            }

            LibOpenMpt.UseDirectory(options.LibOpenMptDirectory);
            return new PairPickRun(songs, options.OutputDirectory, new OpenMptRenderer(), logger).Run(pickAnswers, options.Pairs, DateOnly.FromDateTime(DateTime.Now), options.Jobs, progress, options.Kinds, options.MaxPerKind);
        }

        progress.Begin(new RunInfo(
            string.Join(", ", options.Inputs),
            RunInfo.HarnessFormat,
            "normal",
            [.. songs.Select((song, i) => new PhaseInfo(i + 1, CorpusRun.PhaseId(song), Path.GetFileNameWithoutExtension(song), string.Empty))],
            Math.Min(options.Jobs, songs.Count)));

        // Each step is one song: the history keeps seconds per song (docs/cli-output.md#the-harness).
        progress.SetSongSize(SongSize.Rows, 1);
        LibOpenMpt.UseDirectory(options.LibOpenMptDirectory);
        OpenMptRenderer reference = new();
        List<ICandidateRenderer> candidates = [new ModRendererCheck(options.SampleRate), new GenesisRendererCheck(options.SampleRate, logger)];
        if (!options.NoFurnace)
        {
            if (FurnaceRenderer.Find(options.FurnacePath) is { } furnace)
            {
                candidates.Add(new FurnaceRenderer(furnace, TimeSpan.FromMinutes(10)));
            }
            else
            {
                logger.Information("Furnace not found; DMFs are not rendered with it (--furnace sets the path)");
            }
        }

        string? deflemask = options.DefleMaskWavs ?? (Directory.Exists(DefaultDefleMaskWavs) ? DefaultDefleMaskWavs : null);
        if (deflemask is not null && !options.NoDefleMask)
        {
            candidates.Add(new DefleMaskExports(deflemask));
        }

        logger.Information(
            "Measuring {Count} songs, {Jobs} at a time, against {Reference} at {Rate} Hz; renderers: {Renderers}",
            songs.Count,
            options.Jobs,
            reference.Name,
            options.SampleRate,
            candidates.Count == 0 ? "none (lengths only)" : string.Join(", ", candidates.Select(c => c.Name)));

        bool fits = options.FmFit || options.PsgFit || options.PcmFit || options.PreMix || options.Ch3;
        CandidateCache? cache = fits && !options.NoCache ? new CandidateCache(options.CacheDirectory ?? CandidateCache.DefaultRoot, logger) : null;
        if (cache is not null)
        {
            logger.Information("Candidate cache: {Folder}", cache.Folder);
        }

        IReadOnlyList<SongResult> results = new CorpusRun(options.OutputDirectory, options.SampleRate, options.SaveReferenceWavs, reference, candidates, logger, options.ClipSeconds, options.Balance, options.FmFit ? new FmFitOptions(options.FmMaxRenders) : null, options.PsgFit, options.PcmFit, options.PreMix, options.Ch3 ? new FmFitOptions(options.FmMaxRenders) : null, cache, options.DropClues).Run(songs, options.Jobs, progress);
        return Report(results, options, reference.Name, logger);
    }

    /// <summary>Writes <c>scores.csv</c>, <c>durations.csv</c> and <c>report.md</c>, logs the summary and returns the exit code.</summary>
    internal static int Report(IReadOnlyList<SongResult> results, HarnessOptions options, string reference, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(options);
        string version = typeof(Mod2Xgm.Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";
        Directory.CreateDirectory(options.OutputDirectory);
        File.WriteAllText(Path.Combine(options.OutputDirectory, "scores.csv"), HarnessReport.ScoresCsv(results));
        File.WriteAllText(Path.Combine(options.OutputDirectory, "durations.csv"), HarnessReport.DurationsCsv(results));
        File.WriteAllText(Path.Combine(options.OutputDirectory, "loudness.csv"), HarnessReport.LoudnessCsv(results));
        File.WriteAllText(Path.Combine(options.OutputDirectory, "sample-analysis.csv"), HarnessReport.SampleAnalysisCsv(results));
        if (options.FmFit)
        {
            File.WriteAllText(Path.Combine(options.OutputDirectory, "fm-fit.csv"), HarnessReport.FmFitCsv(results));
        }

        if (options.PsgFit)
        {
            File.WriteAllText(Path.Combine(options.OutputDirectory, "psg-fit.csv"), HarnessReport.PsgFitCsv(results));
        }

        if (options.PcmFit)
        {
            File.WriteAllText(Path.Combine(options.OutputDirectory, "pcm-fit.csv"), HarnessReport.PcmFitCsv(results));
        }

        if (options.PreMix)
        {
            File.WriteAllText(Path.Combine(options.OutputDirectory, "pre-mix.csv"), HarnessReport.PreMixCsv(results));
            File.WriteAllText(Path.Combine(options.OutputDirectory, "voice-steals.csv"), HarnessReport.VoiceStealsCsv(results));
        }

        if (options.Ch3)
        {
            File.WriteAllText(Path.Combine(options.OutputDirectory, "locked-pairs.csv"), HarnessReport.LockedPairsCsv(results));
            File.WriteAllText(Path.Combine(options.OutputDirectory, "ch3-fit.csv"), HarnessReport.Ch3FitCsv(results));
        }

        if (options.DropClues)
        {
            File.WriteAllText(Path.Combine(options.OutputDirectory, "drop-clues.csv"), HarnessReport.DropCluesCsv(results));
            File.WriteAllText(Path.Combine(options.OutputDirectory, "track-levels.csv"), HarnessReport.TrackLevelsCsv(results));
        }

        if (options.ClipSeconds is not null)
        {
            File.WriteAllText(Path.Combine(options.OutputDirectory, "clip-scores.csv"), HarnessReport.ClipScoresCsv(results));
        }

        if (options.Balance)
        {
            File.WriteAllText(Path.Combine(options.OutputDirectory, "balance.csv"), HarnessReport.BalanceCsv(results));
            File.WriteAllText(Path.Combine(options.OutputDirectory, "balance-instruments.csv"), HarnessReport.BalanceInstrumentsCsv(results));
        }

        string report = Path.Combine(options.OutputDirectory, "report.md");
        File.WriteAllText(report, HarnessReport.Markdown(results, reference, version, options.SampleRate));

        int failed = results.Count(r => r.Error is not null || r.Duration?.Within == false || r.Scores.Any(ModRendererCheck.OverTheBar));
        logger.ForContext(new ReportRow("result", ("output", report), ("failed", failed), ("songs", results.Count))).Information(
            "Report: {ReportFile} ({Failed} of {Count} songs failed, are more than a tick off, or the in-app MOD renderer is over its bar)",
            report,
            failed,
            results.Count);
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Every <c>.mod</c> file named or inside a named folder, sorted by path so runs list songs in the same order.</summary>
    internal static IReadOnlyList<string> FindSongs(IReadOnlyList<string> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        List<string> songs = [];
        foreach (string input in inputs)
        {
            if (Directory.Exists(input))
            {
                songs.AddRange(Directory.EnumerateFiles(input).Where(f => f.EndsWith(".mod", StringComparison.OrdinalIgnoreCase)));
            }
            else if (File.Exists(input))
            {
                songs.Add(input);
            }
            else
            {
                throw new HarnessException($"Not found: {input}");
            }
        }

        if (songs.Count == 0)
        {
            throw new HarnessException("No .mod files found in " + string.Join(", ", inputs));
        }

        return [.. songs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }
}

/// <summary>What the command line asked for (docs/harness.md#options).</summary>
/// <param name="Inputs">MOD files or folders.</param>
/// <param name="OutputDirectory">Where everything goes.</param>
/// <param name="SampleRate">Render and scoring rate.</param>
/// <param name="LibOpenMptDirectory">Folder holding libopenmpt, or <c>null</c> for the search path.</param>
/// <param name="FurnacePath">Furnace, or <c>null</c> to look for it.</param>
/// <param name="NoFurnace">Skip Furnace.</param>
/// <param name="DefleMaskWavs">Folder of DefleMask exports, or <c>null</c> for the default when it exists.</param>
/// <param name="SaveReferenceWavs">Write the reference renders.</param>
/// <param name="Verbose">Debug logging.</param>
/// <param name="Jobs">Songs measured at once.</param>
/// <param name="NoDefleMask">Skip DefleMask exports, even in the default folder.</param>
/// <param name="ClipSeconds">Also score and write the first this many seconds, or <c>null</c>.</param>
/// <param name="Balance">Also measure the clips' instrument balance from stems.</param>
/// <param name="FmFit">Also fit FM patches to every sample (<c>fm-fit.csv</c>).</param>
/// <param name="FmMaxRenders">Most notes each step of a region's FM fit renders, or <c>null</c> for no limit.</param>
/// <param name="PsgFit">Also make PSG candidates for every sample (<c>psg-fit.csv</c>).</param>
/// <param name="PcmFit">Also make PCM candidates for every sample and target (<c>pcm-fit.csv</c>).</param>
/// <param name="PreMix">Also make pre-mixed PCM candidates for every song and target (<c>pre-mix.csv</c>, <c>voice-steals.csv</c>).</param>
/// <param name="Ch3">Also find locked pairs and make CH3 candidates for every song (<c>locked-pairs.csv</c>, <c>ch3-fit.csv</c>).</param>
/// <param name="NoCache">Make every candidate again: don't read or write the candidate cache.</param>
/// <param name="CacheDirectory">The candidate cache's folder, or <c>null</c> for the user's cache folder.</param>
/// <param name="DropClues">Also find which tracks of every song are cheap to drop (<c>drop-clues.csv</c>, <c>track-levels.csv</c>).</param>
/// <param name="Remake">An answers file whose clips to make again and check, instead of scoring, or <c>null</c>.</param>
/// <param name="Fit">An answers file to remake and fit the learned listening score on, instead of scoring, or <c>null</c>.</param>
/// <param name="Pick">An answers file whose next round to pick (<c>--pick</c>), instead of scoring, or <c>null</c>.</param>
/// <param name="Pairs">How many pairs <c>--pick</c> picks.</param>
/// <param name="Embeddings">An answers file to remake and compare the pretrained models on (<c>--embeddings</c>), instead of scoring, or <c>null</c>.</param>
/// <param name="ModelsDirectory">The exported models' folder, or <c>null</c> for <see cref="EmbeddingModel.DefaultFolder"/>.</param>
/// <param name="Holdout">An answers file whose hold-out rows to remake and score (<c>--holdout</c>), instead of scoring, or <c>null</c>.</param>
/// <param name="Kinds">The kinds of change <c>--pick</c> picks from (<c>--kinds</c>), or <c>null</c> for every kind.</param>
/// <param name="MaxPerKind">The most pairs of one kind <c>--pick</c> picks (<c>--max-per-kind</c>).</param>
internal sealed record HarnessOptions(
    IReadOnlyList<string> Inputs,
    string OutputDirectory,
    int SampleRate,
    string? LibOpenMptDirectory,
    string? FurnacePath,
    bool NoFurnace,
    string? DefleMaskWavs,
    bool SaveReferenceWavs,
    bool Verbose,
    int Jobs = 1,
    bool NoDefleMask = false,
    double? ClipSeconds = null,
    bool Balance = false,
    bool FmFit = false,
    int? FmMaxRenders = null,
    bool PsgFit = false,
    bool PcmFit = false,
    bool PreMix = false,
    bool Ch3 = false,
    bool NoCache = false,
    string? CacheDirectory = null,
    bool DropClues = false,
    string? Remake = null,
    string? Fit = null,
    string? Pick = null,
    int Pairs = 10,
    string? Embeddings = null,
    string? ModelsDirectory = null,
    string? Holdout = null,
    IReadOnlySet<PairKind>? Kinds = null,
    int MaxPerKind = PairPicker.MaxPerKind);
