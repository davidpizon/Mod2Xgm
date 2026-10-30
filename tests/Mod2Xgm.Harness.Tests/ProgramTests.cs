using Serilog.Core;

namespace Mod2Xgm.Harness.Tests;

/// <summary>Tests for the harness command line (<see cref="Program"/>): options, song lists, reports and exit codes.</summary>
public sealed class ProgramTests
{
    private static HarnessOptions Parse(params string[] args)
    {
        HarnessOptions? parsed = null;
        Assert.Equal(7, Program.Invoke(args, options =>
        {
            parsed = options;
            return 7;
        }));
        return Assert.IsType<HarnessOptions>(parsed);
    }

    /// <summary>With no arguments the corpus is measured into artifacts/harness at 44.1 kHz, with every renderer found.</summary>
    [Fact]
    public void DefaultsMeasureTheCorpus()
    {
        HarnessOptions options = Parse();

        Assert.Equal([Program.DefaultCorpus], options.Inputs);
        Assert.Equal(Program.DefaultOutput, options.OutputDirectory);
        Assert.Equal(44_100, options.SampleRate);
        Assert.Null(options.LibOpenMptDirectory);
        Assert.Null(options.FurnacePath);
        Assert.False(options.NoFurnace);
        Assert.Null(options.DefleMaskWavs);
        Assert.False(options.SaveReferenceWavs);
        Assert.False(options.Verbose);
        Assert.False(options.NoDefleMask);
        Assert.Null(options.ClipSeconds);
        Assert.Equal(Program.DefaultJobs, options.Jobs);
        Assert.InRange(options.Jobs, 1, 4);
    }

    /// <summary>Every option reaches <see cref="HarnessOptions"/>.</summary>
    [Fact]
    public void EveryOptionIsRead()
    {
        HarnessOptions options = Parse(
            "a.mod", "songs", "-o", "out", "--rate", "22050", "--libopenmpt", "lib", "--furnace", "f.exe", "--no-furnace",
            "--deflemask-wavs", "wavs", "--no-deflemask", "--save-reference-wavs", "--clip", "30", "-j", "3", "-v");

        Assert.Equal(["a.mod", "songs"], options.Inputs);
        Assert.Equal("out", options.OutputDirectory);
        Assert.Equal(22_050, options.SampleRate);
        Assert.Equal("lib", options.LibOpenMptDirectory);
        Assert.Equal("f.exe", options.FurnacePath);
        Assert.True(options.NoFurnace);
        Assert.Equal("wavs", options.DefleMaskWavs);
        Assert.True(options.NoDefleMask);
        Assert.True(options.SaveReferenceWavs);
        Assert.Equal(30, options.ClipSeconds);
        Assert.True(options.Verbose);
        Assert.Equal(3, options.Jobs);
    }

    /// <summary>An unknown option, or a rate, job count or clip length out of range, is a usage error.</summary>
    [Fact]
    public void BadOptionsAreUsageErrors()
    {
        Assert.Equal(2, Program.Invoke(["--no-such-option"], _ => 0));
        Assert.Throws<HarnessException>(() => Program.Invoke(["--rate", "100"], _ => 0));
        Assert.Throws<HarnessException>(() => Program.Invoke(["--jobs", "0"], _ => 0));
        Assert.Throws<HarnessException>(() => Program.Invoke(["--clip", "0.5"], _ => 0));
    }

    /// <summary>Songs are the .mod files named or inside folders named, once each, sorted; nothing found is an error.</summary>
    [Fact]
    public void SongsAreFoundInFilesAndFolders()
    {
        using TempDirectory temp = new();
        Directory.CreateDirectory(temp.Combine("songs"));
        File.WriteAllBytes(temp.Combine("songs", "b.MOD"), []);
        File.WriteAllBytes(temp.Combine("songs", "a.mod"), []);
        File.WriteAllBytes(temp.Combine("songs", "notes.txt"), []);
        Directory.CreateDirectory(temp.Combine("empty"));

        IReadOnlyList<string> songs = Program.FindSongs([temp.Combine("songs"), temp.Combine("songs", "a.mod")]);

        Assert.Equal([temp.Combine("songs", "a.mod"), temp.Combine("songs", "b.MOD")], songs);
        Assert.Throws<HarnessException>(() => Program.FindSongs([temp.Combine("missing")]));
        Assert.Throws<HarnessException>(() => Program.FindSongs([temp.Combine("empty")]));
    }

    /// <summary>
    /// The report files are written, clip-scores.csv only with --clip; the exit code is 1 when a song failed or is more
    /// than a tick off.
    /// </summary>
    [Fact]
    public void ReportWritesFilesAndSetsTheExitCode()
    {
        using TempDirectory temp = new();
        HarnessOptions options = new([], temp.Combine("out"), 8000, null, null, true, null, false, false);
        SongResult good = new("good", new DurationCheck(10, 10, 10, 0.02, null, false), 0, [], null);
        SongResult off = new("off", new DurationCheck(10, 10, 11, 0.02, null, false), 0, [], null);
        SongResult failed = new("failed", null, 0, [], "broken");

        Assert.Equal(0, Program.Report([good], options, "ref", Logger.None));
        Assert.True(File.Exists(temp.Combine("out", "scores.csv")));
        Assert.True(File.Exists(temp.Combine("out", "durations.csv")));
        Assert.False(File.Exists(temp.Combine("out", "clip-scores.csv")));
        Assert.Contains("Converter: Mod2Xgm 0.", File.ReadAllText(temp.Combine("out", "report.md")), StringComparison.Ordinal);
        Assert.Equal(1, Program.Report([good, off], options, "ref", Logger.None));
        Assert.Equal(1, Program.Report([good, failed], options, "ref", Logger.None));
        Assert.Equal(0, Program.Report([good], options with { ClipSeconds = 30 }, "ref", Logger.None));
        Assert.True(File.Exists(temp.Combine("out", "clip-scores.csv")));
    }

    /// <summary>A whole run with the real libopenmpt and no candidate renderers (skipped where libopenmpt isn't installed).</summary>
    [Fact]
    public void ARealRunMeasuresTheLength()
    {
        try
        {
            LibOpenMpt.Version();
        }
        catch (HarnessException ex)
        {
            Assert.Skip("libopenmpt isn't installed: " + ex.Message);
        }

        using TempDirectory temp = new();
        Directory.CreateDirectory(temp.Combine("no-exports"));
        HarnessOptions options = new([TestFiles.ShortSong], temp.Combine("out"), 8000, null, null, true, temp.Combine("no-exports"), false, false);

        Assert.Equal(0, Program.Execute(options, Logger.None));
        Assert.Contains("fx-poly1,30.72", File.ReadAllText(temp.Combine("out", "durations.csv")), StringComparison.Ordinal);
    }
}
