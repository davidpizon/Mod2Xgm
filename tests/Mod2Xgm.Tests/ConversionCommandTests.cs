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
[Trait("Category", "Integration")]
public sealed class ConversionCommandTests
{
    /// <summary>An existing input with no output in the way converts successfully.</summary>
    [Fact]
    public void Execute_WithExistingInputAndFreeOutput_ReturnsSuccess()
    {
        // Arrange
        using TempDirectory temp = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod") };

        // Act
        ExitCode code = ConversionCommand.Execute(options);

        // Assert
        Assert.Equal(ExitCode.Success, code);
    }

    /// <summary>An input path that doesn't exist is InputNotFound.</summary>
    [Fact]
    public void Execute_WithMissingInput_ThrowsInputNotFound()
    {
        // Arrange
        using TempDirectory temp = new();
        ConversionOptions options = new() { InputPath = temp.Combine("missing.mod") };

        // Act & Assert
        Assert.Equal(ExitCode.InputNotFound, CodeOf(() => ConversionCommand.Execute(options)));
    }

    /// <summary>An input path that is a directory is InputNotFound.</summary>
    [Fact]
    public void Execute_WithDirectoryInput_ThrowsInputNotFound()
    {
        // Arrange
        using TempDirectory temp = new();
        ConversionOptions options = new() { InputPath = temp.Path };

        // Act & Assert
        Assert.Equal(ExitCode.InputNotFound, CodeOf(() => ConversionCommand.Execute(options)));
    }

    /// <summary>An existing output is not overwritten without <c>--force</c>, and the message names the option.</summary>
    [Fact]
    public void Execute_WhenOutputExistsWithoutForce_ThrowsOutputNotWritable()
    {
        // Arrange
        using TempDirectory temp = new();
        string input = temp.CreateFile("song.mod");
        temp.CreateFile("song.dmf");
        ConversionOptions options = new() { InputPath = input };

        // Act
        ConversionException error = Assert.Throws<ConversionException>(() => ConversionCommand.Execute(options));

        // Assert
        Assert.Equal(ExitCode.OutputNotWritable, error.Code);
        Assert.Contains("--force", error.Message, StringComparison.Ordinal);
    }

    /// <summary><c>--force</c> allows an existing output.</summary>
    [Fact]
    public void Execute_WhenOutputExistsWithForce_ReturnsSuccess()
    {
        // Arrange
        using TempDirectory temp = new();
        string input = temp.CreateFile("song.mod");
        temp.CreateFile("song.dmf");
        ConversionOptions options = new() { InputPath = input, Force = true };

        // Act
        ExitCode code = ConversionCommand.Execute(options);

        // Assert
        Assert.Equal(ExitCode.Success, code);
    }

    /// <summary><c>--info</c> writes nothing, so an existing output doesn't stop it.</summary>
    [Fact]
    public void Execute_WithInfoWhenOutputExists_ReturnsSuccess()
    {
        // Arrange
        using TempDirectory temp = new();
        string input = temp.CreateFile("song.mod");
        temp.CreateFile("song.dmf");
        ConversionOptions options = new() { InputPath = input, InfoOnly = true };

        // Act
        ExitCode code = ConversionCommand.Execute(options);

        // Assert
        Assert.Equal(ExitCode.Success, code);
    }

    /// <summary>The output may never be the input, even with <c>--force</c>.</summary>
    [Fact]
    public void Execute_WithOutputEqualToInput_ThrowsOutputNotWritable()
    {
        // Arrange
        using TempDirectory temp = new();
        string input = temp.CreateFile("song.mod");
        ConversionOptions options = new() { InputPath = input, OutputPath = input, Force = true };

        // Act & Assert
        Assert.Equal(ExitCode.OutputNotWritable, CodeOf(() => ConversionCommand.Execute(options)));
    }

    /// <summary>The output's directory must exist.</summary>
    [Fact]
    public void Execute_WithMissingOutputDirectory_ThrowsOutputNotWritable()
    {
        // Arrange
        using TempDirectory temp = new();
        ConversionOptions options = new()
        {
            InputPath = temp.CreateFile("song.mod"),
            OutputPath = Path.Combine(temp.Path, "missing", "song.dmf"),
        };

        // Act & Assert
        Assert.Equal(ExitCode.OutputNotWritable, CodeOf(() => ConversionCommand.Execute(options)));
    }

    /// <summary>The output path may not be a directory.</summary>
    [Fact]
    public void Execute_WithDirectoryOutput_ThrowsOutputNotWritable()
    {
        // Arrange
        using TempDirectory temp = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod"), OutputPath = temp.Path };

        // Act & Assert
        Assert.Equal(ExitCode.OutputNotWritable, CodeOf(() => ConversionCommand.Execute(options)));
    }

    /// <summary>The summary line says how many conversion notes there were.</summary>
    [Fact]
    public void Execute_WithCleanSong_SummaryReportsNoteCount()
    {
        // Arrange
        using TempDirectory temp = new();
        CollectingSink sink = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod") };

        // Act
        ConversionCommand.Execute(options, sink.CreateLogger());

        // Assert
        Assert.Contains(sink.Messages(LogEventLevel.Information), m => m.Contains("(0 conversion notes)", StringComparison.Ordinal));
    }

    /// <summary>Every run says whose rules it emulates and why: the file's default, or the --player choice.</summary>
    [Theory]
    [InlineData(false, null, "Emulating ProTracker 2 (the default for 31-sample files; --player ust changes it)")]
    [InlineData(true, null, "Emulating Ultimate Soundtracker (the default for 15-sample files; --player protracker changes it)")]
    [InlineData(true, nameof(ModPlayer.ProTracker2), "Emulating ProTracker 2 (chosen with --player)")]
    [InlineData(false, nameof(ModPlayer.UltimateSoundtracker), "Emulating Ultimate Soundtracker (chosen with --player)")]
    public void Execute_WithFileTypeAndPlayerOption_AnnouncesEmulatedPlayer(bool soundtracker, string? player, string expected)
    {
        // Arrange
        using TempDirectory temp = new();
        CollectingSink sink = new();
        byte[] bytes = new ModBuilder { Soundtracker = soundtracker, RestartPosition = soundtracker ? 120 : 127 }.WithSample(1, "kick", 64).Build();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod", bytes), InfoOnly = true, Player = player is null ? null : Enum.Parse<ModPlayer>(player) };

        // Act
        ConversionCommand.Execute(options, sink.CreateLogger());

        // Assert
        Assert.Contains(expected, sink.Messages(LogEventLevel.Information));
    }

    /// <summary>With <c>--info</c>, the summary says no file was written.</summary>
    [Fact]
    public void Execute_WithInfo_SummarySaysNoFileWritten()
    {
        // Arrange
        using TempDirectory temp = new();
        CollectingSink sink = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod"), InfoOnly = true };

        // Act
        ConversionCommand.Execute(options, sink.CreateLogger());

        // Assert
        Assert.Contains(sink.Messages(LogEventLevel.Information), m => m.Contains("no file written", StringComparison.Ordinal));
    }

    /// <summary>The options that shape the conversion are logged at Debug, sample lists sorted.</summary>
    [Fact]
    public void Execute_WithSampleOptions_LogsOptionsAtDebug()
    {
        // Arrange
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

        // Act
        ConversionCommand.Execute(options, sink.CreateLogger());

        // Assert
        string[] debug = [.. sink.Messages(LogEventLevel.Debug)];
        Assert.Contains(debug, m => m.Contains("PCM budget 131072 bytes", StringComparison.Ordinal));
        Assert.Contains(debug, m => m.Contains("Ignoring samples \"3,7\"", StringComparison.Ordinal));
        Assert.Contains(debug, m => m.Contains("Samples required as FM \"9\"", StringComparison.Ordinal));
        Assert.Contains(debug, m => m.Contains("Samples required as DAC \"1,2\"", StringComparison.Ordinal));
        Assert.Contains(debug, m => m.Contains("Samples required as PSG \"5\"", StringComparison.Ordinal));
    }

    /// <summary>With <c>--verbose</c> (Debug), the conversion logs each sample's analysis (its label, then its pitch regions), the locked pairs and the drop clues.</summary>
    [Fact]
    public void Execute_AtDebugLevel_LogsSampleAnalysis()
    {
        // Arrange
        using TempDirectory temp = new();
        CollectingSink sink = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod") };

        // Act
        ConversionCommand.Execute(options, sink.CreateLogger(LogEventLevel.Debug));

        // Assert
        string[] debug = [.. sink.Messages(LogEventLevel.Debug)];
        Assert.Contains(debug, m => m.StartsWith("Sample 1 \"kick\": ", StringComparison.Ordinal) && m.Contains("percussive", StringComparison.Ordinal));
        Assert.Contains(debug, m => m.StartsWith("Sample 1 \"kick\": 1 pitch regions: ", StringComparison.Ordinal));
        Assert.Contains(debug, m => m.EndsWith("locked pairs (candidates for CH3 special mode on DMF and XGM2)", StringComparison.Ordinal));
        Assert.Contains(debug, m => m.EndsWith("drop clues (nothing is dropped: the optimizer weighs them)", StringComparison.Ordinal));
    }

    /// <summary>At Information level no Debug line is written.</summary>
    [Fact]
    public void Execute_AtInformationLevel_HidesDebug()
    {
        // Arrange
        using TempDirectory temp = new();
        CollectingSink sink = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod") };

        // Act
        ConversionCommand.Execute(options, sink.CreateLogger(LogEventLevel.Information));

        // Assert
        Assert.Empty(sink.Messages(LogEventLevel.Debug));
    }

    /// <summary>Bytes that aren't a MOD are InvalidInput.</summary>
    [Fact]
    public void Execute_WithInvalidMod_ThrowsInvalidInput()
    {
        // Arrange
        using TempDirectory temp = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod", [1, 2, 3, 4]) };

        // Act & Assert
        Assert.Equal(ExitCode.InvalidInput, CodeOf(() => ConversionCommand.Execute(options)));
    }

    /// <summary>A file larger than any MOD is refused before it is read.</summary>
    [Fact]
    public void Execute_WithOversizedFile_ThrowsInvalidInput()
    {
        // Arrange
        using TempDirectory temp = new();
        string path = temp.Combine("huge.mod");
        using (FileStream stream = File.Create(path))
        {
            stream.SetLength(ConversionCommand.MaxInputBytes + 1);
        }

        ConversionOptions options = new() { InputPath = path };

        // Act
        ConversionException error = Assert.Throws<ConversionException>(() => ConversionCommand.Execute(options));

        // Assert
        Assert.Equal(ExitCode.InvalidInput, error.Code);
        Assert.Contains("larger than any MOD", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A file without read permission is InputNotFound (Unix only: Windows can't make a file unreadable this way).</summary>
    [Fact]
    public void Execute_WithUnreadableFile_ThrowsInputNotFound()
    {
        // Arrange
        if (OperatingSystem.IsWindows())
        {
            // Explicit guard (not Assert.SkipWhen) so the platform analyzer (CA1416) sees SetUnixFileMode is Unix-only.
            Assert.Skip("Unix file permissions are needed to make a file unreadable.");
            return;
        }

        using TempDirectory temp = new();
        string path = temp.CreateFile("song.mod");
        File.SetUnixFileMode(path, UnixFileMode.None);
        ConversionOptions options = new() { InputPath = path };
        try
        {
            // Act & Assert
            Assert.Equal(ExitCode.InputNotFound, CodeOf(() => ConversionCommand.Execute(options)));
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>The reader's one-line summary of the MOD is logged at Debug.</summary>
    [Fact]
    public void Execute_WithValidMod_LogsModSummaryAtDebug()
    {
        // Arrange
        using TempDirectory temp = new();
        CollectingSink sink = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod") };

        // Act
        ConversionCommand.Execute(options, sink.CreateLogger());

        // Assert
        Assert.Contains(sink.Messages(LogEventLevel.Debug), m => m.Contains("Read ProTracker31 MOD", StringComparison.Ordinal));
    }

    /// <summary>Report sections 1–2 are printed on every conversion, with or without <c>--info</c>, before the summary.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_WithOrWithoutInfo_PrintsReportBeforeSummary(bool infoOnly)
    {
        // Arrange
        using TempDirectory temp = new();
        CollectingSink sink = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod"), InfoOnly = infoOnly };

        // Act
        ConversionCommand.Execute(options, sink.CreateLogger());

        // Assert
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
    public void Execute_WithIgnoredSample_MarksItIgnoredInTable()
    {
        // Arrange
        using TempDirectory temp = new();
        CollectingSink sink = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod"), IgnoreSamples = new HashSet<int> { 1 } };

        // Act
        ConversionCommand.Execute(options, sink.CreateLogger());

        // Assert
        Assert.Contains(sink.Messages(LogEventLevel.Information), m => m.StartsWith(" 01  \"kick\"", StringComparison.Ordinal) && m.EndsWith("ignored", StringComparison.Ordinal));
    }

    /// <summary>The analysis hook gets every sample's analysis, how many notes the song sounds at once and its notes (the harness's PSG and PCM candidates use them).</summary>
    [Fact]
    public void Execute_WithAnalysisHook_PassesVoicesAndNotes()
    {
        // Arrange
        using TempDirectory temp = new();
        byte[] mod = new ModBuilder().WithSample(1, "kick", 64).WithCell(0, row: 0, channel: 0, sample: 1, period: 428).WithCell(0, row: 0, channel: 1, sample: 1, period: 428).Build();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod", mod), InfoOnly = true };
        SongAnalysis? found = null;

        // Act
        ConversionCommand.Execute(options, Logger.None, a => found = a);

        // Assert
        Assert.NotNull(found);
        Assert.Equal([0], found.Samples.Select(a => a.Sample));
        Assert.Equal(2, found.Voices.MostAtOnce);
        Assert.Equal(2, found.Voices.MostAtOnceOf(0));
        Assert.Equal(2, found.Notes.Count);
    }

    /// <summary>
    /// Sample numbers the module doesn't have (16–31 in a 15-sample file) are a usage error naming every option at fault,
    /// and the report is printed first so the table shows which slots exist.
    /// </summary>
    [Fact]
    public void Execute_WithSampleNumbersBeyondModule_ThrowsUsageErrorAfterReport()
    {
        // Arrange
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

        // Act
        ConversionException error = Assert.Throws<ConversionException>(() => ConversionCommand.Execute(options, sink.CreateLogger()));

        // Assert
        Assert.Equal(ExitCode.UsageError, error.Code);
        Assert.Contains("--ignore-samples 16,20: this module has samples 1-15 only.", error.Message, StringComparison.Ordinal);
        Assert.Contains("--require-fm 31: this module has samples 1-15 only.", error.Message, StringComparison.Ordinal);
        Assert.Contains("--require-psg 31: this module has samples 1-15 only.", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("--require-dac", error.Message, StringComparison.Ordinal);
        Assert.Contains(sink.Messages(LogEventLevel.Information), m => m.StartsWith(" 15  ", StringComparison.Ordinal));
    }

    /// <summary>A conversion writes a DMF next to the input that DefleMask can read: the song's title, its DAC sample.</summary>
    [Fact]
    public void Execute_WithDefaultFormat_WritesReadableDmf()
    {
        // Arrange
        using TempDirectory temp = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod") };

        // Act
        ExitCode code = ConversionCommand.Execute(options);

        // Assert
        Assert.Equal(ExitCode.Success, code);
        DmfFile file = DmfReader.Read(File.ReadAllBytes(temp.Combine("song.dmf")));
        Assert.Equal("test song", file.Name);
        Assert.Equal(DmfSystem.Genesis, file.System);
        Assert.StartsWith("kick ", Assert.Single(file.Samples).Name, StringComparison.Ordinal);
    }

    /// <summary><c>--output</c> names the file exactly, and <c>--force</c> replaces what is there.</summary>
    [Fact]
    public void Execute_WithOutputPathAndForce_ReplacesExistingFile()
    {
        // Arrange
        using TempDirectory temp = new();
        string output = temp.CreateFile("out.bin", [1, 2, 3]);
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod"), OutputPath = output, Force = true };

        // Act
        ConversionCommand.Execute(options);

        // Assert
        Assert.Equal("test song", DmfReader.Read(File.ReadAllBytes(output)).Name);
    }

    /// <summary><c>--info</c> converts in memory (section 3 and the notes are printed) but writes nothing.</summary>
    [Fact]
    public void Execute_WithInfo_PrintsOutputTableButWritesNoFile()
    {
        // Arrange
        using TempDirectory temp = new();
        CollectingSink sink = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod"), InfoOnly = true };

        // Act
        ConversionCommand.Execute(options, sink.CreateLogger());

        // Assert
        Assert.False(File.Exists(temp.Combine("song.dmf")));
        Assert.Contains(sink.Messages(LogEventLevel.Information), m => m.StartsWith("Output (DMF): ", StringComparison.Ordinal));
    }

    /// <summary>Section 3 comes after sections 1–2 and before the summary, with or without <c>--info</c>.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_WithOrWithoutInfo_PrintsSectionThreeAfterSampleTable(bool infoOnly)
    {
        // Arrange
        using TempDirectory temp = new();
        CollectingSink sink = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod"), InfoOnly = infoOnly };

        // Act
        ConversionCommand.Execute(options, sink.CreateLogger());

        // Assert
        List<string> info = [.. sink.Messages(LogEventLevel.Information)];
        int lastSample = info.FindIndex(m => m.StartsWith(" 31  ", StringComparison.Ordinal));
        int output = info.FindIndex(m => m.StartsWith("Output (DMF): ", StringComparison.Ordinal));
        int dac = info.FindIndex(m => m.StartsWith(" DAC 01  ← 01 \"kick\"", StringComparison.Ordinal));
        int summary = info.FindIndex(m => m.Contains("conversion notes)", StringComparison.Ordinal));
        Assert.True(lastSample >= 0 && lastSample < output && output < dac && dac < summary, string.Join("\n", info));
    }

    /// <summary>XGM and XGM2 can't be written yet: the run prints sections 1–2, then stops with a usage error and writes nothing.</summary>
    [Theory]
    [InlineData(nameof(OutputFormat.Xgm), "xgm")]
    [InlineData(nameof(OutputFormat.Xgm2), "xgm2")]
    public void Execute_WithXgmFormat_PrintsReportThenThrowsUsageError(string format, string extension)
    {
        // Arrange
        using TempDirectory temp = new();
        CollectingSink sink = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod"), Format = Enum.Parse<OutputFormat>(format) };

        // Act
        ConversionException error = Assert.Throws<ConversionException>(() => ConversionCommand.Execute(options, sink.CreateLogger()));

        // Assert
        Assert.Equal(ExitCode.UsageError, error.Code);
        Assert.Contains($"--format {extension} isn't available yet", error.Message, StringComparison.Ordinal);
        List<string> info = [.. sink.Messages(LogEventLevel.Information)];
        Assert.Contains("Title       : \"test song\"", info);
        Assert.Contains(info, m => m.StartsWith(" 01  \"kick\"", StringComparison.Ordinal));
        Assert.False(File.Exists(temp.Combine("song." + extension)));
    }

    /// <summary><c>--format xgm2</c> prints SGDK's extension note, exactly (AGENTS.md required behavior 5).</summary>
    [Fact]
    public void Execute_WithXgm2Format_PrintsSgdkExtensionNote()
    {
        // Arrange
        using TempDirectory temp = new();
        CollectingSink sink = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod"), Format = OutputFormat.Xgm2 };

        // Act
        Assert.Throws<ConversionException>(() => ConversionCommand.Execute(options, sink.CreateLogger()));

        // Assert
        Assert.Contains("NOTE: SGDK expects XGM2 files to use the .XGM file extension.", sink.Messages(LogEventLevel.Information));
    }

    /// <summary><c>--format xgm</c> doesn't print the XGM2 extension note.</summary>
    [Fact]
    public void Execute_WithXgmFormat_DoesNotPrintXgm2ExtensionNote()
    {
        // Arrange
        using TempDirectory temp = new();
        CollectingSink sink = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod"), Format = OutputFormat.Xgm };

        // Act
        Assert.Throws<ConversionException>(() => ConversionCommand.Execute(options, sink.CreateLogger()));

        // Assert
        Assert.DoesNotContain("NOTE: SGDK expects XGM2 files to use the .XGM file extension.", sink.Messages(LogEventLevel.Information));
    }

    /// <summary><c>fast</c> is the simple mapper, so nothing about the optimizer is said.</summary>
    [Fact]
    public void Execute_WithFastQuality_SaysNothingAboutOptimizer()
    {
        // Arrange
        using TempDirectory temp = new();
        CollectingSink sink = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod"), Quality = Quality.Fast, InfoOnly = true };

        // Act
        ConversionCommand.Execute(options, sink.CreateLogger());

        // Assert
        Assert.DoesNotContain(sink.Messages(LogEventLevel.Information), m => m.Contains("optimizer", StringComparison.Ordinal));
    }

    /// <summary>Until the optimizer exists, <c>normal</c> and <c>best</c> say they use the simple mapper.</summary>
    [Theory]
    [InlineData(nameof(Quality.Normal), "--quality normal: the optimizer isn't built yet (plan M9), so the simple mapper is used")]
    [InlineData(nameof(Quality.Best), "--quality best: the optimizer isn't built yet (plan M9), so the simple mapper is used")]
    public void Execute_WithOptimizerQuality_SaysSimpleMapperIsUsed(string quality, string expected)
    {
        // Arrange
        using TempDirectory temp = new();
        CollectingSink sink = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod"), Quality = Enum.Parse<Quality>(quality), InfoOnly = true };

        // Act
        ConversionCommand.Execute(options, sink.CreateLogger());

        // Assert
        Assert.Contains(expected, sink.Messages(LogEventLevel.Information));
    }

    /// <summary>An ignored sample's notes are left out of the DMF, with a conversion note saying how many.</summary>
    [Fact]
    public void Execute_WithIgnoredSample_LeavesItOutOfDmfWithNote()
    {
        // Arrange
        using TempDirectory temp = new();
        CollectingSink sink = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod"), IgnoreSamples = new HashSet<int> { 1 } };

        // Act
        ConversionCommand.Execute(options, sink.CreateLogger());

        // Assert
        Assert.Empty(DmfReader.Read(File.ReadAllBytes(temp.Combine("song.dmf"))).Samples);
        Assert.Contains(
            sink.Messages(LogEventLevel.Warning),
            m => m.Contains("kick", StringComparison.Ordinal) && m.EndsWith(": ignored (--ignore-samples), so its 1 notes are left out and their tracks are silent until the next note", StringComparison.Ordinal));
    }

    /// <summary>An output that can't be written (here a read-only file, with <c>--force</c>) is exit 4, after the report.</summary>
    [Fact]
    public void Execute_WithReadOnlyOutput_ThrowsOutputNotWritable()
    {
        // Arrange
        using TempDirectory temp = new();
        string output = temp.CreateFile("song.dmf", [0]);
        File.SetAttributes(output, FileAttributes.ReadOnly);
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod"), Force = true };
        try
        {
            // Act
            ConversionException error = Assert.Throws<ConversionException>(() => ConversionCommand.Execute(options));

            // Assert
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
    public void Execute_WithRenderWav_WritesWavNextToOutput()
    {
        // Arrange
        using TempDirectory temp = new();
        CollectingSink sink = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod"), RenderWav = true };

        // Act
        ExitCode code = ConversionCommand.Execute(options, sink.CreateLogger());

        // Assert
        Assert.Equal(ExitCode.Success, code);
        Assert.True(File.Exists(temp.Combine("song.dmf")));
        byte[] wav = File.ReadAllBytes(temp.Combine("song.genesis.wav"));
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Contains(sink.Messages(LogEventLevel.Information), m => m.StartsWith("Rendered ", StringComparison.Ordinal) && m.Contains("Dmf PCM", StringComparison.Ordinal));
    }

    /// <summary>With <c>--info</c> only the WAV is written, at the path given.</summary>
    [Fact]
    public void Execute_WithRenderWavAndInfo_WritesOnlyWav()
    {
        // Arrange
        using TempDirectory temp = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod"), InfoOnly = true, RenderWav = true, RenderWavPath = temp.Combine("x.wav") };

        // Act
        ConversionCommand.Execute(options);

        // Assert
        Assert.False(File.Exists(temp.Combine("song.dmf")));
        Assert.True(File.Exists(temp.Combine("x.wav")));
    }

    /// <summary>XGM and XGM2 can't be written yet, but their drivers can be heard: the WAV is written, no song file, and the run still fails for the writer.</summary>
    [Theory]
    [InlineData(nameof(OutputFormat.Xgm), "xgm", "Xgm PCM")]
    [InlineData(nameof(OutputFormat.Xgm2), "xgm2", "Xgm2 PCM")]
    public void Execute_WithRenderWavAndXgmFormat_WritesWavThenThrowsUsageError(string format, string extension, string driver)
    {
        // Arrange
        using TempDirectory temp = new();
        CollectingSink sink = new();
        string input = temp.CreateFile("song.mod");
        temp.CreateFile("song." + extension);
        ConversionOptions options = new() { InputPath = input, Format = Enum.Parse<OutputFormat>(format), RenderWav = true };

        // Act
        ConversionException error = Assert.Throws<ConversionException>(() => ConversionCommand.Execute(options, sink.CreateLogger()));

        // Assert
        Assert.Equal(ExitCode.UsageError, error.Code);
        Assert.Contains("Only the WAV render was written", error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(temp.Combine("song.genesis.wav")));
        Assert.Contains(sink.Messages(LogEventLevel.Information), m => m.Contains(driver, StringComparison.Ordinal));
    }

    /// <summary>An existing WAV isn't overwritten without <c>--force</c>.</summary>
    [Fact]
    public void Execute_WhenWavExistsWithoutForce_ThrowsOutputNotWritable()
    {
        // Arrange
        using TempDirectory temp = new();
        string input = temp.CreateFile("song.mod");
        temp.CreateFile("song.genesis.wav");
        ConversionOptions options = new() { InputPath = input, RenderWav = true };

        // Act & Assert
        Assert.Equal(ExitCode.OutputNotWritable, CodeOf(() => ConversionCommand.Execute(options)));
    }

    /// <summary><c>--force</c> replaces an existing WAV.</summary>
    [Fact]
    public void Execute_WhenWavExistsWithForce_ReturnsSuccess()
    {
        // Arrange
        using TempDirectory temp = new();
        string input = temp.CreateFile("song.mod");
        temp.CreateFile("song.genesis.wav");
        ConversionOptions options = new() { InputPath = input, RenderWav = true, Force = true };

        // Act
        ExitCode code = ConversionCommand.Execute(options);

        // Assert
        Assert.Equal(ExitCode.Success, code);
    }

    /// <summary>The WAV may never be the input, even with <c>--force</c>.</summary>
    [Fact]
    public void Execute_WithWavPathEqualToInput_ThrowsOutputNotWritable()
    {
        // Arrange
        using TempDirectory temp = new();
        string input = temp.CreateFile("song.mod");
        ConversionOptions options = new() { InputPath = input, RenderWav = true, RenderWavPath = input, Force = true };

        // Act & Assert
        Assert.Equal(ExitCode.OutputNotWritable, CodeOf(() => ConversionCommand.Execute(options)));
    }

    /// <summary>The WAV may never be the song file itself, even with <c>--force</c>.</summary>
    [Fact]
    public void Execute_WithWavPathEqualToSongFile_ThrowsOutputNotWritable()
    {
        // Arrange
        using TempDirectory temp = new();
        string input = temp.CreateFile("song.mod");
        ConversionOptions options = new() { InputPath = input, RenderWav = true, RenderWavPath = temp.Combine("song.dmf"), Force = true };

        // Act & Assert
        Assert.Equal(ExitCode.OutputNotWritable, CodeOf(() => ConversionCommand.Execute(options)));
    }

    /// <summary>The exit code of the <see cref="ConversionException"/> <paramref name="action"/> throws.</summary>
    private static ExitCode CodeOf(Action action) => Assert.Throws<ConversionException>(action).Code;
}
