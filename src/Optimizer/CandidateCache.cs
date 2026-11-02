using System.Text.RegularExpressions;
using Mod2Xgm.Analysis;
using Serilog;

namespace Mod2Xgm.Optimizer;

/// <summary>
/// Keeps the optimizer's candidates on disk so a rerun doesn't make them again (plan T8.6,
/// docs/adr/0074-candidate-cache.md): FM fits per pitch region, keyed by the region's sound, so a sample a song shares with
/// another is fitted once; PSG, PCM, pre-mix and CH3 candidates per song, keyed by the song (<see cref="SongAnalysis.Source"/>).
/// Every key holds the build (<see cref="Build"/>), and a new build deletes the other builds' folders (David). A result
/// from the cache is the one made, with how long it took then, marked <c>Cached</c>.
/// </summary>
/// <remarks>
/// Safe to use from many threads and processes: an entry is written to a temporary file and renamed into place. An entry
/// that can't be read is made again; a folder that can't be written is reported once, and the conversion goes on without
/// it.
/// </remarks>
internal sealed partial class CandidateCache
{
    /// <summary>Bumped when the file layout changes; the build is in every key anyway.</summary>
    private const int FormatVersion = 1;

    private static ReadOnlySpan<byte> Magic => "M2XC"u8;

    private readonly ILogger _logger;
    private int _warned;

    /// <summary>A cache in <paramref name="root"/>'s folder for this build; deletes the other builds' folders there.</summary>
    /// <param name="root">The cache folder (<see cref="DefaultRoot"/>, or <c>--cache-dir</c>).</param>
    /// <param name="logger">Where a cache that can't be used is reported, once, and damaged entries at <c>Debug</c>.</param>
    public CandidateCache(string root, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        Root = Path.GetFullPath(root);
        Folder = Path.Combine(Root, BuildFolderName);
        DeleteOtherBuilds();
    }

    /// <summary>
    /// The compiled code's fingerprint: the Mod2Xgm assembly's module version ID, which a deterministic build changes when,
    /// and only when, the compiled code does (David: every build starts with an empty cache).
    /// </summary>
    public static Guid Build { get; } = typeof(CandidateCache).Module.ModuleVersionId;

    /// <summary>The folder this build's entries go in, under <see cref="Root"/>.</summary>
    public static string BuildFolderName { get; } = "build-" + Build.ToString("N");

    /// <summary>The cache folder given.</summary>
    public string Root { get; }

    /// <summary>This build's folder.</summary>
    public string Folder { get; }

    /// <summary>
    /// The user's cache folder: <c>%LOCALAPPDATA%\Mod2Xgm\Cache</c> on Windows, <c>~/Library/Caches/Mod2Xgm</c> on macOS,
    /// <c>$XDG_CACHE_HOME/mod2xgm</c> (or <c>~/.cache/mod2xgm</c>) elsewhere.
    /// </summary>
    public static string DefaultRoot
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mod2Xgm", "Cache");
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsMacOS())
            {
                return Path.Combine(home, "Library", "Caches", "Mod2Xgm");
            }

            string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            return Path.Combine(Path.IsPathRooted(xdg) ? xdg : Path.Combine(home, ".cache"), "mod2xgm");
        }
    }

    /// <summary>The FM fits of one pitch region: from the cache, or made by <paramref name="fit"/> and kept.</summary>
    /// <param name="target">The region's sound.</param>
    /// <param name="options">How it is fitted.</param>
    /// <param name="region">The region's index, which the fit carries.</param>
    /// <param name="name">What fitted patches are called; a fit kept under another sample's name is renamed.</param>
    /// <param name="fit">Makes the fits.</param>
    public FmRegionFit FmRegion(NoteTarget target, FmFitOptions options, int region, string name, Func<FmRegionFit> fit)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fit);
        using Hasher key = new();
        key.Add("fm");
        AddTarget(key, target);
        AddOptions(key, options);
        FmRegionFit? found = Get(key.Finish(), "fm", CandidateCodec.ReadFmRegionFit);
        if (found is not null)
        {
            return Renamed(found, region, name) with { Cached = true };
        }

        FmRegionFit made = fit();
        Put(key.Finish(), "fm", w => CandidateCodec.Write(w, made));
        return made;
    }

    /// <summary>The PSG candidates of <paramref name="analysis"/> in <paramref name="song"/>.</summary>
    public PsgSampleFit Psg(SongAnalysis song, SampleAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(analysis);
        return SongWide(song, "psg", k => k.Add(analysis.Sample), () => PsgFitter.Fit(analysis, song.Voices), CandidateCodec.Write, CandidateCodec.ReadPsgSampleFit, f => f with { Cached = true });
    }

    /// <summary>The PCM candidates of <paramref name="analysis"/> in <paramref name="song"/> for <paramref name="target"/>.</summary>
    public PcmSampleFit Pcm(SongAnalysis song, SampleAnalysis analysis, PcmTarget target)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(analysis);
        return SongWide(
            song,
            "pcm",
            k =>
            {
                k.Add(analysis.Sample);
                k.Add((int)target);
            },
            () => PcmFitter.Fit(song, analysis, target),
            CandidateCodec.Write,
            CandidateCodec.ReadPcmSampleFit,
            f => f with { Cached = true });
    }

    /// <summary>The pre-mix candidates of <paramref name="song"/> for <paramref name="target"/>.</summary>
    public PreMixSongFit PreMix(SongAnalysis song, PcmTarget target, IReadOnlySet<int>? keepOut = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        return SongWide(
            song,
            "premix",
            k =>
            {
                k.Add((int)target);
                AddSet(k, keepOut);
            },
            () => PreMixer.Fit(song, target, keepOut),
            CandidateCodec.Write,
            CandidateCodec.ReadPreMixSongFit,
            f => f with { Cached = true });
    }

    /// <summary>The locked pairs and CH3 candidates of <paramref name="song"/> (<see cref="Ch3Fitter.Fit"/>).</summary>
    public Ch3SongFit Ch3(SongAnalysis song, FmFitOptions options, IReadOnlyList<FmSampleFit>? fmFits = null, IReadOnlySet<int>? keepOut = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(options);
        return SongWide(
            song,
            "ch3",
            k =>
            {
                AddOptions(k, options);
                AddSet(k, keepOut);
                AddFmFits(k, fmFits);
            },
            () => Ch3Fitter.Fit(song, options, fmFits, keepOut),
            CandidateCodec.Write,
            CandidateCodec.ReadCh3SongFit,
            f => f with { Cached = true });
    }

    /// <summary>A song-wide candidate: cached when the song has a <see cref="SongAnalysis.Source"/>, made every time without one.</summary>
    private T SongWide<T>(SongAnalysis song, string kind, Action<Hasher> addKey, Func<T> make, Action<BinaryWriter, T> write, Func<BinaryReader, T> read, Func<T, T> mark)
        where T : class
    {
        if (song.Source is not { } source)
        {
            return make();
        }

        using Hasher key = new();
        key.Add(kind);
        key.Add(source);
        addKey(key);
        Fingerprint k = key.Finish();
        if (Get(k, kind, read) is { } found)
        {
            return mark(found);
        }

        T made = make();
        Put(k, kind, w => write(w, made));
        return made;
    }

    private T? Get<T>(Fingerprint key, string kind, Func<BinaryReader, T> read)
        where T : class
    {
        string path = PathOf(key, kind);
        try
        {
            using FileStream file = File.OpenRead(path);
            using BinaryReader reader = new(file);
            Span<byte> head = stackalloc byte[Magic.Length + Fingerprint.Size];
            reader.BaseStream.ReadExactly(head[..Magic.Length]);
            if (!head[..Magic.Length].SequenceEqual(Magic) || reader.ReadInt32() != FormatVersion || reader.ReadString() != kind)
            {
                throw new InvalidDataException("Not a cache entry of this kind.");
            }

            reader.BaseStream.ReadExactly(head[Magic.Length..]);
            if (Fingerprint.From(head[Magic.Length..]) != key)
            {
                throw new InvalidDataException("The entry's key doesn't match its name.");
            }

            T value = read(reader);
            return file.Position == file.Length ? value : throw new InvalidDataException("Bytes after the entry.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or ArgumentException or OverflowException)
        {
            _logger.Debug("Cache entry {Path} can't be read ({Message}); making it again", path, ex.Message);
            return null;
        }
    }

    private void Put(Fingerprint key, string kind, Action<BinaryWriter> write)
    {
        string path = PathOf(key, kind);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (FileStream file = File.Create(temp))
            using (BinaryWriter writer = new(file))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(kind);
                Span<byte> hash = stackalloc byte[Fingerprint.Size];
                key.WriteTo(hash);
                writer.Write(hash);
                write(writer);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                _logger.Warning("The candidate cache in {Folder} can't be written ({Message}); going on without keeping candidates", Folder, ex.Message);
            }
        }
    }

    private string PathOf(Fingerprint key, string kind) => Path.Combine(Folder, kind, key.ToHex() + ".bin");

    /// <summary>Deletes every other build's folder in <see cref="Root"/>: only folders named as this class names them.</summary>
    private void DeleteOtherBuilds()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        foreach (string folder in Directory.EnumerateDirectories(Root))
        {
            string name = Path.GetFileName(folder);
            if (name != BuildFolderName && BuildFolderPattern().IsMatch(name))
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                    _logger.Debug("Deleted another build's candidate cache {Folder}", folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.Debug("Another build's candidate cache {Folder} can't be deleted ({Message})", folder, ex.Message);
                }
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next build's clean-up.
        }
    }

    /// <summary>A fit kept for another sample or region, with this one's region and patch names.</summary>
    private static FmRegionFit Renamed(FmRegionFit fit, int region, string name)
    {
        Dictionary<FmFit, FmFit> renamed = new(ReferenceEqualityComparer.Instance);
        FmFit Rename(FmFit f)
        {
            if (!renamed.TryGetValue(f, out FmFit? r))
            {
                r = f.Source == FmFitSource.Library ? f : f with { Patch = f.Patch with { Name = name } };
                renamed[f] = r;
            }

            return r;
        }

        return new FmRegionFit(
            region,
            Rename(fit.Library),
            fit.Exhaustive is null ? null : Rename(fit.Exhaustive),
            fit.Genetic is null ? null : Rename(fit.Genetic),
            Rename(fit.Best),
            Rename(fit.NoLfo),
            fit.Renders,
            fit.Elapsed);
    }

    private static void AddTarget(Hasher key, NoteTarget target)
    {
        key.Add(target.NoteHz);
        key.Add(target.FundamentalHz);
        key.Add(target.Spectrum.HopSeconds);
        AddFrames(key, target.Spectrum.MelBandsDb);
        key.Add(target.Spectrum.HarmonicsDb is not null);
        if (target.Spectrum.HarmonicsDb is { } harmonics)
        {
            AddFrames(key, harmonics);
        }
    }

    private static void AddFrames(Hasher key, IReadOnlyList<float[]> frames)
    {
        key.Add(frames.Count);
        foreach (float[] frame in frames)
        {
            key.Add(frame.Length);
            foreach (float v in frame)
            {
                key.Add(v);
            }
        }
    }

    private static void AddOptions(Hasher key, FmFitOptions options)
    {
        key.Add(options.MaxRenders);
        key.Add(options.Seed);
        key.Add(options.Exhaustive);
        key.Add(options.Genetic);
        key.Add(options.SsgEg);
        key.Add(options.Lfo);
    }

    private static void AddSet(Hasher key, IReadOnlySet<int>? set)
    {
        int[] items = set is null ? [] : [.. set.Order()];
        key.Add(items.Length);
        foreach (int i in items)
        {
            key.Add(i);
        }
    }

    /// <summary>The FM fits CH3 cuts down, all but how long they took: CH3's candidates depend on them.</summary>
    private static void AddFmFits(Hasher key, IReadOnlyList<FmSampleFit>? fits)
    {
        key.Add(fits is not null);
        if (fits is null)
        {
            return;
        }

        using MemoryStream bytes = new();
        using (BinaryWriter writer = new(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(fits.Count);
            foreach (FmSampleFit fit in fits)
            {
                writer.Write(fit.Sample);
                writer.Write(fit.Regions.Count);
                foreach (FmRegionFit region in fit.Regions)
                {
                    CandidateCodec.Write(writer, region, times: false);
                }
            }
        }

        key.Add(bytes.GetBuffer().AsSpan(0, (int)bytes.Length));
    }

    [GeneratedRegex("^build-[0-9a-f]{32}$")]
    private static partial Regex BuildFolderPattern();
}
