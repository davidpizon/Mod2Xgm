using Mod2Xgm.Cli;
using Mod2Xgm.Tests.Mod;
using Serilog.Events;

namespace Mod2Xgm.Tests;

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
            RequirePcm = new HashSet<int> { 2, 1 },
        };

        ConversionCommand.Execute(options, sink.CreateLogger());

        string[] debug = [.. sink.Messages(LogEventLevel.Debug)];
        Assert.Contains(debug, m => m.Contains("PCM budget 131072 bytes", StringComparison.Ordinal));
        Assert.Contains(debug, m => m.Contains("Ignoring samples \"3,7\"", StringComparison.Ordinal));
        Assert.Contains(debug, m => m.Contains("Samples required as FM \"9\"", StringComparison.Ordinal));
        Assert.Contains(debug, m => m.Contains("Samples required as PCM \"1,2\"", StringComparison.Ordinal));
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
            RequirePcm = new HashSet<int> { 15 },
        };

        ConversionException error = Assert.Throws<ConversionException>(() => ConversionCommand.Execute(options, sink.CreateLogger()));

        Assert.Equal(ExitCode.UsageError, error.Code);
        Assert.Contains("--ignore-samples 16,20: this module has samples 1-15 only.", error.Message, StringComparison.Ordinal);
        Assert.Contains("--require-fm 31: this module has samples 1-15 only.", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("--require-pcm", error.Message, StringComparison.Ordinal);
        Assert.Contains(sink.Messages(LogEventLevel.Information), m => m.StartsWith(" 15  ", StringComparison.Ordinal));
    }
}
