using Mod2Xgm.Analysis;
using Mod2Xgm.Cli;
using Mod2Xgm.Optimizer;
using Mod2Xgm.Tests.Mod;
using Serilog.Core;
using Serilog.Events;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="CandidateCache"/> and <see cref="CandidateCodec"/> (plan T8.6): a rerun is fast and identical.</summary>
[Trait("Category", "Integration")]
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
    public void Fit_OnRerun_GivesSameCandidates()
    {
        // Arrange
        using TempDirectory folder = new();
        SongAnalysis song = Song.Value;
        SampleAnalysis saw = song.Samples[0];
        Kinds first = Kinds.Make(new CandidateCache(folder.Path, Logger.None), song);

        // Act
        Kinds second = Kinds.Make(new CandidateCache(folder.Path, Logger.None), song);

        // Assert
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
    public void Fit_WithCh3Candidate_KeepsItsPair()
    {
        // Arrange
        using TempDirectory folder = new();
        _ = new CandidateCache(folder.Path, Logger.None).Ch3(Song.Value, Quick);

        // Act
        Ch3SongFit fit = new CandidateCache(folder.Path, Logger.None).Ch3(Song.Value, Quick);

        // Assert
        Assert.True(fit.Cached);
        Assert.All(fit.Candidates, c => Assert.Contains(fit.Pairs, p => ReferenceEquals(p, c.Pair)));
    }

    /// <summary>A region's FM fits are keyed by its sound: the same sample as another number, in another song, is fitted once and renamed.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Fit_WithSamplesThatSoundTheSame_SharesFmFit()
    {
        // Arrange
        using TempDirectory folder = new();
        CandidateCache sut = new(folder.Path, Logger.None);
        FmSampleFit made = FmFitter.Fit(SampleAnalyzer.Analyze(0, FmTargets.Saw, [FmTargets.C2]), Quick, sut);

        // Act
        FmSampleFit shared = FmFitter.Fit(SampleAnalyzer.Analyze(4, FmTargets.Saw, [FmTargets.C2]), Quick, sut);

        // Assert
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
    public void Fit_WithOtherOptionsOrSounds_FitsAgain()
    {
        // Arrange
        using TempDirectory folder = new();
        CandidateCache sut = new(folder.Path, Logger.None);

        // Act
        _ = FmFitter.Fit(FmTargets.Analyze(FmTargets.Saw), Quick, sut);

        // Assert
        Assert.False(FmFitter.Fit(FmTargets.Analyze(FmTargets.Saw), Quick with { MaxRenders = 13 }, sut).Regions[0].Cached);
        Assert.False(FmFitter.Fit(FmTargets.Analyze(FmTargets.Saw), Quick with { Seed = 2 }, sut).Regions[0].Cached);
        Assert.False(FmFitter.Fit(FmTargets.Analyze(FmTargets.Square), Quick, sut).Regions[0].Cached);
        Assert.True(FmFitter.Fit(FmTargets.Analyze(FmTargets.Saw), Quick, sut).Regions[0].Cached);
    }

    /// <summary>A song without a <see cref="SongAnalysis.Source"/> can't be matched, so its candidates are made every time and never kept.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Fit_WithoutSource_NeverCaches()
    {
        // Arrange
        using TempDirectory folder = new();
        SongAnalysis song = Song.Value with { Source = null };
        CandidateCache sut = new(folder.Path, Logger.None);

        // Act
        _ = sut.Psg(song, song.Samples[0]);

        // Assert
        Assert.False(sut.Psg(song, song.Samples[0]).Cached);
        Assert.False(Directory.Exists(Path.Combine(sut.Folder, "psg")));
    }

    /// <summary>Another song file, or another target, is another key.</summary>
    [Fact]
    public void Key_ForSong_HoldsSongAndTarget()
    {
        // Arrange
        using TempDirectory folder = new();
        CandidateCache sut = new(folder.Path, Logger.None);
        SongAnalysis song = Song.Value;

        // Act
        _ = sut.Pcm(song, song.Samples[0], PcmTarget.Xgm);

        // Assert
        Assert.False(sut.Pcm(song, song.Samples[0], PcmTarget.Xgm2).Cached);
        Assert.False(sut.Pcm(song with { Source = Fingerprint.Of([1, 2, 3]) }, song.Samples[0], PcmTarget.Xgm).Cached);
        Assert.True(sut.Pcm(song, song.Samples[0], PcmTarget.Xgm).Cached);
    }

    /// <summary>A cut-short entry is made again and replaced, said at Debug only.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Psg_WithTruncatedEntry_MakesItAgain()
    {
        // Arrange
        using TempDirectory folder = new();
        CollectingSink log = new();
        CandidateCache sut = new(folder.Path, log.CreateLogger());
        SongAnalysis song = Song.Value;
        _ = sut.Psg(song, song.Samples[0]);
        string entry = Assert.Single(Directory.GetFiles(Path.Combine(sut.Folder, "psg")));
        File.WriteAllBytes(entry, File.ReadAllBytes(entry)[..^3]);

        // Act
        bool first = sut.Psg(song, song.Samples[0]).Cached;
        bool second = sut.Psg(song, song.Samples[0]).Cached;

        // Assert
        Assert.False(first);
        Assert.True(second);
        Assert.Contains(log.Messages(LogEventLevel.Debug), m => m.Contains("can't be read", StringComparison.Ordinal));
        Assert.Empty(log.Messages(LogEventLevel.Warning));
    }

    /// <summary>An entry with garbage after the header (counts, lengths and enums out of range) is made again and replaced.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Psg_WithGarbledEntry_MakesItAgain()
    {
        // Arrange
        using TempDirectory folder = new();
        CollectingSink log = new();
        CandidateCache sut = new(folder.Path, log.CreateLogger());
        SongAnalysis song = Song.Value;
        _ = sut.Psg(song, song.Samples[0]);
        string entry = Assert.Single(Directory.GetFiles(Path.Combine(sut.Folder, "psg")));
        byte[] garbled = File.ReadAllBytes(entry);
        Array.Fill(garbled, (byte)0xFF, 48, garbled.Length - 48);
        File.WriteAllBytes(entry, garbled);

        // Act
        bool first = sut.Psg(song, song.Samples[0]).Cached;
        bool second = sut.Psg(song, song.Samples[0]).Cached;

        // Assert
        Assert.False(first);
        Assert.True(second);
        Assert.Empty(log.Messages(LogEventLevel.Warning));
    }

    /// <summary>A new build deletes the other builds' folders, and nothing else in the cache folder.</summary>
    [Fact]
    public void Constructor_WithNewBuild_DeletesOnlyOtherBuildsFolders()
    {
        // Arrange
        using TempDirectory folder = new();
        string other = folder.Combine("build-" + Guid.NewGuid().ToString("N"));
        string notOurs = folder.Combine("build-notes");
        string mine = folder.Combine(CandidateCache.BuildFolderName);
        foreach (string f in (string[])[other, notOurs, Path.Combine(mine, "psg")])
        {
            Directory.CreateDirectory(f);
        }
        _ = folder.CreateFile("readme.txt");

        // Act
        CandidateCache sut = new(folder.Path, Logger.None);

        // Assert
        Assert.Equal(mine, sut.Folder);
        Assert.False(Directory.Exists(other));
        Assert.True(Directory.Exists(notOurs));
        Assert.True(Directory.Exists(Path.Combine(mine, "psg")));
        Assert.True(File.Exists(folder.Combine("readme.txt")));
    }

    /// <summary>A cache folder that can't be written is reported once, and the candidates are made anyway.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Fit_WithUnwritableCache_ReportsOnce()
    {
        // Arrange
        using TempDirectory folder = new();
        string file = folder.CreateFile("in-the-way");
        CollectingSink log = new();
        CandidateCache sut = new(file, log.CreateLogger());

        // Act
        SongAnalysis song = Song.Value;

        // Assert
        Assert.False(sut.Psg(song, song.Samples[0]).Cached);
        Assert.False(sut.Psg(song, song.Samples[1]).Cached);
        Assert.Single(log.Messages(LogEventLevel.Warning));
    }

    /// <summary>The analysis key the conversion uses under <paramref name="options"/>.</summary>
    private static Fingerprint? SourceOf(ConversionOptions options)
    {
        Fingerprint? found = null;
        ConversionCommand.Execute(options, Logger.None, a => found = a.Source);
        return found;
    }

    /// <summary>The seed and the quality don't change the analysis, so they don't change its key.</summary>
    [Fact]
    public void Execute_WithOtherSeedAndQuality_KeepsAnalysisKey()
    {
        // Arrange
        using TempDirectory folder = new();
        ConversionOptions plain = new() { InputPath = folder.CreateFile("song.mod"), InfoOnly = true };
        Fingerprint? first = SourceOf(plain);

        // Act
        Fingerprint? other = SourceOf(plain with { Seed = 9, Quality = Quality.Best });

        // Assert
        Assert.NotNull(first);
        Assert.Equal(first, other);
    }

    /// <summary>The input region changes the analysis, so it changes its key.</summary>
    [Fact]
    public void Execute_WithOtherRegion_ChangesAnalysisKey()
    {
        // Arrange
        using TempDirectory folder = new();
        ConversionOptions plain = new() { InputPath = folder.CreateFile("song.mod"), InfoOnly = true };
        Fingerprint? first = SourceOf(plain);

        // Act
        Fingerprint? other = SourceOf(plain with { InputRegion = Region.Ntsc });

        // Assert
        Assert.NotEqual(first, other);
    }

    /// <summary>Ignored samples change the analysis, so they change its key.</summary>
    [Fact]
    public void Execute_WithIgnoredSamples_ChangesAnalysisKey()
    {
        // Arrange
        using TempDirectory folder = new();
        ConversionOptions plain = new() { InputPath = folder.CreateFile("song.mod"), InfoOnly = true };
        Fingerprint? first = SourceOf(plain);

        // Act
        Fingerprint? other = SourceOf(plain with { IgnoreSamples = new HashSet<int> { 1 } });

        // Assert
        Assert.NotEqual(first, other);
    }

    /// <summary>Another file gets another key.</summary>
    [Fact]
    public void Execute_WithChangedFile_ChangesAnalysisKey()
    {
        // Arrange
        using TempDirectory folder = new();
        string input = folder.CreateFile("song.mod");
        ConversionOptions plain = new() { InputPath = input, InfoOnly = true };
        Fingerprint? first = SourceOf(plain);
        File.WriteAllBytes(input, new ModBuilder().WithSample(1, "kick", 128).WithCell(0, row: 0, channel: 0, sample: 1, period: 428).Build());

        // Act
        Fingerprint? other = SourceOf(plain);

        // Assert
        Assert.NotEqual(first, other);
    }

    /// <summary>The cache folder is the user's: under the local app data on Windows, a cache folder elsewhere.</summary>
    [Fact]
    public void DefaultRoot_ForUser_IsCacheFolder()
    {
        // Act
        string root = CandidateCache.DefaultRoot;

        // Assert
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
