using System.Diagnostics;
using System.Globalization;

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
internal sealed record CandidateRender(StereoAudio Audio, string? Remark);

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
/// Furnace's command line renders the DMF to a WAV (<c>furnace -console … -output</c>): automatic, so every run gets
/// a rendering of the fresh conversion. Furnace is a separate program, which only the harness may start (AGENTS.md).
/// </summary>
/// <param name="executable">Path to <c>furnace</c>.</param>
/// <param name="timeout">How long one render may take.</param>
internal sealed class FurnaceRenderer(string executable, TimeSpan timeout) : ICandidateRenderer
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
        string wav = Path.Combine(song.WavDirectory, song.Name + ".furnace.wav");
        File.Delete(wav);
        ProcessStartInfo start = new(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in Arguments(song.DmfPath, wav))
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
            throw new HarnessException(string.Create(CultureInfo.InvariantCulture, $"Furnace took longer than {timeout.TotalSeconds} s on {song.DmfPath}"));
        }

        Task.WaitAll(output, errors);
        if (process.ExitCode != 0 || !File.Exists(wav))
        {
            throw new HarnessException(string.Create(
                CultureInfo.InvariantCulture,
                $"Furnace failed on {song.DmfPath} (exit {process.ExitCode}): {errors.Result.Trim()}{output.Result.Trim()}"));
        }

        return new CandidateRender(WavFile.Read(wav), null);
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
