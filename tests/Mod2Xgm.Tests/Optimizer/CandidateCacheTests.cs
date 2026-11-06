using Mod2Xgm.Analysis;
using Mod2Xgm.Cli;
using Mod2Xgm.Optimizer;
using Mod2Xgm.Tests.Mod;
using Serilog.Core;
using Serilog.Events;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="CandidateCache"/> and <see cref="CandidateCodec"/> (plan T8.6): a rerun is fast and identical.</summary>
public sealed class CandidateCacheTests
{
    /// <summary>A tiny budget: the fitting itself is tested with <see cref="FmFitter"/>.</summary>
    private static readonly FmFitOptions Quick = new(MaxRenders: 12, Genetic: false, Lfo: false, SsgEg: false);

    /// <summary>A saw and a square an octave up, together every 8 rows, eight times (a locked pair, docs/adr/0076-loose-locked-pairs.md), keyed by its file.</summary>
    private static readonly Lazy<SongAnalysis> Song = new(() =>
    {
        ModBuilder mod = new ModBuilder()
            .WithSample(1, "saw", 64, loopStart: 0, loopLength: 64)
            .WithSample(2, "square", 64, data: i => (sbyte)(i < 32 ? 80 : -80), loopStart: 0, loopLength: 64);
        for (int row = 0; row < 64; row += 8)
        {
            mod.WithCell(0, row: row, channel: 0, sample: 1, period: 428).WithCell(0, row: row, channel: 1, sample: 2, period: 214);
        }

        byte[] bytes = mod.Build();
        return PcmSongs.Analyze(bytes) with { Source = Fingerprint.Of(bytes) };
    });

    /// <summary>Every kind of candidate read back from the cache is the one made, byte for byte, with how long it took then.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void ARerunGivesTheSameCandidates()
    {
        using TempDirectory folder = new();
        SongAnalysis song = Song.Value;
        SampleAnalysis saw = song.Samples[0];

        Kinds first = Kinds.Make(new CandidateCache(folder.Path, Logger.None), song);
        Kinds second = Kinds.Make(new CandidateCache(folder.Path, Logger.None), song);

        Assert.False(first.AnyCached);
        Assert.True(second.AllCached);
        Assert.Equal(first.Bytes(), second.Bytes());
        Assert.Equal(first.Fm.Regions[0].Elapsed, second.Fm.Regions[0].Elapsed);
        Assert.Equal(first.Ch3.Elapsed, second.Ch3.Elapsed);
        Assert.NotEmpty(second.Ch3.Candidates);
        Assert.Equal(saw.Name, second.Psg.Name);
    }

    /// <summary>A CH3 candidate's pair is one of the fit's pairs when read back, as when made.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void ACh3CandidateKeepsItsPair()
    {
        using TempDirectory folder = new();
        _ = new CandidateCache(folder.Path, Logger.None).Ch3(Song.Value, Quick);
        Ch3SongFit fit = new CandidateCache(folder.Path, Logger.None).Ch3(Song.Value, Quick);

        Assert.True(fit.Cached);
        Assert.All(fit.Candidates, c => Assert.Contains(fit.Pairs, p => ReferenceEquals(p, c.Pair)));
    }

    /// <summary>A region's FM fits are keyed by its sound: the same sample as another number, in another song, is fitted once and renamed.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void FmFitsAreSharedBetweenSamplesThatSoundTheSame()
    {
        using TempDirectory folder = new();
        CandidateCache cache = new(folder.Path, Logger.None);
        FmSampleFit made = FmFitter.Fit(SampleAnalyzer.Analyze(0, FmTargets.Saw, [FmTargets.C2]), Quick, cache);
        FmSampleFit shared = FmFitter.Fit(SampleAnalyzer.Analyze(4, FmTargets.Saw, [FmTargets.C2]), Quick, cache);

        FmRegionFit region = Assert.Single(shared.Regions);
        Assert.True(region.Cached);
        Assert.Equal("Fit 1.1", made.Regions[0].Exhaustive!.Patch.Name);
        Assert.Equal("Fit 5.1", region.Exhaustive!.Patch.Name);
        Assert.Equal(made.Regions[0].Library.Patch.Name, region.Library.Patch.Name);
        Assert.Equal(made.Regions[0].Best.Score, region.Best.Score);
        Assert.Equal(ReferenceEquals(made.Regions[0].Best, made.Regions[0].Exhaustive), ReferenceEquals(region.Best, region.Exhaustive));
    }

    /// <summary>Other fit options, or another sound, are other keys.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void OtherOptionsOrSoundsAreFittedAgain()
    {
        using TempDirectory folder = new();
        CandidateCache cache = new(folder.Path, Logger.None);
        _ = FmFitter.Fit(FmTargets.Analyze(FmTargets.Saw), Quick, cache);

        Assert.False(FmFitter.Fit(FmTargets.Analyze(FmTargets.Saw), Quick with { MaxRenders = 13 }, cache).Regions[0].Cached);
        Assert.False(FmFitter.Fit(FmTargets.Analyze(FmTargets.Saw), Quick with { Seed = 2 }, cache).Regions[0].Cached);
        Assert.False(FmFitter.Fit(FmTargets.Analyze(FmTargets.Square), Quick, cache).Regions[0].Cached);
        Assert.True(FmFitter.Fit(FmTargets.Analyze(FmTargets.Saw), Quick, cache).Regions[0].Cached);
    }

    /// <summary>A song without a <see cref="SongAnalysis.Source"/> can't be matched, so its candidates are made every time and never kept.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void ASongWithoutASourceIsNeverCached()
    {
        using TempDirectory folder = new();
        SongAnalysis song = Song.Value with { Source = null };
        CandidateCache cache = new(folder.Path, Logger.None);
        _ = cache.Psg(song, song.Samples[0]);

        Assert.False(cache.Psg(song, song.Samples[0]).Cached);
        Assert.False(Directory.Exists(Path.Combine(cache.Folder, "psg")));
    }

    /// <summary>Another song file, or another target, is another key.</summary>
    [Fact]
    public void SongWideKeysHoldTheSongAndTarget()
    {
        using TempDirectory folder = new();
        CandidateCache cache = new(folder.Path, Logger.None);
        SongAnalysis song = Song.Value;
        _ = cache.Pcm(song, song.Samples[0], PcmTarget.Xgm);

        Assert.False(cache.Pcm(song, song.Samples[0], PcmTarget.Xgm2).Cached);
        Assert.False(cache.Pcm(song with { Source = Fingerprint.Of([1, 2, 3]) }, song.Samples[0], PcmTarget.Xgm).Cached);
        Assert.True(cache.Pcm(song, song.Samples[0], PcmTarget.Xgm).Cached);
    }

    /// <summary>A damaged entry is made again and replaced, said at Debug only.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void ADamagedEntryIsMadeAgain()
    {
        using TempDirectory folder = new();
        CollectingSink log = new();
        CandidateCache cache = new(folder.Path, log.CreateLogger());
        SongAnalysis song = Song.Value;
        _ = cache.Psg(song, song.Samples[0]);
        string entry = Assert.Single(Directory.GetFiles(Path.Combine(cache.Folder, "psg")));
        byte[] bytes = File.ReadAllBytes(entry);
        File.WriteAllBytes(entry, bytes[..^3]);

        Assert.False(cache.Psg(song, song.Samples[0]).Cached);
        Assert.True(cache.Psg(song, song.Samples[0]).Cached);

        // Garbage after the header: counts, lengths and enums out of range.
        byte[] garbled = File.ReadAllBytes(entry);
        Array.Fill(garbled, (byte)0xFF, 48, garbled.Length - 48);
        File.WriteAllBytes(entry, garbled);
        Assert.False(cache.Psg(song, song.Samples[0]).Cached);
        Assert.True(cache.Psg(song, song.Samples[0]).Cached);
        Assert.Contains(log.Messages(LogEventLevel.Debug), m => m.Contains("can't be read", StringComparison.Ordinal));
        Assert.Empty(log.Messages(LogEventLevel.Warning));
    }

    /// <summary>A new build deletes the other builds' folders, and nothing else in the cache folder.</summary>
    [Fact]
    public void ANewBuildDeletesOnlyOtherBuildsFolders()
    {
        using TempDirectory folder = new();
        string other = folder.Combine("build-" + Guid.NewGuid().ToString("N"));
        string notOurs = folder.Combine("build-notes");
        string mine = folder.Combine(CandidateCache.BuildFolderName);
        foreach (string f in (string[])[other, notOurs, Path.Combine(mine, "psg")])
        {
            Directory.CreateDirectory(f);
        }

        _ = folder.CreateFile("readme.txt");

        CandidateCache cache = new(folder.Path, Logger.None);

        Assert.Equal(mine, cache.Folder);
        Assert.False(Directory.Exists(other));
        Assert.True(Directory.Exists(notOurs));
        Assert.True(Directory.Exists(Path.Combine(mine, "psg")));
        Assert.True(File.Exists(folder.Combine("readme.txt")));
    }

    /// <summary>A cache folder that can't be written is reported once, and the candidates are made anyway.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void AnUnwritableCacheIsReportedOnce()
    {
        using TempDirectory folder = new();
        string file = folder.CreateFile("in-the-way");
        CollectingSink log = new();
        CandidateCache cache = new(file, log.CreateLogger());
        SongAnalysis song = Song.Value;

        Assert.False(cache.Psg(song, song.Samples[0]).Cached);
        Assert.False(cache.Psg(song, song.Samples[1]).Cached);
        Assert.Single(log.Messages(LogEventLevel.Warning));
    }

    /// <summary>The conversion keys its analysis by the file and the options that change it.</summary>
    [Fact]
    public void TheConversionKeysItsAnalysis()
    {
        using TempDirectory folder = new();
        string input = folder.CreateFile("song.mod");
        Fingerprint? Source(ConversionOptions options)
        {
            Fingerprint? found = null;
            ConversionCommand.Execute(options, Logger.None, a => found = a.Source);
            return found;
        }

        ConversionOptions plain = new() { InputPath = input, InfoOnly = true };
        Fingerprint? first = Source(plain);

        Assert.NotNull(first);
        Assert.Equal(first, Source(plain with { Seed = 9, Quality = Quality.Best }));
        Assert.NotEqual(first, Source(plain with { InputRegion = Region.Ntsc }));
        Assert.NotEqual(first, Source(plain with { IgnoreSamples = new HashSet<int> { 1 } }));
        File.WriteAllBytes(input, new ModBuilder().WithSample(1, "kick", 128).WithCell(0, row: 0, channel: 0, sample: 1, period: 428).Build());
        Assert.NotEqual(first, Source(plain));
    }

    /// <summary>The cache folder is the user's: under the local app data on Windows, a cache folder elsewhere.</summary>
    [Fact]
    public void TheDefaultFolderIsTheUsersCacheFolder()
    {
        string root = CandidateCache.DefaultRoot;

        Assert.True(Path.IsPathRooted(root));
        Assert.EndsWith(OperatingSystem.IsWindows() ? Path.Combine("Mod2Xgm", "Cache") : OperatingSystem.IsMacOS() ? Path.Combine("Caches", "Mod2Xgm") : "mod2xgm", root, StringComparison.Ordinal);
        Assert.Matches("^build-[0-9a-f]{32}$", CandidateCache.BuildFolderName);
        Assert.NotEqual(Guid.Empty, CandidateCache.Build);
    }

    /// <summary>One of each kind of candidate for <see cref="Song"/>.</summary>
    private sealed record Kinds(FmSampleFit Fm, PsgSampleFit Psg, PcmSampleFit Pcm, PreMixSongFit PreMix, Ch3SongFit Ch3)
    {
        public bool AnyCached => Fm.Regions.Any(r => r.Cached) || Psg.Cached || Pcm.Cached || PreMix.Cached || Ch3.Cached;

        public bool AllCached => Fm.Regions.All(r => r.Cached) && Psg.Cached && Pcm.Cached && PreMix.Cached && Ch3.Cached;

        public static Kinds Make(CandidateCache cache, SongAnalysis song)
        {
            FmSampleFit fm = FmFitter.Fit(song.Samples[0], Quick, cache);
            return new Kinds(
                fm,
                cache.Psg(song, song.Samples[0]),
                cache.Pcm(song, song.Samples[0], PcmTarget.Xgm2),
                cache.PreMix(song, PcmTarget.Dmf),
                cache.Ch3(song, Quick, [fm]));
        }

        /// <summary>Every candidate as the cache writes it.</summary>
        public byte[] Bytes()
        {
            using MemoryStream bytes = new();
            using (BinaryWriter writer = new(bytes))
            {
                foreach (FmRegionFit r in Fm.Regions)
                {
                    CandidateCodec.Write(writer, r);
                }

                CandidateCodec.Write(writer, Psg);
                CandidateCodec.Write(writer, Pcm);
                CandidateCodec.Write(writer, PreMix);
                CandidateCodec.Write(writer, Ch3);
            }

            return bytes.ToArray();
        }
    }
}
