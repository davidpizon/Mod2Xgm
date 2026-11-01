using Mod2Xgm.Analysis;
using Mod2Xgm.Cli;
using Mod2Xgm.Dmf;
using Mod2Xgm.Mod;
using Mod2Xgm.Tests.Dmf;
using Mod2Xgm.Tests.Mod;
using Serilog.Core;
using Serilog.Events;

namespace Mod2Xgm.Tests;

/// <summary>
/// Tests for <see cref="ConversionCommand"/>: path checks and exit codes, the report on every run, and the whole DMF
/// conversion (plan T5.6): the file written, <c>--info</c>, report section 3, formats not written yet, <c>--quality</c>
/// and <c>--ignore-samples</c>.
/// </summary>
public sealed class ConversionCommandTests
{
    private static ExitCode CodeOf(Action action) => Assert.Throws<ConversionException>(action).Code;

    [Fact]
    public void ExistingInputAndFreeOutputSucceeds()
    {
        using TempDirectory temp = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod") };

        Assert.Equal(ExitCode.Success, ConversionCommand.Execute(options));
    }

    [Fact]
    public void MissingInputIsInputNotFound()
    {
        using TempDirectory temp = new();
        ConversionOptions options = new() { InputPath = temp.Combine("missing.mod") };

        Assert.Equal(ExitCode.InputNotFound, CodeOf(() => ConversionCommand.Execute(options)));
    }

    [Fact]
    public void DirectoryInputIsInputNotFound()
    {
        using TempDirectory temp = new();
        ConversionOptions options = new() { InputPath = temp.Path };

        Assert.Equal(ExitCode.InputNotFound, CodeOf(() => ConversionCommand.Execute(options)));
    }

    [Fact]
    public void ExistingOutputIsNotOverwrittenWithoutForce()
    {
        using TempDirectory temp = new();
        string input = temp.CreateFile("song.mod");
        temp.CreateFile("song.dmf");

        ConversionException error = Assert.Throws<ConversionException>(() => ConversionCommand.Execute(new() { InputPath = input }));

        Assert.Equal(ExitCode.OutputNotWritable, error.Code);
        Assert.Contains("--force", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForceAllowsAnExistingOutput()
    {
        using TempDirectory temp = new();
        string input = temp.CreateFile("song.mod");
        temp.CreateFile("song.dmf");

        Assert.Equal(ExitCode.Success, ConversionCommand.Execute(new() { InputPath = input, Force = true }));
    }

    [Fact]
    public void InfoSkipsOutputChecks()
    {
        using TempDirectory temp = new();
        string input = temp.CreateFile("song.mod");
        temp.CreateFile("song.dmf");

        Assert.Equal(ExitCode.Success, ConversionCommand.Execute(new() { InputPath = input, InfoOnly = true }));
    }

    [Fact]
    public void OutputMayNeverBeTheInput()
    {
        using TempDirectory temp = new();
        string input = temp.CreateFile("song.mod");
        ConversionOptions options = new() { InputPath = input, OutputPath = input, Force = true };

        Assert.Equal(ExitCode.OutputNotWritable, CodeOf(() => ConversionCommand.Execute(options)));
    }

    [Fact]
    public void OutputDirectoryMustExist()
    {
        using TempDirectory temp = new();
        ConversionOptions options = new()
        {
            InputPath = temp.CreateFile("song.mod"),
            OutputPath = Path.Combine(temp.Path, "missing", "song.dmf"),
        };

        Assert.Equal(ExitCode.OutputNotWritable, CodeOf(() => ConversionCommand.Execute(options)));
    }

    [Fact]
    public void OutputMustNotBeADirectory()
    {
        using TempDirectory temp = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod"), OutputPath = temp.Path };

        Assert.Equal(ExitCode.OutputNotWritable, CodeOf(() => ConversionCommand.Execute(options)));
    }

    [Fact]
    public void SummaryReportsTheNoteCount()
    {
        using TempDirectory temp = new();
        CollectingSink sink = new();

        ConversionCommand.Execute(new() { InputPath = temp.CreateFile("song.mod") }, sink.CreateLogger());

        Assert.Contains(sink.Messages(LogEventLevel.Information), m => m.Contains("(0 conversion notes)", StringComparison.Ordinal));
    }

    /// <summary>Every run says whose rules it emulates and why: the file's default, or the --player choice.</summary>
    [Theory]
    [InlineData(false, null, "Emulating ProTracker 2 (the default for 31-sample files; --player ust changes it)")]
    [InlineData(true, null, "Emulating Ultimate Soundtracker (the default for 15-sample files; --player protracker changes it)")]
    [InlineData(true, "protracker", "Emulating ProTracker 2 (chosen with --player)")]
    [InlineData(false, "ust", "Emulating Ultimate Soundtracker (chosen with --player)")]
    public void ThePlayerIsAnnounced(bool soundtracker, string? player, string expected)
    {
        using TempDirectory temp = new();
        CollectingSink sink = new();
        byte[] bytes = new ModBuilder { Soundtracker = soundtracker, RestartPosition = soundtracker ? 120 : 127 }.WithSample(1, "kick", 64).Build();
        ConversionOptions options = new()
        {
            InputPath = temp.CreateFile("song.mod", bytes),
            InfoOnly = true,
            Player = player switch
            {
                "protracker" => ModPlayer.ProTracker2,
                "ust" => ModPlayer.UltimateSoundtracker,
                _ => null,
            },
        };

        ConversionCommand.Execute(options, sink.CreateLogger());

        Assert.Contains(expected, sink.Messages(LogEventLevel.Information));
    }

    [Fact]
    public void InfoSummarySaysNoFileIsWritten()
    {
        using TempDirectory temp = new();
        CollectingSink sink = new();

        ConversionCommand.Execute(new() { InputPath = temp.CreateFile("song.mod"), InfoOnly = true }, sink.CreateLogger());

        Assert.Contains(sink.Messages(LogEventLevel.Information), m => m.Contains("no file written", StringComparison.Ordinal));
    }

    [Fact]
    public void OptionsAreLoggedAtDebug()
    {
        using TempDirectory temp = new();
        CollectingSink sink = new();
        ConversionOptions options = new()
        {
            InputPath = temp.CreateFile("song.mod"),
            IgnoreSamples = new HashSet<int> { 7, 3 },
            RequireFm = new HashSet<int> { 9 },
            RequireDac = new HashSet<int> { 2, 1 },
            RequirePsg = new HashSet<int> { 5 },
        };

        ConversionCommand.Execute(options, sink.CreateLogger());

        string[] debug = [.. sink.Messages(LogEventLevel.Debug)];
        Assert.Contains(debug, m => m.Contains("PCM budget 131072 bytes", StringComparison.Ordinal));
        Assert.Contains(debug, m => m.Contains("Ignoring samples \"3,7\"", StringComparison.Ordinal));
        Assert.Contains(debug, m => m.Contains("Samples required as FM \"9\"", StringComparison.Ordinal));
        Assert.Contains(debug, m => m.Contains("Samples required as DAC \"1,2\"", StringComparison.Ordinal));
        Assert.Contains(debug, m => m.Contains("Samples required as PSG \"5\"", StringComparison.Ordinal));
    }

    /// <summary>With <c>--verbose</c> (Debug), the conversion logs each sample's analysis: its label, then its pitch regions.</summary>
    [Fact]
    public void VerboseLogsTheSampleAnalysis()
    {
        using TempDirectory temp = new();
        CollectingSink sink = new();

        ConversionCommand.Execute(new() { InputPath = temp.CreateFile("song.mod") }, sink.CreateLogger(LogEventLevel.Debug));

        string[] debug = [.. sink.Messages(LogEventLevel.Debug)];
        Assert.Contains(debug, m => m.StartsWith("Sample 1 \"kick\": ", StringComparison.Ordinal) && m.Contains("percussive", StringComparison.Ordinal));
        Assert.Contains(debug, m => m.StartsWith("Sample 1 \"kick\": 1 pitch regions: ", StringComparison.Ordinal));
    }

    [Fact]
    public void DebugIsHiddenAtInformationLevel()
    {
        using TempDirectory temp = new();
        CollectingSink sink = new();

        ConversionCommand.Execute(new() { InputPath = temp.CreateFile("song.mod") }, sink.CreateLogger(LogEventLevel.Information));

        Assert.Empty(sink.Messages(LogEventLevel.Debug));
    }

    [Fact]
    public void AnInvalidModIsInvalidInput()
    {
        using TempDirectory temp = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod", [1, 2, 3, 4]) };

        Assert.Equal(ExitCode.InvalidInput, CodeOf(() => ConversionCommand.Execute(options)));
    }

    [Fact]
    public void AnOversizedFileIsInvalidInput()
    {
        using TempDirectory temp = new();
        string path = temp.Combine("huge.mod");
        using (FileStream stream = File.Create(path))
        {
            stream.SetLength(ConversionCommand.MaxInputBytes + 1);
        }

        ConversionException error = Assert.Throws<ConversionException>(() => ConversionCommand.Execute(new() { InputPath = path }));

        Assert.Equal(ExitCode.InvalidInput, error.Code);
        Assert.Contains("larger than any MOD", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableFileIsInputNotFound()
    {
        if (OperatingSystem.IsWindows())
        {
            // Explicit guard (not Assert.SkipWhen) so the platform analyzer (CA1416) sees SetUnixFileMode is Unix-only.
            Assert.Skip("Unix file permissions are needed to make a file unreadable.");
            return;
        }

        using TempDirectory temp = new();
        string path = temp.CreateFile("song.mod");
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            Assert.Equal(ExitCode.InputNotFound, CodeOf(() => ConversionCommand.Execute(new() { InputPath = path })));
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void TheModSummaryIsLoggedAtDebug()
    {
        using TempDirectory temp = new();
        CollectingSink sink = new();

        ConversionCommand.Execute(new() { InputPath = temp.CreateFile("song.mod") }, sink.CreateLogger());

        Assert.Contains(sink.Messages(LogEventLevel.Debug), m => m.Contains("Read ProTracker31 MOD", StringComparison.Ordinal));
    }

    /// <summary>Report sections 1–2 are printed on every conversion, with or without <c>--info</c>, before the summary.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryConversionPrintsTheReport(bool infoOnly)
    {
        using TempDirectory temp = new();
        CollectingSink sink = new();

        ConversionCommand.Execute(new() { InputPath = temp.CreateFile("song.mod"), InfoOnly = infoOnly }, sink.CreateLogger());

        List<string> info = [.. sink.Messages(LogEventLevel.Information)];
        int title = info.IndexOf("Title       : \"test song\"");
        int kick = info.FindIndex(m => m.StartsWith(" 01  \"kick\"", StringComparison.Ordinal));
        int summary = info.FindIndex(m => m.Contains("conversion notes)", StringComparison.Ordinal));
        Assert.True(title >= 0 && title < kick && kick < summary, string.Join("\n", info));
        Assert.EndsWith("used", info[kick], StringComparison.Ordinal);
        Assert.Equal(1 + 31, info.Count(m => m.StartsWith(' ') && m.Length > 4 && (char.IsDigit(m[1]) || m[1] == '#')));
    }

    /// <summary>An ignored sample shows "ignored" in the table.</summary>
    [Fact]
    public void IgnoredSamplesAreMarked()
    {
        using TempDirectory temp = new();
        CollectingSink sink = new();

        ConversionCommand.Execute(new() { InputPath = temp.CreateFile("song.mod"), IgnoreSamples = new HashSet<int> { 1 } }, sink.CreateLogger());

        Assert.Contains(sink.Messages(LogEventLevel.Information), m => m.StartsWith(" 01  \"kick\"", StringComparison.Ordinal) && m.EndsWith("ignored", StringComparison.Ordinal));
    }

    /// <summary>The analysis hook gets every sample's analysis and how many notes the song sounds at once (the harness's PSG candidates use both).</summary>
    [Fact]
    public void TheAnalysisHookGetsTheVoices()
    {
        using TempDirectory temp = new();
        byte[] mod = new ModBuilder().WithSample(1, "kick", 64).WithCell(0, row: 0, channel: 0, sample: 1, period: 428).WithCell(0, row: 0, channel: 1, sample: 1, period: 428).Build();
        IReadOnlyList<SampleAnalysis>? analyses = null;
        SongVoices? voices = null;

        ConversionCommand.Execute(new() { InputPath = temp.CreateFile("song.mod", mod), InfoOnly = true }, Logger.None, (a, v) => (analyses, voices) = (a, v));

        Assert.Equal([0], analyses!.Select(a => a.Sample));
        Assert.Equal(2, voices!.MostAtOnce);
        Assert.Equal(2, voices.MostAtOnceOf(0));
    }

    /// <summary>
    /// Sample numbers the module doesn't have (16–31 in a 15-sample file) are a usage error naming every option at fault,
    /// and the report is printed first so the table shows which slots exist.
    /// </summary>
    [Fact]
    public void SampleNumbersBeyondTheModuleAreUsageErrors()
    {
        using TempDirectory temp = new();
        CollectingSink sink = new();
        byte[] soundtracker = new ModBuilder { Soundtracker = true }.WithSample(1, "kick", 64).WithCell(0, row: 0, channel: 0, sample: 1, period: 428).Build();
        ConversionOptions options = new()
        {
            InputPath = temp.CreateFile("song.mod", soundtracker),
            IgnoreSamples = new HashSet<int> { 3, 20, 16 },
            RequireFm = new HashSet<int> { 31 },
            RequireDac = new HashSet<int> { 15 },
            RequirePsg = new HashSet<int> { 31 },
        };

        ConversionException error = Assert.Throws<ConversionException>(() => ConversionCommand.Execute(options, sink.CreateLogger()));

        Assert.Equal(ExitCode.UsageError, error.Code);
        Assert.Contains("--ignore-samples 16,20: this module has samples 1-15 only.", error.Message, StringComparison.Ordinal);
        Assert.Contains("--require-fm 31: this module has samples 1-15 only.", error.Message, StringComparison.Ordinal);
        Assert.Contains("--require-psg 31: this module has samples 1-15 only.", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("--require-dac", error.Message, StringComparison.Ordinal);
        Assert.Contains(sink.Messages(LogEventLevel.Information), m => m.StartsWith(" 15  ", StringComparison.Ordinal));
    }

    /// <summary>A conversion writes a DMF next to the input that DefleMask can read: the song's title, its DAC sample.</summary>
    [Fact]
    public void AConversionWritesTheDmf()
    {
        using TempDirectory temp = new();

        Assert.Equal(ExitCode.Success, ConversionCommand.Execute(new() { InputPath = temp.CreateFile("song.mod") }));

        DmfFile file = DmfReader.Read(File.ReadAllBytes(temp.Combine("song.dmf")));
        Assert.Equal("test song", file.Name);
        Assert.Equal(DmfSystem.Genesis, file.System);
        Assert.StartsWith("kick ", Assert.Single(file.Samples).Name, StringComparison.Ordinal);
    }

    /// <summary><c>--output</c> names the file exactly, and <c>--force</c> replaces what is there.</summary>
    [Fact]
    public void ForceReplacesTheOutput()
    {
        using TempDirectory temp = new();
        string output = temp.CreateFile("out.bin", [1, 2, 3]);

        ConversionCommand.Execute(new() { InputPath = temp.CreateFile("song.mod"), OutputPath = output, Force = true });

        Assert.Equal("test song", DmfReader.Read(File.ReadAllBytes(output)).Name);
    }

    /// <summary><c>--info</c> converts in memory (section 3 and the notes are printed) but writes nothing.</summary>
    [Fact]
    public void InfoWritesNoFile()
    {
        using TempDirectory temp = new();
        CollectingSink sink = new();

        ConversionCommand.Execute(new() { InputPath = temp.CreateFile("song.mod"), InfoOnly = true }, sink.CreateLogger());

        Assert.False(File.Exists(temp.Combine("song.dmf")));
        Assert.Contains(sink.Messages(LogEventLevel.Information), m => m.StartsWith("Output (DMF): ", StringComparison.Ordinal));
    }

    /// <summary>Section 3 comes after sections 1–2 and before the summary, with or without <c>--info</c>.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SectionThreeFollowsTheSampleTable(bool infoOnly)
    {
        using TempDirectory temp = new();
        CollectingSink sink = new();

        ConversionCommand.Execute(new() { InputPath = temp.CreateFile("song.mod"), InfoOnly = infoOnly }, sink.CreateLogger());

        List<string> info = [.. sink.Messages(LogEventLevel.Information)];
        int lastSample = info.FindIndex(m => m.StartsWith(" 31  ", StringComparison.Ordinal));
        int output = info.FindIndex(m => m.StartsWith("Output (DMF): ", StringComparison.Ordinal));
        int dac = info.FindIndex(m => m.StartsWith(" DAC 01  ← 01 \"kick\"", StringComparison.Ordinal));
        int summary = info.FindIndex(m => m.Contains("conversion notes)", StringComparison.Ordinal));
        Assert.True(lastSample >= 0 && lastSample < output && output < dac && dac < summary, string.Join("\n", info));
    }

    /// <summary>
    /// XGM and XGM2 can't be written yet: the run prints sections 1–2, then stops with a usage error and writes nothing.
    /// <c>--format xgm2</c> still prints SGDK's extension note, exactly.
    /// </summary>
    [Theory]
    [InlineData(nameof(OutputFormat.Xgm), "xgm")]
    [InlineData(nameof(OutputFormat.Xgm2), "xgm2")]
    public void XgmFormatsPrintTheReportThenStop(string format, string extension)
    {
        using TempDirectory temp = new();
        CollectingSink sink = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod"), Format = Enum.Parse<OutputFormat>(format) };

        ConversionException error = Assert.Throws<ConversionException>(() => ConversionCommand.Execute(options, sink.CreateLogger()));

        Assert.Equal(ExitCode.UsageError, error.Code);
        Assert.Contains($"--format {extension} isn't available yet", error.Message, StringComparison.Ordinal);
        List<string> info = [.. sink.Messages(LogEventLevel.Information)];
        Assert.Contains("Title       : \"test song\"", info);
        Assert.Contains(info, m => m.StartsWith(" 01  \"kick\"", StringComparison.Ordinal));
        Assert.Equal(options.Format == OutputFormat.Xgm2, info.Contains("NOTE: SGDK expects XGM2 files to use the .XGM file extension."));
        Assert.False(File.Exists(temp.Combine("song." + extension)));
    }

    /// <summary>Until the optimizer exists, <c>normal</c> and <c>best</c> say they use the simple mapper; <c>fast</c> is the simple mapper.</summary>
    [Theory]
    [InlineData(nameof(Quality.Fast), null)]
    [InlineData(nameof(Quality.Normal), "--quality normal: the optimizer isn't built yet (plan M9), so the simple mapper is used")]
    [InlineData(nameof(Quality.Best), "--quality best: the optimizer isn't built yet (plan M9), so the simple mapper is used")]
    public void QualityExplainsTheMapperUsed(string quality, string? expected)
    {
        using TempDirectory temp = new();
        CollectingSink sink = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod"), Quality = Enum.Parse<Quality>(quality), InfoOnly = true };

        ConversionCommand.Execute(options, sink.CreateLogger());

        List<string> info = [.. sink.Messages(LogEventLevel.Information)];
        if (expected is null)
        {
            Assert.DoesNotContain(info, m => m.Contains("optimizer", StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains(expected, info);
        }
    }

    /// <summary>An ignored sample's notes are left out of the DMF, with a conversion note saying how many.</summary>
    [Fact]
    public void IgnoredSamplesAreLeftOutOfTheDmf()
    {
        using TempDirectory temp = new();
        CollectingSink sink = new();

        ConversionCommand.Execute(new() { InputPath = temp.CreateFile("song.mod"), IgnoreSamples = new HashSet<int> { 1 } }, sink.CreateLogger());

        Assert.Empty(DmfReader.Read(File.ReadAllBytes(temp.Combine("song.dmf"))).Samples);
        Assert.Contains(
            sink.Messages(LogEventLevel.Warning),
            m => m.Contains("kick", StringComparison.Ordinal) && m.EndsWith(": ignored (--ignore-samples), so its 1 notes are left out and their tracks are silent until the next note", StringComparison.Ordinal));
    }

    /// <summary>An output that can't be written (here a read-only file, with <c>--force</c>) is exit 4, after the report.</summary>
    [Fact]
    public void AnUnwritableOutputIsOutputNotWritable()
    {
        using TempDirectory temp = new();
        string output = temp.CreateFile("song.dmf", [0]);
        File.SetAttributes(output, FileAttributes.ReadOnly);
        try
        {
            ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod"), Force = true };

            ConversionException error = Assert.Throws<ConversionException>(() => ConversionCommand.Execute(options));

            Assert.Equal(ExitCode.OutputNotWritable, error.Code);
            Assert.Contains("can't be written", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetAttributes(output, FileAttributes.Normal);
        }
    }

    /// <summary><c>--render-wav</c> writes the DMF and, next to it, a WAV of the Genesis render, and says how long the render took.</summary>
    [Fact]
    public void RenderWavWritesAWavNextToTheOutput()
    {
        using TempDirectory temp = new();
        CollectingSink sink = new();

        ExitCode code = ConversionCommand.Execute(new() { InputPath = temp.CreateFile("song.mod"), RenderWav = true }, sink.CreateLogger());

        Assert.Equal(ExitCode.Success, code);
        Assert.True(File.Exists(temp.Combine("song.dmf")));
        byte[] wav = File.ReadAllBytes(temp.Combine("song.genesis.wav"));
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Contains(sink.Messages(LogEventLevel.Information), m => m.StartsWith("Rendered ", StringComparison.Ordinal) && m.Contains("Dmf PCM", StringComparison.Ordinal));
    }

    /// <summary>With <c>--info</c> only the WAV is written, at the path given.</summary>
    [Fact]
    public void RenderWavWithInfoWritesOnlyTheWav()
    {
        using TempDirectory temp = new();

        ConversionCommand.Execute(new() { InputPath = temp.CreateFile("song.mod"), InfoOnly = true, RenderWav = true, RenderWavPath = temp.Combine("x.wav") });

        Assert.False(File.Exists(temp.Combine("song.dmf")));
        Assert.True(File.Exists(temp.Combine("x.wav")));
    }

    /// <summary>XGM and XGM2 can't be written yet, but their drivers can be heard: the WAV is written, no song file, and the run still fails for the writer.</summary>
    [Theory]
    [InlineData(nameof(OutputFormat.Xgm), "xgm", "Xgm PCM")]
    [InlineData(nameof(OutputFormat.Xgm2), "xgm2", "Xgm2 PCM")]
    public void RenderWavPlaysTheXgmDrivers(string format, string extension, string driver)
    {
        using TempDirectory temp = new();
        CollectingSink sink = new();
        string input = temp.CreateFile("song.mod");
        temp.CreateFile("song." + extension);
        ConversionOptions options = new() { InputPath = input, Format = Enum.Parse<OutputFormat>(format), RenderWav = true };

        ConversionException error = Assert.Throws<ConversionException>(() => ConversionCommand.Execute(options, sink.CreateLogger()));

        Assert.Equal(ExitCode.UsageError, error.Code);
        Assert.Contains("Only the WAV render was written", error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(temp.Combine("song.genesis.wav")));
        Assert.Contains(sink.Messages(LogEventLevel.Information), m => m.Contains(driver, StringComparison.Ordinal));
    }

    /// <summary>The WAV follows the output file's rules: no overwrite without <c>--force</c>, never the input, never the song file itself.</summary>
    [Fact]
    public void RenderWavFollowsTheOutputRules()
    {
        using TempDirectory temp = new();
        string input = temp.CreateFile("song.mod");
        temp.CreateFile("song.genesis.wav");
        string dmf = temp.Combine("song.dmf");

        Assert.Equal(ExitCode.OutputNotWritable, CodeOf(() => ConversionCommand.Execute(new() { InputPath = input, RenderWav = true })));
        Assert.Equal(ExitCode.Success, ConversionCommand.Execute(new() { InputPath = input, RenderWav = true, Force = true }));
        Assert.Equal(ExitCode.OutputNotWritable, CodeOf(() => ConversionCommand.Execute(new() { InputPath = input, RenderWav = true, RenderWavPath = input, Force = true })));
        Assert.Equal(ExitCode.OutputNotWritable, CodeOf(() => ConversionCommand.Execute(new() { InputPath = input, RenderWav = true, RenderWavPath = dmf, Force = true })));
    }
}
