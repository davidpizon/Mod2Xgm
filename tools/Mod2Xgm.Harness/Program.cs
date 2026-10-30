using System.CommandLine;
using System.Globalization;
using System.Reflection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Mod2Xgm.Harness;

/// <summary>
/// The accuracy harness's command line (docs/harness.md). Exit codes: 0 every song measured and within one tick,
/// 1 a song failed or its length is off by more than a tick, 2 the harness couldn't run (a tool is missing, bad options).
/// </summary>
internal static class Program
{
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
        LoggingLevelSwitch level = new(); // starts at Information
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(level)
            .WriteTo.Console(outputTemplate: Mod2Xgm.Program.ConsoleOutputTemplate, formatProvider: CultureInfo.InvariantCulture, standardErrorFromLevel: LogEventLevel.Warning)
            .CreateLogger();
        try
        {
            return Invoke(args, options =>
            {
                level.MinimumLevel = options.Verbose ? LogEventLevel.Debug : LogEventLevel.Information;
                return Execute(options, Log.Logger);
            });
        }
        catch (HarnessException ex)
        {
            Log.Error("{Message}", ex.Message);
            return 2;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>Parses <paramref name="args"/> and hands the options to <paramref name="execute"/>; a usage error exits 2.</summary>
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
        Option<int> jobs = new("--jobs", "-j") { Description = "Songs measured at once.", DefaultValueFactory = _ => DefaultJobs };
        Option<bool> verbose = new("--verbose", "-v") { Description = "Log every scoring step." };

        RootCommand root = new("Mod2Xgm accuracy harness: scores conversions against OpenMPT (docs/harness.md).")
        {
            inputs, output, rate, libopenmpt, furnace, noFurnace, deflemask, noDefleMask, saveReference, clip, balance, jobs, verbose,
        };
        root.SetAction(result =>
        {
            int sampleRate = result.GetValue(rate);
            if (sampleRate is < 8_000 or > 192_000)
            {
                throw new HarnessException(string.Create(CultureInfo.InvariantCulture, $"--rate {sampleRate}: between 8000 and 192000 Hz, please"));
            }

            int songsAtOnce = result.GetValue(jobs);
            if (songsAtOnce is < 1 or > 64)
            {
                throw new HarnessException(string.Create(CultureInfo.InvariantCulture, $"--jobs {songsAtOnce}: between 1 and 64, please"));
            }

            double? clipSeconds = result.GetValue(clip);
            if (clipSeconds is < 1 or > 600)
            {
                throw new HarnessException(string.Create(CultureInfo.InvariantCulture, $"--clip {clipSeconds}: between 1 and 600 seconds, please"));
            }

            bool measureBalance = result.GetValue(balance);
            if (measureBalance && clipSeconds is null)
            {
                throw new HarnessException("--balance measures the listening clips: give --clip too");
            }

            string[] given = result.GetValue(inputs) ?? [];
            return execute(new HarnessOptions(
                given.Length == 0 ? [DefaultCorpus] : given,
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
                measureBalance));
        });

        ParseResult parsed = root.Parse(args);
        return parsed.Errors.Count > 0 ? Errors(parsed) : parsed.Invoke(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
    }

    private static int Errors(ParseResult parsed)
    {
        foreach (System.CommandLine.Parsing.ParseError error in parsed.Errors)
        {
            Log.Error("{Message}", error.Message);
        }

        return 2;
    }

    /// <summary>Sets up the renderers from <paramref name="options"/>, measures every song, writes the reports and returns the exit code.</summary>
    internal static int Execute(HarnessOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        LibOpenMpt.UseDirectory(options.LibOpenMptDirectory);
        OpenMptRenderer reference = new();
        List<ICandidateRenderer> candidates = [new ModRendererCheck(options.SampleRate)];
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

        IReadOnlyList<string> songs = FindSongs(options.Inputs);
        logger.Information(
            "Measuring {Count} songs, {Jobs} at a time, against {Reference} at {Rate} Hz; renderers: {Renderers}",
            songs.Count,
            options.Jobs,
            reference.Name,
            options.SampleRate,
            candidates.Count == 0 ? "none (lengths only)" : string.Join(", ", candidates.Select(c => c.Name)));

        IReadOnlyList<SongResult> results = new CorpusRun(options.OutputDirectory, options.SampleRate, options.SaveReferenceWavs, reference, candidates, logger, options.ClipSeconds, options.Balance).Run(songs, options.Jobs);
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
        logger.Information("Report: {Report} ({Failed} of {Count} songs failed, are more than a tick off, or the in-app MOD renderer is over its bar)", report, failed, results.Count);
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
    bool Balance = false);
