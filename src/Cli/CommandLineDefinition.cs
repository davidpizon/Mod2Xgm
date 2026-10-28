using System.CommandLine;

namespace Mod2Xgm.Cli;

/// <summary>
/// The <c>mod2xgm</c> command line (docs/cli.md). Values are parsed as text here and converted and validated by
/// <see cref="OptionsBinder"/>, so the rules live in one testable place.
/// </summary>
internal sealed class CommandLineDefinition
{
    public Argument<string> Input { get; } = new("input.mod")
    {
        Description = "Path to the MOD file to convert.",
    };

    public Option<string> Format { get; } = new Option<string>("--format", "-f")
    {
        Description = "Output format: dmf (.dmf), xgm (.xgm) or xgm2 (.xgm2).",
        DefaultValueFactory = _ => "dmf",
    }.AcceptOnlyFromAmong("dmf", "xgm", "xgm2");

    public Option<string?> Output { get; } = new("--output", "-o")
    {
        Description = "Output file path (default: next to the input, extension from --format).",
    };

    public Option<string> InputRegion { get; } = new Option<string>("--input-region")
    {
        Description = "Region of the Amiga the MOD was made on; sets the clock that turns periods into pitch.",
        DefaultValueFactory = _ => "pal",
    }.AcceptOnlyFromAmong("pal", "ntsc");

    public Option<string> OutputRegion { get; } = new Option<string>("--output-region")
    {
        Description = "Region of the Genesis the output is made for.",
        DefaultValueFactory = _ => "pal",
    }.AcceptOnlyFromAmong("pal", "ntsc");

    public Option<string?> Player { get; } = new Option<string?>("--player")
    {
        Description = "Whose rules to read the MOD by: protracker (ProTracker 2) or ust (Ultimate Soundtracker). Default: ust for 15-sample files, protracker otherwise.",
    }.AcceptOnlyFromAmong("protracker", "ust");

    public Option<string> Resampler { get; } = new Option<string>("--resampler")
    {
        Description = "How samples are played at a new rate: smooth (like OpenMPT) or amiga (Paula's steps, with their fizzy overtones).",
        DefaultValueFactory = _ => "smooth",
    }.AcceptOnlyFromAmong("smooth", "amiga");

    public Option<string?> Mapping { get; } = new("--mapping")
    {
        Description = "Channel-mapping strategy (see docs/channel-mapping.md).",
    };

    public Option<string[]> IgnoreSamples { get; } = new("--ignore-samples")
    {
        Description = "MOD samples to leave out, e.g. 3,7,12-14 or 0x1F. Repeatable.",
    };

    public Option<string[]> RequireFm { get; } = new("--require-fm")
    {
        Description = "MOD samples that must be played by FM (never as digitized PCM), e.g. 4,9-11. Repeatable.",
    };

    public Option<string[]> RequirePcm { get; } = new("--require-pcm")
    {
        Description = "MOD samples that must stay digitized PCM (never replaced by FM or PSG), e.g. 1,2. Repeatable.",
    };

    public Option<bool> NoDac { get; } = new("--no-dac")
    {
        Description = "Don't use the DAC (digitized PCM): every sample is played by FM or the PSG.",
    };

    public Option<bool> NoConsolidate { get; } = new("--no-consolidate")
    {
        Description = "Don't consolidate samples; fail (exit 6) if the song doesn't fit.",
    };

    public Option<string> MaxPcmBytes { get; } = new("--max-pcm-bytes")
    {
        Description = "PCM budget per song for XGM/XGM2, e.g. 128K or 1M.",
        DefaultValueFactory = _ => "128K",
    };

    public Option<string> Quality { get; } = new Option<string>("--quality")
    {
        Description = "fast (simple mapper), normal (multi-pass, ~5 min budget) or best (until no improvement).",
        DefaultValueFactory = _ => "normal",
    }.AcceptOnlyFromAmong("fast", "normal", "best");

    public Option<string?> TimeBudget { get; } = new("--time-budget")
    {
        Description = "Optimizer time limit, e.g. 90s or 5m (default depends on --quality).",
    };

    public Option<int> Seed { get; } = new("--seed")
    {
        Description = "Optimizer seed; the same input, options and seed give the same output.",
    };

    public Option<bool> NoCache { get; } = new("--no-cache")
    {
        Description = "Don't read or write the fitted-patch cache.",
    };

    public Option<int> ReserveSfxChannels { get; } = new("--reserve-sfx-channels")
    {
        Description = "PCM channels to leave free for game sound effects (XGM up to 4, XGM2 up to 3).",
    };

    public Option<bool> Info { get; } = new("--info")
    {
        Description = "Dry run: print the report and conversion notes but write no file.",
    };

    public Option<bool> Force { get; } = new("--force")
    {
        Description = "Overwrite an existing output file.",
    };

    public Option<bool> Verbose { get; } = new("--verbose", "-v")
    {
        Description = "Print extra detail.",
    };

    /// <summary>
    /// Builds the root command; <paramref name="execute"/> runs when parsing and binding succeed. Binding errors are
    /// written to <paramref name="error"/>.
    /// </summary>
    private RootCommand CreateRootCommand(Func<ConversionOptions, ExitCode> execute, TextWriter error)
    {
        RootCommand root = new("Convert Amiga MOD music for the Sega Genesis / Mega Drive (DefleMask DMF, SGDK XGM/XGM2).")
        {
            Input,
            Format,
            Output,
            InputRegion,
            OutputRegion,
            Player,
            Resampler,
            Mapping,
            IgnoreSamples,
            RequireFm,
            RequirePcm,
            NoDac,
            NoConsolidate,
            MaxPcmBytes,
            Quality,
            TimeBudget,
            Seed,
            NoCache,
            ReserveSfxChannels,
            Info,
            Force,
            Verbose,
        };

        root.SetAction(parseResult =>
        {
            OptionsBinder.Result bound = OptionsBinder.Bind(this, parseResult);
            if (bound.Options is null)
            {
                foreach (string message in bound.Errors)
                {
                    error.WriteLine(message);
                }

                return (int)ExitCode.UsageError;
            }

            return (int)execute(bound.Options);
        });

        return root;
    }

    /// <summary>Parses and runs <paramref name="args"/>; unexpected exceptions propagate to the caller.</summary>
    public static ExitCode Invoke(string[] args, Func<ConversionOptions, ExitCode> execute, TextWriter? output = null, TextWriter? error = null)
    {
        CommandLineDefinition definition = new();
        ParseResult parseResult = definition.CreateRootCommand(execute, error ?? Console.Error).Parse(args);

        InvocationConfiguration configuration = new()
        {
            // Let Program.Run turn unexpected exceptions into ExitCode.InternalError (5).
            EnableDefaultExceptionHandler = false,
        };

        if (output is not null)
        {
            configuration.Output = output;
        }

        if (error is not null)
        {
            configuration.Error = error;
        }

        return (ExitCode)parseResult.Invoke(configuration);
    }
}
