using System.Diagnostics;
using System.Globalization;
using Mod2Xgm.Cli;
using Mod2Xgm.Mapping;
using Mod2Xgm.Mod;
using Mod2Xgm.Model;
using Mod2Xgm.Render;
using Serilog.Core;

namespace Mod2Xgm.Harness;

/// <summary>The original MOD played by the reference player, and how long that player says the song is.</summary>
/// <param name="Audio">The rendering, once through the song.</param>
/// <param name="DurationSeconds">The song's length by the player's own count.</param>
internal sealed record ReferenceRender(StereoAudio Audio, double DurationSeconds);

/// <summary>Plays the original MOD: the sound every conversion is judged against.</summary>
internal interface IReferenceRenderer
{
    /// <summary>Name and version, for the report.</summary>
    string Name { get; }

    /// <summary>Renders <paramref name="mod"/> once through at <paramref name="sampleRate"/>.</summary>
    ReferenceRender Render(byte[] mod, int sampleRate);

    /// <summary>The time, in seconds, at which this player reaches each of <paramref name="rows"/> (order, row).</summary>
    IReadOnlyList<double> RowTimes(byte[] mod, IReadOnlyList<(int Order, int Row)> rows);
}

/// <summary>One song's files as the harness sees them.</summary>
/// <param name="Name">File name without extension; the key everything else is found by.</param>
/// <param name="ModPath">The original MOD.</param>
/// <param name="DmfPath">The DMF the harness just converted it to.</param>
/// <param name="WavDirectory">Where renders of this run go.</param>
internal sealed record SongFiles(string Name, string ModPath, string DmfPath, string WavDirectory);

/// <summary>A rendering of the conversion, and anything the report should say about where it came from.</summary>
/// <param name="Audio">The rendering.</param>
/// <param name="Remark">E.g. that a hand-made export may be out of date; <c>null</c> when there is nothing to say.</param>
/// <param name="InReferenceTime">
/// The rendering keeps the reference's own time, so it is scored without alignment: on a song that repeats, the
/// alignment can line a bar up with the wrong repeat.
/// </param>
internal sealed record CandidateRender(StereoAudio Audio, string? Remark, bool InReferenceTime = false);

/// <summary>Plays the converted file: one column of the report per renderer.</summary>
internal interface ICandidateRenderer
{
    /// <summary>Short name for the report (<c>furnace</c>, <c>deflemask</c>).</summary>
    string Name { get; }

    /// <summary>Renders <paramref name="song"/>, or returns <c>null</c> when this renderer has nothing for it.</summary>
    CandidateRender? Render(SongFiles song);
}

/// <summary>OpenMPT's engine, libopenmpt, with its default settings (what <c>openmpt123 --render</c> plays): the reference.</summary>
internal sealed class OpenMptRenderer : IReferenceRenderer
{
    private const int ChunkFrames = 4096;

    /// <summary>Loads libopenmpt (throws <see cref="HarnessException"/> when it can't).</summary>
    public OpenMptRenderer() => Name = "libopenmpt " + LibOpenMpt.Version();

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public IReadOnlyList<double> RowTimes(byte[] mod, IReadOnlyList<(int Order, int Row)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        IntPtr module = Open(mod);
        try
        {
            return [.. rows.Select(r => LibOpenMpt.SetPositionOrderRow(module, r.Order, r.Row))];
        }
        finally
        {
            LibOpenMpt.Destroy(module);
        }
    }

    /// <summary>Loads <paramref name="mod"/> into libopenmpt; the caller destroys the handle.</summary>
    private static unsafe IntPtr Open(byte[] mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        IntPtr module;
        int error;
        fixed (byte* data = mod)
        {
            module = LibOpenMpt.CreateFromMemory(data, (nuint)mod.Length, LibOpenMpt.SilentLog, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out error, IntPtr.Zero, IntPtr.Zero);
        }

        return module != IntPtr.Zero
            ? module
            : throw new HarnessException(string.Create(CultureInfo.InvariantCulture, $"libopenmpt can't load the module: {LibOpenMpt.DescribeError(error)} (error {error})"));
    }

    /// <inheritdoc/>
    public unsafe ReferenceRender Render(byte[] mod, int sampleRate)
    {
        IntPtr module = Open(mod);
        try
        {
            double duration = LibOpenMpt.GetDurationSeconds(module);
            List<float> left = [];
            List<float> right = [];
            float[] chunkLeft = new float[ChunkFrames];
            float[] chunkRight = new float[ChunkFrames];
            while (true)
            {
                nuint read;
                fixed (float* l = chunkLeft, r = chunkRight)
                {
                    read = LibOpenMpt.ReadFloatStereo(module, sampleRate, ChunkFrames, l, r);
                }

                if (read == 0)
                {
                    break;
                }

                left.AddRange(chunkLeft.AsSpan(0, (int)read));
                right.AddRange(chunkRight.AsSpan(0, (int)read));
            }

            return new ReferenceRender(new StereoAudio([.. left], [.. right], sampleRate), duration);
        }
        finally
        {
            LibOpenMpt.Destroy(module);
        }
    }
}

/// <summary>
/// The converter's own MOD renderer (plan T7.1, docs/adr/0054-mod-renderer.md) playing the original MOD, not the
/// conversion: it checks the in-app reference against libopenmpt's. The tick player plays the MOD (PAL) and its ticks get
/// OpenMPT's lengths at the render rate, with tempo changes one tick late as in libopenmpt's audio, so the two keep time
/// and the score measures the sound.
/// </summary>
/// <param name="sampleRate">The rate to render at: the harness's scoring rate.</param>
internal sealed class ModRendererCheck(int sampleRate) : ICandidateRenderer
{
    /// <summary>The renderer's name in the reports.</summary>
    public const string RendererName = "mod-renderer";

    /// <summary>
    /// The bar (plan T7.1): every song's whole-song log-mel distance to libopenmpt stays at or below this
    /// (docs/adr/0054-mod-renderer.md; the corpus's worst song was 0.94 dB at 8 kHz, and 0.85 dB after
    /// docs/adr/0060-openmpt-pitch-and-loops.md).
    /// </summary>
    public const double MaxMelDistanceDb = 1.25;

    /// <inheritdoc/>
    public string Name => RendererName;

    /// <summary>True when <paramref name="score"/> is this renderer's and over the bar.</summary>
    public static bool OverTheBar(CandidateScore score)
    {
        ArgumentNullException.ThrowIfNull(score);
        return score is { Renderer: RendererName, Score.MelDistanceDb: > MaxMelDistanceDb };
    }

    /// <inheritdoc/>
    public CandidateRender Render(SongFiles song)
    {
        ArgumentNullException.ThrowIfNull(song);
        ConversionNotes silent = new(Logger.None);
        ModModule module = ModReader.Read(File.ReadAllBytes(song.ModPath), silent);
        Song model = ModToSong.Convert(module, AmigaPitch.PalClockHz, silent);
        Playback playback = ModTickPlayer.Play(module, AmigaPitch.PalClockHz, 1.0, silent);
        ModRenderOptions options = new(sampleRate) { TickDurations = OpenMptTiming.TickDurations(playback, sampleRate, tempoOnItsTick: false) };
        return new CandidateRender(ModRenderer.Render(model, playback, options), null, InReferenceTime: true);
    }
}

/// <summary>
/// The converter's own Genesis renderer (plan T7.5, docs/adr/0059-genesis-renderer.md) playing the conversion's plan with
/// the DMF's DAC: the steps of the default conversion, then the tick player's playback with OpenMPT's tick lengths at
/// the render rate (as <see cref="ModRendererCheck"/>), so it keeps the reference's time and the score measures the
/// sound. How fast it rendered goes to the log, not the reports, which never depend on the machine.
/// </summary>
/// <param name="sampleRate">The rate to render at: the harness's scoring rate.</param>
/// <param name="logger">Where the render's speed is logged.</param>
internal sealed class GenesisRendererCheck(int sampleRate, Serilog.ILogger logger) : ICandidateRenderer
{
    /// <summary>The renderer's name in the reports.</summary>
    public const string RendererName = "genesis";

    /// <inheritdoc />
    public string Name => RendererName;

    /// <inheritdoc/>
    public CandidateRender Render(SongFiles song)
    {
        ArgumentNullException.ThrowIfNull(song);
        ConversionOptions options = new() { InputPath = song.ModPath };
        ConversionNotes silent = new(Logger.None);
        ModModule module = ModReader.Read(File.ReadAllBytes(song.ModPath), silent, options.Player);
        Song model = ModToSong.Convert(module, options.AmigaClockHz, silent);
        Playback playback = ModTickPlayer.Play(module, options.AmigaClockHz, options.AmigaTickRateScale, silent);
        GenesisPlan plan = new SimpleMapper().Map(model, playback, MappingOptions.From(options), silent);
        GenesisRenderOptions render = new(PcmDriver.Dmf, options.OutputRegion, sampleRate, options.Resampler)
        {
            TickDurations = OpenMptTiming.TickDurations(playback, sampleRate, tempoOnItsTick: false),
        };
        Stopwatch clock = Stopwatch.StartNew();
        StereoAudio audio = GenesisRenderer.Render(plan, model, playback, render, silent);
        clock.Stop();
        logger.Information(
            "{Song}: genesis rendered {Seconds:0.0} s in {Elapsed:0.00} s ({Speed:0}x real time)",
            song.Name,
            audio.Seconds,
            clock.Elapsed.TotalSeconds,
            audio.Seconds / Math.Max(clock.Elapsed.TotalSeconds, 1e-6));
        return new CandidateRender(audio, null, InReferenceTime: true);
    }
}

/// <summary>
/// Furnace's command line renders the DMF to a WAV (<c>furnace -console … -output</c>): automatic, so every run gets
/// a rendering of the fresh conversion. Furnace is a separate program, which only the harness may start (AGENTS.md).
/// </summary>
/// <param name="executable">Path to <c>furnace</c>.</param>
/// <param name="timeout">How long one render may take.</param>
internal sealed class FurnaceRenderer(string executable, TimeSpan timeout) : ICandidateRenderer, IDmfRenderer
{
    /// <inheritdoc/>
    public string Name => "furnace";

    /// <summary>The arguments for rendering <paramref name="dmf"/> once through (no loops) to <paramref name="wav"/>.</summary>
    internal static IReadOnlyList<string> Arguments(string dmf, string wav) =>
        ["-console", "-nostatus", "-nocontrols", "-loops", "0", "-output", wav, dmf];

    /// <summary>
    /// <paramref name="configured"/> when given, else <c>furnace</c> on PATH, else the Windows installer's folder; or
    /// <c>null</c> when none is there.
    /// </summary>
    public static string? Find(string? configured)
    {
        if (configured is not null)
        {
            return File.Exists(configured) ? configured : throw new HarnessException($"Furnace not found at {configured} (--furnace)");
        }

        string name = OperatingSystem.IsWindows() ? "furnace.exe" : "furnace";
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Furnace", name);
        return OperatingSystem.IsWindows() && File.Exists(installed) ? installed : null;
    }

    /// <inheritdoc/>
    public CandidateRender Render(SongFiles song)
    {
        ArgumentNullException.ThrowIfNull(song);
        return new CandidateRender(RenderDmf(song.DmfPath, Path.Combine(song.WavDirectory, song.Name + ".furnace.wav")), null);
    }

    /// <inheritdoc/>
    public StereoAudio RenderDmf(string dmfPath, string wavPath)
    {
        ArgumentNullException.ThrowIfNull(dmfPath);
        ArgumentNullException.ThrowIfNull(wavPath);
        string wav = wavPath;
        File.Delete(wav);
        ProcessStartInfo start = new(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in Arguments(dmfPath, wav))
        {
            start.ArgumentList.Add(argument);
        }

        Process? started;
        try
        {
            started = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new HarnessException($"Furnace can't be started: {executable} ({ex.Message})", ex);
        }

        using Process process = started ?? throw new HarnessException($"Furnace didn't start: {executable}");

        // Read both streams while it runs, or a full pipe stalls it.
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new HarnessException(string.Create(CultureInfo.InvariantCulture, $"Furnace took longer than {timeout.TotalSeconds} s on {dmfPath}"));
        }

        Task.WaitAll(output, errors);
        if (process.ExitCode != 0 || !File.Exists(wav))
        {
            throw new HarnessException(string.Create(
                CultureInfo.InvariantCulture,
                $"Furnace failed on {dmfPath} (exit {process.ExitCode}): {errors.Result.Trim()}{output.Result.Trim()}"));
        }

        return WavFile.Read(wav);
    }
}

/// <summary>
/// DefleMask exports made by hand (File → Save WAV), found as <c>name.wav</c> in one folder. DefleMask has no command
/// line, so these can be older than the conversion: when the DMF they were made from sits in a sibling <c>dmf</c>
/// folder (the corpus-check layout), it is compared with the fresh conversion and the report says when they differ.
/// </summary>
/// <param name="directory">The folder of WAVs.</param>
internal sealed class DefleMaskExports(string directory) : ICandidateRenderer
{
    /// <inheritdoc/>
    public string Name => "deflemask";

    /// <inheritdoc/>
    public CandidateRender? Render(SongFiles song)
    {
        ArgumentNullException.ThrowIfNull(song);
        string wav = Path.Combine(directory, song.Name + ".wav");
        if (!File.Exists(wav))
        {
            return null;
        }

        string exported = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(wav)) ?? directory, "..", "dmf", song.Name + ".dmf");
        string? remark = !File.Exists(exported)
            ? "export not checked against the conversion (no ../dmf copy)"
            : File.ReadAllBytes(exported).AsSpan().SequenceEqual(File.ReadAllBytes(song.DmfPath))
                ? null
                : "stale: exported from an older conversion";
        return new CandidateRender(WavFile.Read(wav), remark);
    }
}
