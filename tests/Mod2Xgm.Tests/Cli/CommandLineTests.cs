using Mod2Xgm.Cli;
using Mod2Xgm.Mod;

namespace Mod2Xgm.Tests.Cli;

public sealed class CommandLineTests
{
    private sealed record Outcome(ExitCode Code, ConversionOptions? Options, string Output, string Error);

    private static Outcome Run(params string[] args)
    {
        ConversionOptions? captured = null;
        using StringWriter output = new();
        using StringWriter error = new();
        ExitCode code = CommandLineDefinition.Invoke(
            args,
            options =>
            {
                captured = options;
                return ExitCode.Success;
            },
            output,
            error);
        return new Outcome(code, captured, output.ToString(), error.ToString());
    }

    [Fact]
    public void DefaultsMatchTheCliContract()
    {
        Outcome result = Run("song.mod");

        Assert.Equal(ExitCode.Success, result.Code);
        ConversionOptions options = Assert.IsType<ConversionOptions>(result.Options);
        Assert.Equal("song.mod", options.InputPath);
        Assert.Equal(OutputFormat.Dmf, options.Format);
        Assert.Null(options.OutputPath);
        Assert.Equal(Region.Pal, options.InputRegion);
        Assert.Equal(Region.Pal, options.OutputRegion);
        Assert.Null(options.Player);
        Assert.Null(options.Mapping);
        Assert.Empty(options.IgnoreSamples);
        Assert.False(options.NoConsolidate);
        Assert.Equal(128L * 1024, options.MaxPcmBytes);
        Assert.Equal(Quality.Normal, options.Quality);
        Assert.Null(options.TimeBudget);
        Assert.Equal(0, options.Seed);
        Assert.False(options.NoCache);
        Assert.Equal(0, options.ReserveSfxChannels);
        Assert.False(options.InfoOnly);
        Assert.False(options.Force);
        Assert.False(options.Verbose);
    }

    [Fact]
    public void EveryOptionIsBound()
    {
        Outcome result = Run(
            "song.mod",
            "--format", "xgm2",
            "--output", "out/level1.xgm2",
            "--input-region", "ntsc",
            "--output-region", "ntsc",
            "--player", "ust",
            "--mapping", "simple",
            "--ignore-samples", "3,7",
            "--ignore-samples", "12-14,0x1F",
            "--no-consolidate",
            "--max-pcm-bytes", "1M",
            "--quality", "best",
            "--time-budget", "90s",
            "--seed", "42",
            "--no-cache",
            "--reserve-sfx-channels", "2",
            "--info",
            "--force",
            "--verbose");

        Assert.Equal(ExitCode.Success, result.Code);
        ConversionOptions options = Assert.IsType<ConversionOptions>(result.Options);
        Assert.Equal(OutputFormat.Xgm2, options.Format);
        Assert.Equal("out/level1.xgm2", options.OutputPath);
        Assert.Equal(Region.Ntsc, options.InputRegion);
        Assert.Equal(Region.Ntsc, options.OutputRegion);
        Assert.Equal(ModPlayer.UltimateSoundtracker, options.Player);
        Assert.Equal("simple", options.Mapping);
        int[] ignored = [3, 7, 12, 13, 14, 31];
        Assert.Equal(ignored, options.IgnoreSamples.Order());
        Assert.True(options.NoConsolidate);
        Assert.Equal(1024L * 1024, options.MaxPcmBytes);
        Assert.Equal(Quality.Best, options.Quality);
        Assert.Equal(TimeSpan.FromSeconds(90), options.TimeBudget);
        Assert.Equal(42, options.Seed);
        Assert.True(options.NoCache);
        Assert.Equal(2, options.ReserveSfxChannels);
        Assert.True(options.InfoOnly);
        Assert.True(options.Force);
        Assert.True(options.Verbose);
    }

    [Fact]
    public void ShortAliasesWork()
    {
        Outcome result = Run("song.mod", "-f", "xgm", "-o", "x.xgm", "-v");

        ConversionOptions options = Assert.IsType<ConversionOptions>(result.Options);
        Assert.Equal(OutputFormat.Xgm, options.Format);
        Assert.Equal("x.xgm", options.OutputPath);
        Assert.True(options.Verbose);
    }

    [Theory]
    [InlineData("fast", nameof(Quality.Fast))]
    [InlineData("normal", nameof(Quality.Normal))]
    [InlineData("best", nameof(Quality.Best))]
    public void QualityValues(string text, string expected)
    {
        // Enum names as strings: the enums are internal, and public test methods can't take internal types.
        Assert.Equal(Enum.Parse<Quality>(expected), Run("song.mod", "--quality", text).Options?.Quality);
    }

    [Theory]
    [InlineData("dmf", nameof(OutputFormat.Dmf))]
    [InlineData("xgm", nameof(OutputFormat.Xgm))]
    [InlineData("xgm2", nameof(OutputFormat.Xgm2))]
    public void FormatValues(string text, string expected) =>
        Assert.Equal(Enum.Parse<OutputFormat>(expected), Run("song.mod", "--format", text).Options?.Format);

    /// <summary>The two regions are set independently, and the input region picks the Amiga clock.</summary>
    [Theory]
    [InlineData("pal", "ntsc", nameof(Region.Pal), nameof(Region.Ntsc), 3_546_895.0)]
    [InlineData("ntsc", "pal", nameof(Region.Ntsc), nameof(Region.Pal), 3_579_545.0)]
    public void RegionValues(string input, string output, string expectedInput, string expectedOutput, double expectedClockHz)
    {
        ConversionOptions options = Assert.IsType<ConversionOptions>(
            Run("song.mod", "--input-region", input, "--output-region", output).Options);

        Assert.Equal(Enum.Parse<Region>(expectedInput), options.InputRegion);
        Assert.Equal(Enum.Parse<Region>(expectedOutput), options.OutputRegion);
        Assert.Equal(expectedClockHz, options.AmigaClockHz);
    }

    /// <summary><c>--player</c> takes protracker or ust; without it the player is chosen by the file's format.</summary>
    [Theory]
    [InlineData("protracker", nameof(ModPlayer.ProTracker2))]
    [InlineData("ust", nameof(ModPlayer.UltimateSoundtracker))]
    public void PlayerValues(string text, string expected) =>
        Assert.Equal(Enum.Parse<ModPlayer>(expected), Run("song.mod", "--player", text).Options?.Player);

    [Fact]
    public void MissingInputIsAUsageError()
    {
        Outcome result = Run();
        Assert.Equal(ExitCode.UsageError, result.Code);
        Assert.Null(result.Options);
    }

    [Theory]
    [InlineData("song.mod", "--format", "wav")]
    [InlineData("song.mod", "--input-region", "japan")]
    [InlineData("song.mod", "--output-region", "japan")]
    [InlineData("song.mod", "--region", "pal")]
    [InlineData("song.mod", "--player", "openmpt")]
    [InlineData("song.mod", "--quality", "ultra")]
    [InlineData("song.mod", "--seed", "abc")]
    [InlineData("song.mod", "--unknown")]
    [InlineData("a.mod", "b.mod")]
    public void ParseErrorsAreUsageErrors(params string[] args)
    {
        Outcome result = Run(args);
        Assert.Equal(ExitCode.UsageError, result.Code);
        Assert.Null(result.Options);
    }

    [Theory]
    [InlineData("--ignore-samples", "32", "--ignore-samples")]
    [InlineData("--max-pcm-bytes", "lots", "--max-pcm-bytes")]
    [InlineData("--time-budget", "soon", "--time-budget")]
    [InlineData("--reserve-sfx-channels", "1", "only applies to --format xgm or xgm2")]
    public void InvalidValuesAreUsageErrors(string option, string value, string expectedFragment)
    {
        Outcome result = Run("song.mod", option, value);

        Assert.Equal(ExitCode.UsageError, result.Code);
        Assert.Null(result.Options);
        Assert.Contains(expectedFragment, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AllBindingErrorsAreReported()
    {
        Outcome result = Run("song.mod", "--ignore-samples", "0", "--max-pcm-bytes", "0", "--time-budget", "0s");

        Assert.Equal(ExitCode.UsageError, result.Code);
        Assert.Contains("--ignore-samples", result.Error, StringComparison.Ordinal);
        Assert.Contains("--max-pcm-bytes", result.Error, StringComparison.Ordinal);
        Assert.Contains("--time-budget", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyInputIsAUsageError()
    {
        Outcome result = Run("  ");
        Assert.Equal(ExitCode.UsageError, result.Code);
        Assert.Contains("input MOD file is required", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(OutputFormat.Dmf), 0, true)]
    [InlineData(nameof(OutputFormat.Dmf), 1, false)]
    [InlineData(nameof(OutputFormat.Xgm), 4, true)]
    [InlineData(nameof(OutputFormat.Xgm), 5, false)]
    [InlineData(nameof(OutputFormat.Xgm2), 3, true)]
    [InlineData(nameof(OutputFormat.Xgm2), 4, false)]
    [InlineData(nameof(OutputFormat.Xgm), -1, false)]
    public void ReserveSfxChannelLimits(string format, int channels, bool valid) =>
        Assert.Equal(valid, OptionsBinder.ReserveSfxError(Enum.Parse<OutputFormat>(format), channels) is null);

    [Fact]
    public void HelpSucceedsWithoutConverting()
    {
        Outcome result = Run("--help");

        Assert.Equal(ExitCode.Success, result.Code);
        Assert.Null(result.Options);
        Assert.Contains("--ignore-samples", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ExitCodeFromTheConversionIsReturned()
    {
        ExitCode code = CommandLineDefinition.Invoke(["song.mod"], _ => ExitCode.DoesNotFit, TextWriter.Null, TextWriter.Null);
        Assert.Equal(ExitCode.DoesNotFit, code);
    }

    [Fact]
    public void ExceptionsFromTheConversionPropagate()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CommandLineDefinition.Invoke(["song.mod"], _ => throw new InvalidOperationException("boom"), TextWriter.Null, TextWriter.Null));
    }

    [Fact]
    public void DefaultWritersAreUsedWhenNoneAreGiven() =>
        Assert.Equal(ExitCode.Success, CommandLineDefinition.Invoke(["song.mod"], _ => ExitCode.Success));

    [Fact]
    public void RequiredFmAndPcmListsAreBound()
    {
        Outcome result = Run("song.mod", "--require-fm", "4,9-11", "--require-pcm", "1", "--require-pcm", "0x02");

        ConversionOptions options = Assert.IsType<ConversionOptions>(result.Options);
        int[] fm = [4, 9, 10, 11];
        int[] pcm = [1, 2];
        Assert.Equal(fm, options.RequireFm.Order());
        Assert.Equal(pcm, options.RequirePcm.Order());
    }

    [Fact]
    public void RequiredListsDefaultToEmpty()
    {
        ConversionOptions options = Assert.IsType<ConversionOptions>(Run("song.mod").Options);

        Assert.Empty(options.RequireFm);
        Assert.Empty(options.RequirePcm);
    }

    [Fact]
    public void ASampleCannotBeBothFmAndPcm()
    {
        Outcome result = Run("song.mod", "--require-fm", "3,5-7", "--require-pcm", "6,1,3");

        Assert.Equal(ExitCode.UsageError, result.Code);
        Assert.Null(result.Options);
        Assert.Contains("--require-fm and --require-pcm both list samples 3, 6; a sample can be FM or PCM, not both.", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ASingleConflictUsesTheSingularWord()
    {
        Outcome result = Run("song.mod", "--require-fm", "3", "--require-pcm", "3");

        Assert.Contains("both list sample 3;", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--require-fm")]
    [InlineData("--require-pcm")]
    public void AnIgnoredSampleCannotBeRequired(string requireOption)
    {
        Outcome result = Run("song.mod", "--ignore-samples", "8", requireOption, "8");

        Assert.Equal(ExitCode.UsageError, result.Code);
        Assert.Contains($"--ignore-samples and {requireOption} both list sample 8", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--require-fm")]
    [InlineData("--require-pcm")]
    public void InvalidRequiredListsAreUsageErrors(string option)
    {
        Outcome result = Run("song.mod", option, "40");

        Assert.Equal(ExitCode.UsageError, result.Code);
        Assert.Contains($"{option}: Sample '40' is out of range", result.Error, StringComparison.Ordinal);
    }
}
