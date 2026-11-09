using Mod2Xgm.Audio;
using Mod2Xgm.Cli;
using Mod2Xgm.Mod;
using Mod2Xgm.Output;

namespace Mod2Xgm.Tests.Cli;

/// <summary>
/// Tests for <see cref="CommandLineDefinition"/> and <see cref="OptionsBinder"/>: every option is parsed and bound
/// as docs/cli.md says, and every bad command line is a usage error naming the problem.
/// </summary>
public sealed class CommandLineTests
{
    /// <summary>A missing input is named in the usage error, so an agent can fix its call.</summary>
    [Fact]
    public void Invoke_WithoutInput_ThrowsUsageNamingInput()
    {
        // Act
        UsageException usage = Assert.Throws<UsageException>(() => CommandLineDefinition.Invoke([], _ => ExitCode.Success, TextWriter.Null, TextWriter.Null));

        // Assert
        Assert.Equal(["input.mod"], usage.Missing);
        Assert.Equal(["Required argument missing: <input.mod>."], usage.Problems);
    }

    /// <summary>A blank input is a missing input too.</summary>
    [Fact]
    public void Invoke_WithBlankInput_ThrowsUsageNamingInput()
    {
        // Act
        UsageException usage = Assert.Throws<UsageException>(() => CommandLineDefinition.Invoke(["  "], _ => ExitCode.Success, TextWriter.Null, TextWriter.Null));

        // Assert
        Assert.Equal(["input.mod"], usage.Missing);
    }

    /// <summary>A parse error is one line, not the parser's list over several lines, and no input is missing.</summary>
    [Fact]
    public void Invoke_WithUnknownFormat_ReportsOneLineProblem()
    {
        // Act
        UsageException usage = Assert.Throws<UsageException>(() => CommandLineDefinition.Invoke(["song.mod", "--format", "wav"], _ => ExitCode.Success, TextWriter.Null, TextWriter.Null));

        // Assert
        string problem = Assert.Single(usage.Problems);
        Assert.Equal("Argument 'wav' not recognized. Must be one of: 'dmf' 'xgm' 'xgm2'", problem);
        Assert.Empty(usage.Missing);
    }

    /// <summary><c>--help</c> still prints the help when the rest of the command line is wrong.</summary>
    [Fact]
    public void Invoke_WithHelpAndBadOption_PrintsHelp()
    {
        // Arrange
        using StringWriter output = new();

        // Act
        ExitCode code = CommandLineDefinition.Invoke(["--help", "--bogus"], _ => ExitCode.Success, output, TextWriter.Null);

        // Assert
        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("--progress", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Without <c>--progress</c> the mode is <c>fancy</c>.</summary>
    [Fact]
    public void Invoke_WithoutProgress_DefaultsToFancy()
    {
        // Act
        Outcome result = Run("song.mod");

        // Assert
        Assert.Equal(OutputMode.Fancy, result.Options?.Progress);
    }

    /// <summary>Every <c>--progress</c> mode is bound.</summary>
    [Theory]
    [InlineData("fancy", nameof(OutputMode.Fancy))]
    [InlineData("text", nameof(OutputMode.Text))]
    [InlineData("json", nameof(OutputMode.Json))]
    [InlineData("silent", nameof(OutputMode.Silent))]
    public void Invoke_WithProgressMode_BindsMode(string mode, string expected)
    {
        // Act
        Outcome result = Run("song.mod", "--progress", mode);

        // Assert
        Assert.Equal(Enum.Parse<OutputMode>(expected), result.Options?.Progress);
    }

    /// <summary>An unknown <c>--progress</c> mode is a usage error.</summary>
    [Fact]
    public void Invoke_WithUnknownProgressMode_ReturnsUsageError()
    {
        // Act
        Outcome result = Run("song.mod", "--progress", "auto");

        // Assert
        Assert.Equal(ExitCode.UsageError, result.Code);
    }

    /// <summary>With only an input, every option has the default docs/cli.md gives it.</summary>
    [Fact]
    public void Invoke_WithOnlyInput_BindsCliContractDefaults()
    {
        // Act
        Outcome result = Run("song.mod");

        // Assert
        Assert.Equal(ExitCode.Success, result.Code);
        ConversionOptions options = Assert.IsType<ConversionOptions>(result.Options);
        Assert.Equal("song.mod", options.InputPath);
        Assert.Equal(OutputFormat.Dmf, options.Format);
        Assert.Null(options.OutputPath);
        Assert.Equal(Region.Pal, options.InputRegion);
        Assert.Equal(Region.Pal, options.OutputRegion);
        Assert.Null(options.Player);
        Assert.Equal(ResamplingMode.Smooth, options.Resampler);
        Assert.Null(options.Mapping);
        Assert.Empty(options.IgnoreSamples);
        Assert.False(options.NoDac);
        Assert.False(options.NoConsolidate);
        Assert.Null(options.MaxPcmBytes);
        Assert.Equal(Quality.Normal, options.Quality);
        Assert.Null(options.TimeBudget);
        Assert.Equal(0, options.Seed);
        Assert.False(options.NoCache);
        Assert.Null(options.CacheDirectory);
        Assert.Equal(0, options.ReserveSfxChannels);
        Assert.False(options.InfoOnly);
        Assert.False(options.Force);
        Assert.False(options.Verbose);
        Assert.False(options.RenderWav);
        Assert.Empty(options.RequireFm);
        Assert.Empty(options.RequireDac);
        Assert.Empty(options.RequirePsg);
    }

    /// <summary>Every option given at once is bound to its property.</summary>
    [Fact]
    public void Invoke_WithEveryOption_BindsEachOne()
    {
        // Act
        Outcome result = Run(
            "song.mod",
            "--format", "xgm2",
            "--output", "out/level1.xgm2",
            "--input-region", "ntsc",
            "--output-region", "ntsc",
            "--player", "ust",
            "--resampler", "amiga",
            "--mapping", "simple",
            "--ignore-samples", "3,7",
            "--ignore-samples", "12-14,0x1F",
            "--no-dac",
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

        // Assert
        Assert.Equal(ExitCode.Success, result.Code);
        ConversionOptions options = Assert.IsType<ConversionOptions>(result.Options);
        Assert.Equal(OutputFormat.Xgm2, options.Format);
        Assert.Equal("out/level1.xgm2", options.OutputPath);
        Assert.Equal(Region.Ntsc, options.InputRegion);
        Assert.Equal(Region.Ntsc, options.OutputRegion);
        Assert.Equal(ModPlayer.UltimateSoundtracker, options.Player);
        Assert.Equal(ResamplingMode.Amiga, options.Resampler);
        Assert.Equal("simple", options.Mapping);
        int[] ignored = [3, 7, 12, 13, 14, 31];
        Assert.Equal(ignored, options.IgnoreSamples.Order());
        Assert.True(options.NoDac);
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

    /// <summary>The short aliases <c>-f</c>, <c>-o</c> and <c>-v</c> bind like the long names.</summary>
    [Fact]
    public void Invoke_WithShortAliases_BindsLikeLongNames()
    {
        // Act
        Outcome result = Run("song.mod", "-f", "xgm", "-o", "x.xgm", "-v");

        // Assert
        ConversionOptions options = Assert.IsType<ConversionOptions>(result.Options);
        Assert.Equal(OutputFormat.Xgm, options.Format);
        Assert.Equal("x.xgm", options.OutputPath);
        Assert.True(options.Verbose);
    }

    /// <summary>Each <c>--quality</c> value is bound (enum names as strings: the enums are internal, and public test methods can't take internal types).</summary>
    [Theory]
    [InlineData("fast", nameof(Quality.Fast))]
    [InlineData("normal", nameof(Quality.Normal))]
    [InlineData("best", nameof(Quality.Best))]
    public void Invoke_WithQuality_BindsQuality(string text, string expected)
    {
        // Act
        Outcome result = Run("song.mod", "--quality", text);

        // Assert
        Assert.Equal(Enum.Parse<Quality>(expected), result.Options?.Quality);
    }

    /// <summary>Each <c>--format</c> value is bound.</summary>
    [Theory]
    [InlineData("dmf", nameof(OutputFormat.Dmf))]
    [InlineData("xgm", nameof(OutputFormat.Xgm))]
    [InlineData("xgm2", nameof(OutputFormat.Xgm2))]
    public void Invoke_WithFormat_BindsFormat(string text, string expected)
    {
        // Act
        Outcome result = Run("song.mod", "--format", text);

        // Assert
        Assert.Equal(Enum.Parse<OutputFormat>(expected), result.Options?.Format);
    }

    /// <summary>The two regions are set independently, and the input region picks the Amiga clock and tick rate.</summary>
    [Theory]
    [InlineData("pal", "ntsc", nameof(Region.Pal), nameof(Region.Ntsc), 3_546_895.0, 1.0)]
    [InlineData("ntsc", "pal", nameof(Region.Ntsc), nameof(Region.Pal), 3_579_545.0, 1.2)]
    public void Invoke_WithRegions_BindsRegionsAndAmigaClock(string input, string output, string expectedInput, string expectedOutput, double expectedClockHz, double expectedTickScale)
    {
        // Act
        Outcome result = Run("song.mod", "--input-region", input, "--output-region", output);

        // Assert
        ConversionOptions options = Assert.IsType<ConversionOptions>(result.Options);
        Assert.Equal(Enum.Parse<Region>(expectedInput), options.InputRegion);
        Assert.Equal(Enum.Parse<Region>(expectedOutput), options.OutputRegion);
        Assert.Equal(expectedClockHz, options.AmigaClockHz);
        Assert.Equal(expectedTickScale, options.AmigaTickRateScale, 1e-12);
    }

    /// <summary><c>--player</c> takes protracker or ust; without it the player is chosen by the file's format.</summary>
    [Theory]
    [InlineData("protracker", nameof(ModPlayer.ProTracker2))]
    [InlineData("ust", nameof(ModPlayer.UltimateSoundtracker))]
    public void Invoke_WithPlayer_BindsPlayer(string text, string expected)
    {
        // Act
        Outcome result = Run("song.mod", "--player", text);

        // Assert
        Assert.Equal(Enum.Parse<ModPlayer>(expected), result.Options?.Player);
    }

    /// <summary><c>--resampler</c> takes smooth or amiga.</summary>
    [Theory]
    [InlineData("smooth", nameof(ResamplingMode.Smooth))]
    [InlineData("amiga", nameof(ResamplingMode.Amiga))]
    public void Invoke_WithResampler_BindsResampler(string text, string expected)
    {
        // Act
        Outcome result = Run("song.mod", "--resampler", text);

        // Assert
        Assert.Equal(Enum.Parse<ResamplingMode>(expected), result.Options?.Resampler);
    }

    /// <summary>No input is a usage error and nothing is converted.</summary>
    [Fact]
    public void Invoke_WithoutInput_ReturnsUsageErrorWithoutConverting()
    {
        // Act
        Outcome result = Run();

        // Assert
        Assert.Equal(ExitCode.UsageError, result.Code);
        Assert.Null(result.Options);
    }

    /// <summary>Unknown values, unknown options and a second input are usage errors and nothing is converted.</summary>
    [Theory]
    [InlineData("song.mod", "--format", "wav")]
    [InlineData("song.mod", "--input-region", "japan")]
    [InlineData("song.mod", "--output-region", "japan")]
    [InlineData("song.mod", "--region", "pal")]
    [InlineData("song.mod", "--player", "openmpt")]
    [InlineData("song.mod", "--mapping", "clever")]
    [InlineData("song.mod", "--resampler", "linear")]
    [InlineData("song.mod", "--quality", "ultra")]
    [InlineData("song.mod", "--seed", "abc")]
    [InlineData("song.mod", "--unknown")]
    [InlineData("a.mod", "b.mod")]
    public void Invoke_WithParseError_ReturnsUsageErrorWithoutConverting(params string[] args)
    {
        // Act
        Outcome result = Run(args);

        // Assert
        Assert.Equal(ExitCode.UsageError, result.Code);
        Assert.Null(result.Options);
    }

    /// <summary>A value the binder rejects is a usage error naming the option.</summary>
    [Theory]
    [InlineData("--ignore-samples", "32", "--ignore-samples")]
    [InlineData("--max-pcm-bytes", "lots", "--max-pcm-bytes")]
    [InlineData("--time-budget", "soon", "--time-budget")]
    [InlineData("--reserve-sfx-channels", "1", "only applies to --format xgm or xgm2")]
    public void Invoke_WithInvalidValue_ReturnsUsageErrorNamingOption(string option, string value, string expectedFragment)
    {
        // Act
        Outcome result = Run("song.mod", option, value);

        // Assert
        Assert.Equal(ExitCode.UsageError, result.Code);
        Assert.Null(result.Options);
        Assert.Contains(expectedFragment, result.Error, StringComparison.Ordinal);
    }

    /// <summary>Every binding error is reported at once, not only the first.</summary>
    [Fact]
    public void Invoke_WithSeveralInvalidValues_ReportsEveryOne()
    {
        // Act
        Outcome result = Run("song.mod", "--ignore-samples", "0", "--max-pcm-bytes", "0", "--time-budget", "0s");

        // Assert
        Assert.Equal(ExitCode.UsageError, result.Code);
        Assert.Contains("--ignore-samples", result.Error, StringComparison.Ordinal);
        Assert.Contains("--max-pcm-bytes", result.Error, StringComparison.Ordinal);
        Assert.Contains("--time-budget", result.Error, StringComparison.Ordinal);
    }

    /// <summary>A blank input is a usage error saying the input is required.</summary>
    [Fact]
    public void Invoke_WithBlankInput_ReturnsUsageErrorSayingInputRequired()
    {
        // Act
        Outcome result = Run("  ");

        // Assert
        Assert.Equal(ExitCode.UsageError, result.Code);
        Assert.Contains("input MOD file is required", result.Error, StringComparison.Ordinal);
    }

    /// <summary>Each format allows its own number of reserved SFX channels: none for DMF, up to 4 for XGM, 3 for XGM2.</summary>
    [Theory]
    [InlineData(nameof(OutputFormat.Dmf), 0, true)]
    [InlineData(nameof(OutputFormat.Dmf), 1, false)]
    [InlineData(nameof(OutputFormat.Xgm), 4, true)]
    [InlineData(nameof(OutputFormat.Xgm), 5, false)]
    [InlineData(nameof(OutputFormat.Xgm2), 3, true)]
    [InlineData(nameof(OutputFormat.Xgm2), 4, false)]
    [InlineData(nameof(OutputFormat.Xgm), -1, false)]
    public void ReserveSfxError_WithFormatAndChannelCount_ReturnsNullOnlyWhenAllowed(string format, int channels, bool valid)
    {
        // Act
        string? error = OptionsBinder.ReserveSfxError(Enum.Parse<OutputFormat>(format), channels);

        // Assert
        Assert.Equal(valid, error is null);
    }

    /// <summary><c>--help</c> prints the help, succeeds and converts nothing.</summary>
    [Fact]
    public void Invoke_WithHelp_PrintsHelpWithoutConverting()
    {
        // Act
        Outcome result = Run("--help");

        // Assert
        Assert.Equal(ExitCode.Success, result.Code);
        Assert.Null(result.Options);
        Assert.Contains("--ignore-samples", result.Output, StringComparison.Ordinal);
    }

    /// <summary>The conversion's exit code is returned as is.</summary>
    [Fact]
    public void Invoke_WhenConversionReturnsCode_ReturnsThatCode()
    {
        // Act
        ExitCode code = CommandLineDefinition.Invoke(["song.mod"], _ => ExitCode.DoesNotFit, TextWriter.Null, TextWriter.Null);

        // Assert
        Assert.Equal(ExitCode.DoesNotFit, code);
    }

    /// <summary>An exception from the conversion isn't caught here (<see cref="Program.Run(string[], Func{string[], ExitCode}, TextWriter, TextWriter, TerminalProbe)"/> handles it).</summary>
    [Fact]
    public void Invoke_WhenConversionThrows_PropagatesException()
    {
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            CommandLineDefinition.Invoke(["song.mod"], _ => throw new InvalidOperationException("boom"), TextWriter.Null, TextWriter.Null));
    }

    /// <summary>Without writers given, the console's are used and the run still succeeds.</summary>
    [Fact]
    public void Invoke_WithoutWriters_UsesDefaultsAndSucceeds()
    {
        // Act
        ExitCode code = CommandLineDefinition.Invoke(["song.mod"], _ => ExitCode.Success);

        // Assert
        Assert.Equal(ExitCode.Success, code);
    }

    /// <summary>The FM, DAC and PSG requirement lists take ranges, hex and repeats.</summary>
    [Fact]
    public void Invoke_WithRequireLists_BindsEachList()
    {
        // Act
        Outcome result = Run("song.mod", "--require-fm", "4,9-11", "--require-dac", "1", "--require-dac", "0x02", "--require-psg", "5-6");

        // Assert
        ConversionOptions options = Assert.IsType<ConversionOptions>(result.Options);
        int[] fm = [4, 9, 10, 11];
        int[] dac = [1, 2];
        Assert.Equal(fm, options.RequireFm.Order());
        Assert.Equal(dac, options.RequireDac.Order());
        Assert.Equal([5, 6], options.RequirePsg.Order());
    }

    /// <summary>A sample in both the FM and the DAC lists is a usage error listing every such sample.</summary>
    [Fact]
    public void Invoke_WithSamplesInFmAndDacLists_ReturnsUsageErrorListingThem()
    {
        // Act
        Outcome result = Run("song.mod", "--require-fm", "3,5-7", "--require-dac", "6,1,3");

        // Assert
        Assert.Equal(ExitCode.UsageError, result.Code);
        Assert.Null(result.Options);
        Assert.Contains("--require-fm and --require-dac both list samples 3, 6; a sample can be played by FM or the DAC, not both.", result.Error, StringComparison.Ordinal);
    }

    /// <summary>One conflicting sample is called "sample", not "samples".</summary>
    [Fact]
    public void Invoke_WithOneConflictingSample_UsesSingularWord()
    {
        // Act
        Outcome result = Run("song.mod", "--require-fm", "3", "--require-dac", "3");

        // Assert
        Assert.Contains("both list sample 3;", result.Error, StringComparison.Ordinal);
    }

    /// <summary>A sample can't be required in two lists at once; every pair is checked.</summary>
    [Theory]
    [InlineData("--require-fm", "--require-psg", "a sample can be played by FM or the PSG, not both.")]
    [InlineData("--require-dac", "--require-psg", "a sample can be played by the DAC or the PSG, not both.")]
    public void Invoke_WithSampleInTwoRequireLists_ReturnsUsageErrorWithReason(string first, string second, string reason)
    {
        // Act
        Outcome result = Run("song.mod", first, "4", second, "4");

        // Assert
        Assert.Equal(ExitCode.UsageError, result.Code);
        Assert.Contains($"{first} and {second} both list sample 4; {reason}", result.Error, StringComparison.Ordinal);
    }

    /// <summary><c>--cache-dir</c> names the candidate cache's folder.</summary>
    [Fact]
    public void Invoke_WithCacheDir_BindsCacheDirectory()
    {
        // Act
        Outcome result = Run("song.mod", "--cache-dir", "my cache");

        // Assert
        Assert.Equal("my cache", Assert.IsType<ConversionOptions>(result.Options).CacheDirectory);
    }

    /// <summary><c>--cache-dir</c> can't be combined with <c>--no-cache</c>.</summary>
    [Fact]
    public void Invoke_WithCacheDirAndNoCache_ReturnsUsageError()
    {
        // Act
        Outcome result = Run("song.mod", "--no-cache", "--cache-dir", "c");

        // Assert
        Assert.Equal(ExitCode.UsageError, result.Code);
        Assert.Contains("--no-cache and --cache-dir both given", result.Error, StringComparison.Ordinal);
    }

    /// <summary>An empty <c>--cache-dir</c> is a usage error.</summary>
    [Fact]
    public void Invoke_WithEmptyCacheDir_ReturnsUsageError()
    {
        // Act
        Outcome result = Run("song.mod", "--cache-dir", "");

        // Assert
        Assert.Equal(ExitCode.UsageError, result.Code);
        Assert.Contains("--cache-dir: a folder, please.", result.Error, StringComparison.Ordinal);
    }

    /// <summary><c>--no-dac</c> can be combined with <c>--require-fm</c> or <c>--require-psg</c>: those samples don't need the DAC.</summary>
    [Theory]
    [InlineData("--require-fm")]
    [InlineData("--require-psg")]
    public void Invoke_WithNoDacAndNonDacRequirement_BindsNoDac(string requireOption)
    {
        // Act
        Outcome result = Run("song.mod", "--no-dac", requireOption, "2");

        // Assert
        Assert.True(Assert.IsType<ConversionOptions>(result.Options).NoDac);
    }

    /// <summary><c>--no-dac</c> with <c>--require-dac</c> is a usage error: that sample would have nowhere to play.</summary>
    [Theory]
    [InlineData("3", "--no-dac and --require-dac both given (sample 3);")]
    [InlineData("3,1", "(samples 1, 3);")]
    public void Invoke_WithNoDacAndRequireDac_ReturnsUsageErrorListingSamples(string samples, string expected)
    {
        // Act
        Outcome result = Run("song.mod", "--no-dac", "--require-dac", samples);

        // Assert
        Assert.Equal(ExitCode.UsageError, result.Code);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
    }

    /// <summary>An ignored sample can't also be required.</summary>
    [Theory]
    [InlineData("--require-fm")]
    [InlineData("--require-dac")]
    [InlineData("--require-psg")]
    public void Invoke_WithIgnoredSampleRequired_ReturnsUsageError(string requireOption)
    {
        // Act
        Outcome result = Run("song.mod", "--ignore-samples", "8", requireOption, "8");

        // Assert
        Assert.Equal(ExitCode.UsageError, result.Code);
        Assert.Contains($"--ignore-samples and {requireOption} both list sample 8", result.Error, StringComparison.Ordinal);
    }

    /// <summary>A sample number above 31 in a requirement list is a usage error naming the option.</summary>
    [Theory]
    [InlineData("--require-fm")]
    [InlineData("--require-dac")]
    [InlineData("--require-psg")]
    public void Invoke_WithOutOfRangeRequiredSample_ReturnsUsageError(string option)
    {
        // Act
        Outcome result = Run("song.mod", option, "40");

        // Assert
        Assert.Equal(ExitCode.UsageError, result.Code);
        Assert.Contains($"{option}: Sample '40' is out of range", result.Error, StringComparison.Ordinal);
    }

    /// <summary><c>--render-wav</c> given alone asks for the default path.</summary>
    [Fact]
    public void Invoke_WithBareRenderWav_RendersToDefaultPath()
    {
        // Act
        Outcome result = Run("song.mod", "--render-wav");

        // Assert
        ConversionOptions options = Assert.IsType<ConversionOptions>(result.Options);
        Assert.True(options.RenderWav);
        Assert.Null(options.RenderWavPath);
        Assert.Equal("song.mod", options.InputPath);
    }

    /// <summary><c>--render-wav</c> given a value takes that path, and the input is still found.</summary>
    [Fact]
    public void Invoke_WithRenderWavPath_BindsPathAndInput()
    {
        // Act
        Outcome result = Run("--render-wav", "out.wav", "song.mod");

        // Assert
        ConversionOptions options = Assert.IsType<ConversionOptions>(result.Options);
        Assert.True(options.RenderWav);
        Assert.Equal("out.wav", options.RenderWavPath);
        Assert.Equal("song.mod", options.InputPath);
    }

    /// <summary>Parses <paramref name="args"/>; a usage error's problems become the outcome's error text.</summary>
    private static Outcome Run(params string[] args)
    {
        ConversionOptions? captured = null;
        using StringWriter output = new();
        using StringWriter error = new();
        try
        {
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
        catch (UsageException usage)
        {
            return new Outcome(usage.Code, captured, output.ToString(), usage.Message);
        }
    }

    /// <summary>What a parse produced: the exit code, the bound options (null when nothing was converted) and the text written.</summary>
    private sealed record Outcome(ExitCode Code, ConversionOptions? Options, string Output, string Error);
}
