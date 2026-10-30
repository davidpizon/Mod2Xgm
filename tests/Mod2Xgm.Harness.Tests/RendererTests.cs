using Mod2Xgm.Cli;
using Mod2Xgm.Render;
using Mod2Xgm.Scoring;
using Serilog.Core;

namespace Mod2Xgm.Harness.Tests;

/// <summary>Tests for the renderers: libopenmpt (when installed), Furnace (when installed) and DefleMask exports.</summary>
public sealed class RendererTests
{
    /// <summary>True when libopenmpt loads here; CI has none, so those tests are skipped there.</summary>
    private static bool OpenMptAvailable { get; } = CanLoadOpenMpt();

    private static bool CanLoadOpenMpt()
    {
        try
        {
            LibOpenMpt.Version();
            return true;
        }
        catch (HarnessException)
        {
            return false;
        }
    }

    /// <summary>libopenmpt plays a corpus song once through: its length matches the audio, and there is sound.</summary>
    [Fact]
    public void OpenMptRendersASong()
    {
        Assert.SkipUnless(OpenMptAvailable, "libopenmpt isn't installed (docs/harness.md#setup)");
        OpenMptRenderer renderer = new();

        ReferenceRender render = renderer.Render(File.ReadAllBytes(TestFiles.ShortSong), 8000);

        Assert.StartsWith("libopenmpt 0.", renderer.Name, StringComparison.Ordinal);
        Assert.Equal(30.72, render.DurationSeconds, 0.05);
        Assert.Equal(render.DurationSeconds, render.Audio.Seconds, 0.1);
        Assert.Equal(8000, render.Audio.SampleRate);
        Assert.Contains(render.Audio.Left, s => Math.Abs(s) > 0.05f);
    }

    /// <summary>Bytes libopenmpt can't read are reported, not crashed on.</summary>
    [Fact]
    public void OpenMptRejectsGarbage()
    {
        Assert.SkipUnless(OpenMptAvailable, "libopenmpt isn't installed (docs/harness.md#setup)");

        HarnessException ex = Assert.Throws<HarnessException>(() => new OpenMptRenderer().Render([1, 2, 3], 8000));
        Assert.Contains("can't load the module", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The Windows package's folder for this CPU is under Program Files; other systems have none.</summary>
    [Fact]
    public void WindowsInstallFolderMatchesTheSystem()
    {
        string? folder = LibOpenMpt.WindowsInstallFolder();

        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(folder);
            Assert.EndsWith(Path.Combine("libopenmpt", "bin", Environment.Is64BitProcess ? "amd64" : "x86"), folder, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(folder);
        }
    }

    /// <summary>Furnace renders once through, with no window and no status output, to the WAV named.</summary>
    [Fact]
    public void FurnaceArgumentsRenderOnceToTheWav() =>
        Assert.Equal(["-console", "-nostatus", "-nocontrols", "-loops", "0", "-output", "out.wav", "in.dmf"], FurnaceRenderer.Arguments("in.dmf", "out.wav"));

    /// <summary>A Furnace path given on the command line must exist; an existing one is used as is.</summary>
    [Fact]
    public void FurnacePathGivenMustExist()
    {
        using TempDirectory temp = new();
        string fake = temp.Combine("furnace.exe");
        File.WriteAllBytes(fake, []);

        Assert.Equal(fake, FurnaceRenderer.Find(fake));
        Assert.Throws<HarnessException>(() => FurnaceRenderer.Find(temp.Combine("missing.exe")));
    }

    /// <summary>A program that isn't there, or fails, is reported as a harness failure for that song.</summary>
    [Fact]
    public void FurnaceFailuresAreReported()
    {
        using TempDirectory temp = new();
        SongFiles song = new("song", "song.mod", temp.Combine("song.dmf"), temp.Path);

        HarnessException missing = Assert.Throws<HarnessException>(() => new FurnaceRenderer(temp.Combine("no-such-program.exe"), TimeSpan.FromSeconds(30)).Render(song));
        HarnessException failing = Assert.Throws<HarnessException>(() => new FurnaceRenderer("dotnet", TimeSpan.FromSeconds(60)).Render(song));

        Assert.Contains("can't be started", missing.Message, StringComparison.Ordinal);
        Assert.Contains("Furnace failed", failing.Message, StringComparison.Ordinal);
    }

    /// <summary>The real Furnace renders a converted corpus song (skipped where Furnace isn't installed).</summary>
    [Fact]
    public void FurnaceRendersAConversion()
    {
        string? furnace = FurnaceRenderer.Find(null);
        Assert.SkipWhen(furnace is null, "Furnace isn't installed");
        using TempDirectory temp = new();
        SongFiles song = new("fx-poly1", TestFiles.ShortSong, temp.Combine("fx-poly1.dmf"), temp.Path);
        ConversionCommand.Execute(new ConversionOptions { InputPath = song.ModPath, OutputPath = song.DmfPath }, Logger.None);

        CandidateRender render = new FurnaceRenderer(furnace, TimeSpan.FromMinutes(5)).Render(song);

        Assert.InRange(render.Audio.Seconds, 25, 40);
        Assert.True(File.Exists(temp.Combine("fx-poly1.furnace.wav")));
    }

    /// <summary>
    /// A DefleMask export is found by song name; the report says whether it was made from the same DMF the harness just
    /// converted (the corpus-check layout keeps that DMF in ../dmf), and a song without an export gets nothing.
    /// </summary>
    [Fact]
    public void DefleMaskExportsAreCheckedAgainstTheConversion()
    {
        using TempDirectory temp = new();
        Directory.CreateDirectory(temp.Combine("wav"));
        Directory.CreateDirectory(temp.Combine("dmf"));
        string fresh = temp.Combine("fresh.dmf");
        File.WriteAllBytes(fresh, [1, 2, 3]);
        foreach (string name in new[] { "same", "stale", "unchecked" })
        {
            WavFile.Write(temp.Combine("wav", name + ".wav"), new StereoAudio([0.5f], [0.5f], 8000));
        }

        File.WriteAllBytes(temp.Combine("dmf", "same.dmf"), [1, 2, 3]);
        File.WriteAllBytes(temp.Combine("dmf", "stale.dmf"), [9]);
        DefleMaskExports exports = new(temp.Combine("wav"));
        CandidateRender? Render(string name) => exports.Render(new SongFiles(name, name + ".mod", fresh, temp.Path));

        Assert.Equal("deflemask", exports.Name);
        Assert.Null(Render("same")?.Remark);
        Assert.NotNull(Render("same"));
        Assert.Equal("stale: exported from an older conversion", Render("stale")?.Remark);
        Assert.Equal("export not checked against the conversion (no ../dmf copy)", Render("unchecked")?.Remark);
        Assert.Null(Render("absent"));
    }

    /// <summary>The in-app renderer plays the original MOD in OpenMPT's time, so it is scored unaligned, with sound and no remark.</summary>
    [Fact]
    public void ModRendererCheckRendersTheMod()
    {
        ModRendererCheck check = new(8000);

        CandidateRender render = check.Render(new SongFiles("fx-poly1", TestFiles.ShortSong, "unused.dmf", "unused"));

        Assert.Equal(ModRendererCheck.RendererName, check.Name);
        Assert.Null(render.Remark);
        Assert.True(render.InReferenceTime);
        Assert.Equal(8000, render.Audio.SampleRate);
        Assert.Equal(30.72, render.Audio.Seconds, 0.05);
        Assert.Contains(render.Audio.Left, s => Math.Abs(s) > 0.05f);
    }

    /// <summary>Only the in-app renderer's scores are held to the bar, and only above it.</summary>
    [Fact]
    public void OnlyTheModRendererIsHeldToTheBar()
    {
        static CandidateScore Score(string renderer, double melDb) =>
            new(renderer, new ScoreReport(melDb, 0, 1, 1, 1, 0, 0, 0, melDb), null);

        Assert.True(ModRendererCheck.OverTheBar(Score(ModRendererCheck.RendererName, 1.3)));
        Assert.False(ModRendererCheck.OverTheBar(Score(ModRendererCheck.RendererName, 1.25)));
        Assert.False(ModRendererCheck.OverTheBar(Score("furnace", 9)));
        Assert.False(ModRendererCheck.OverTheBar(new CandidateScore(ModRendererCheck.RendererName, null, "failed")));
    }
}
