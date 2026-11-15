using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Mod2Xgm.Render;
using Mod2Xgm.Output;
using Mod2Xgm.Scoring;
using Serilog;

namespace Mod2Xgm.Harness;

/// <summary>A way of fitting a squeezed budget (plan T9.4t1, docs/adr/0113-squeeze-rounds.md).</summary>
internal enum SqueezeWay
{
    /// <summary>The converter's own way: shrink the recordings, then the least important notes give theirs up.</summary>
    Today,

    /// <summary>No shrinking (<c>rule:shrink=off</c>): recordings stay whole and whole sounds go to FM/PSG.</summary>
    NoShrink,

    /// <summary>Every recording at XGM2 half speed (<c>rule:half-speed=all</c>), so more sounds stay recordings.</summary>
    HalfSpeedAll,

    /// <summary>Drums choose first (<c>rule:keep=drums</c>), so they keep their recordings.</summary>
    DrumsFirst,

    /// <summary>Tonal sounds choose first (<c>rule:keep=tonal</c>): melody, bass and chords keep their recordings.</summary>
    TonalFirst,
}

/// <summary>One pair of a squeeze round: two ways at one budget, at the clip where they differ most.</summary>
/// <param name="Song">The song's file name.</param>
/// <param name="First">The way compared with the second.</param>
/// <param name="Second">The other way.</param>
/// <param name="Budget">The PCM bytes both ways may use.</param>
/// <param name="StartSeconds">Where the clip starts.</param>
/// <param name="DifferenceDb">The log-mel distance between the two clips: how far apart the ways sound there.</param>
internal sealed record SqueezePair(string Song, SqueezeWay First, SqueezeWay Second, long Budget, double StartSeconds, double DifferenceDb);

/// <summary>Builds squeeze rounds (plan T9.4t1, docs/adr/0113-squeeze-rounds.md): each pair is one song at one budget, squeezed two ways.</summary>
internal static class Squeeze
{
    /// <summary>Most pairs of one song in a round: those whose ways differ most (Claude: six songs make a round of at most 18).</summary>
    public const int MaxPairsPerSong = 3;

    /// <summary>How many songs a squeeze round squeezes: those that store the most PCM bytes today (David).</summary>
    public const int MaxSongs = 6;

    /// <summary>The <see cref="MaxSongs"/> songs that store the most PCM bytes, biggest first; ties by name. A song that stores none can't be squeezed.</summary>
    public static IReadOnlyList<string> Biggest(IReadOnlyDictionary<string, long> stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        return [.. stored.Where(s => s.Value > 0).OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal).Take(MaxSongs).Select(s => s.Key)];
    }

    /// <summary>XGM2's PCM blocks are 256 bytes (docs/formats/xgm.md), so a budget is a multiple of it.</summary>
    private const int BlockBytes = 256;

    /// <summary>Every way, in the order the tables list them.</summary>
    public static IReadOnlyList<SqueezeWay> Ways { get; } = Enum.GetValues<SqueezeWay>();

    /// <summary>The way's name, in <c>squeeze.tsv</c> and the logs.</summary>
    public static string Name(SqueezeWay way) => way switch
    {
        SqueezeWay.Today => "today",
        SqueezeWay.NoShrink => "no shrinking",
        SqueezeWay.HalfSpeedAll => "half speed for all",
        SqueezeWay.DrumsFirst => "drums keep theirs first",
        _ => "tonal sounds keep theirs first",
    };

    /// <summary>The recipe of <paramref name="way"/> at <paramref name="budget"/> PCM bytes, as a recipe cell.</summary>
    public static string RecipeOf(SqueezeWay way, long budget)
    {
        string start = string.Create(CultureInfo.InvariantCulture, $"{PairPicker.DefaultRecipe} --max-pcm-bytes {budget}");
        return way switch
        {
            SqueezeWay.Today => start,
            SqueezeWay.NoShrink => start + " rule:shrink=off",
            SqueezeWay.HalfSpeedAll => start + " rule:half-speed=all",
            SqueezeWay.DrumsFirst => start + " rule:keep=drums",
            _ => start + " rule:keep=tonal",
        };
    }

    /// <summary>
    /// <paramref name="percent"/> % of <paramref name="storedBytes"/> (what today's version stores), rounded down to whole
    /// 256-byte blocks; 0 when that leaves no block, since <c>--max-pcm-bytes</c> needs a positive number.
    /// </summary>
    public static long Budget(long storedBytes, int percent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(storedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(percent);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(percent, 100);
        return storedBytes * percent / 100 / BlockBytes * BlockBytes;
    }

    /// <summary>The pairs one song puts to the round: today against each other way, the <see cref="MaxPairsPerSong"/> that differ most, ways that sound the same left out.</summary>
    public static IReadOnlyList<SqueezePair> Chosen(IEnumerable<SqueezePair> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        return [.. pairs.Where(p => p.DifferenceDb > 0).OrderByDescending(p => p.DifferenceDb).ThenBy(p => p.Second).Take(MaxPairsPerSong)];
    }

    /// <summary>The next round's rows for <paramref name="pairs"/>: the versions' order by a hash of the song and start, as every round.</summary>
    public static IReadOnlyList<ListeningRow> Rows(IReadOnlyList<SqueezePair> pairs, string round, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        List<ListeningRow> rows = [];
        foreach (SqueezePair p in pairs)
        {
            string first = RecipeOf(p.First, p.Budget);
            string second = RecipeOf(p.Second, p.Budget);
            bool swap = PairPicker.ChangedFirst(p.Song, p.StartSeconds);
            rows.Add(new ListeningRow(
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                round,
                "q" + (rows.Count + 1).ToString(CultureInfo.InvariantCulture),
                p.Song,
                p.StartSeconds,
                PairPicker.ClipSeconds,
                PairPicker.Format,
                swap ? second : first,
                swap ? first : second,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty));
        }

        return rows;
    }

    /// <summary><c>squeeze.tsv</c>: every pair tried, the chosen ones marked, by song and how far apart the ways sound.</summary>
    public static string Table(IEnumerable<SqueezePair> tried, IReadOnlySet<SqueezePair> chosen)
    {
        ArgumentNullException.ThrowIfNull(tried);
        ArgumentNullException.ThrowIfNull(chosen);
        StringBuilder table = new();
        table.AppendJoin('\t', "song", "budget", "first", "second", "start_s", "difference_db", "to_round").Append('\n');
        foreach (SqueezePair p in tried.OrderBy(p => p.Song, StringComparer.Ordinal).ThenByDescending(p => p.DifferenceDb))
        {
            table.AppendJoin(
                '\t',
                p.Song,
                p.Budget.ToString(CultureInfo.InvariantCulture),
                Name(p.First),
                Name(p.Second),
                p.StartSeconds.ToString("0.##", CultureInfo.InvariantCulture),
                p.DifferenceDb.ToString("0.###", CultureInfo.InvariantCulture),
                chosen.Contains(p) ? "yes" : "no").Append('\n');
        }

        return table.ToString();
    }
}

/// <summary>
/// <c>--squeeze</c>: for each song, today's version without a budget, then every way at <c>--budget</c> % of what it
/// stores, each compared with today's way at that budget at the clip where they differ most (docs/harness.md#squeeze-rounds).
/// </summary>
/// <param name="songs">The songs to squeeze.</param>
/// <param name="outputDirectory">Where <c>listening/squeeze.tsv</c>, <c>listening/squeeze-round.tsv</c> and the renders go.</param>
/// <param name="logger">Where the per-song results go.</param>
internal sealed class SqueezeRun(IReadOnlyList<string> songs, string outputDirectory, ILogger logger)
{
    /// <summary>Makes a squeeze round at <paramref name="percent"/> % of each song's PCM bytes; always 0.</summary>
    public int Run(string answersPath, int percent, DateOnly date, int jobs, IConversionProgress progress)
    {
        ArgumentNullException.ThrowIfNull(answersPath);
        ArgumentNullException.ThrowIfNull(progress);
        IReadOnlyList<ListeningRow> answered = AnswersFile.Parse(File.ReadAllText(answersPath));
        progress.Begin(new RunInfo(answersPath, RunInfo.HarnessFormat, "normal", [.. songs.Select((s, i) => new PhaseInfo(i + 1, "song:" + Path.GetFileName(s), Path.GetFileName(s), string.Empty))], Math.Max(1, Math.Min(jobs, songs.Count))));
        progress.SetSongSize(SongSize.Rows, 1);
        string listening = Path.Combine(outputDirectory, "listening");
        string folder = Path.Combine(listening, "squeeze");
        Directory.CreateDirectory(folder);
        ParallelOptions parallel = new() { MaxDegreeOfParallelism = Math.Max(1, jobs) };

        // Today's version of every song first: only the biggest are squeezed (docs/adr/0113-squeeze-rounds.md).
        ConcurrentDictionary<string, long> stored = [];
        ConcurrentDictionary<string, IPhase> phases = [];
        Parallel.ForEach(songs, parallel, song =>
        {
            phases[song] = progress.Start("song:" + Path.GetFileName(song));
            try
            {
                stored[song] = Stored(song, folder);
            }
            catch (Exception ex) when (ex is HarnessException or ConversionException or IOException)
            {
                logger.Warning("{Song:l}: no squeeze pairs ({Reason:l})", Path.GetFileName(song), ex.Message);
            }
        });

        string[] biggest = [.. Squeeze.Biggest(stored)];
        foreach (string song in songs.Except(biggest))
        {
            phases[song].Done();
        }

        ConcurrentDictionary<string, IReadOnlyList<SqueezePair>> tried = [];
        Parallel.ForEach(biggest, parallel, song =>
        {
            try
            {
                tried[song] = Song(song, stored[song], percent, folder);
            }
            catch (Exception ex) when (ex is HarnessException or ConversionException or IOException)
            {
                logger.Warning("{Song:l}: no squeeze pairs ({Reason:l})", Path.GetFileName(song), ex.Message);
            }

            phases[song].Done();
        });

        SqueezePair[] all = [.. biggest.Where(tried.ContainsKey).SelectMany(s => tried[s])];
        SqueezePair[] chosen = [.. biggest.Where(tried.ContainsKey).SelectMany(s => Squeeze.Chosen(tried[s]))];
        string round = PairPicker.NextRound(answered);
        IReadOnlyList<ListeningRow> rows = Squeeze.Rows(chosen, round, date);
        File.WriteAllText(Path.Combine(listening, "squeeze.tsv"), Squeeze.Table(all, chosen.ToHashSet()));
        File.WriteAllText(Path.Combine(listening, "squeeze-round.tsv"), AnswersFile.Format(rows));
        foreach (string song in songs.Where(stored.ContainsKey).OrderByDescending(s => stored[s]))
        {
            logger.Information("{Song:l}: {Bytes} PCM bytes today{Squeezed:l}", Path.GetFileName(song), stored[song], biggest.Contains(song) ? ", squeezed" : string.Empty);
        }

        logger.ForContext(new ReportRow("result", ("output", listening), ("songs", biggest.Length), ("questions", rows.Count))).Information(
            "Squeeze round at {Percent} %: {Questions} questions for {Round:l} in {Folder}",
            percent,
            rows.Count,
            round,
            Path.Combine(listening, "squeeze-round.tsv"));
        return 0;
    }

    /// <summary>The PCM bytes today's version of the song stores, made once without a budget.</summary>
    private static long Stored(string songPath, string folder)
    {
        string song = Path.GetFileName(songPath);
        string stem = Path.Combine(folder, Path.GetFileNameWithoutExtension(song));
        ListeningRemake.Render(songPath, PairPicker.Format, PairPicker.DefaultRecipe, stem + "-full", null, song);
        return PairPicker.StoredBytes(File.ReadAllBytes(stem + "-full.out"));
    }

    /// <summary>One song: its budget, every way at it and each way's pair with today's.</summary>
    private List<SqueezePair> Song(string songPath, long stored, int percent, string folder)
    {
        string song = Path.GetFileName(songPath);
        string stem = Path.Combine(folder, Path.GetFileNameWithoutExtension(song));
        long budget = Squeeze.Budget(stored, percent);
        if (budget == 0)
        {
            logger.Warning("{Song:l}: stores {Bytes} PCM bytes, so {Percent} % leaves no 256-byte block", song, stored, percent);
            return [];
        }

        Dictionary<SqueezeWay, byte[]> renders = [];
        foreach (SqueezeWay way in Squeeze.Ways)
        {
            try
            {
                renders[way] = File.ReadAllBytes(ListeningRemake.Render(songPath, PairPicker.Format, Squeeze.RecipeOf(way, budget), stem + "-" + way.ToString().ToLowerInvariant(), null, song));
            }
            catch (Exception ex) when (ex is HarnessException or ConversionException)
            {
                logger.Warning("{Song:l}: {Way:l} made no render ({Reason:l})", song, Squeeze.Name(way), ex.Message);
            }
        }

        List<SqueezePair> pairs = [];
        if (!renders.TryGetValue(SqueezeWay.Today, out byte[]? today))
        {
            return pairs;
        }

        StereoAudio whole = WavFile.Read(today);
        double seconds = whole.Left.Length / (double)whole.SampleRate;
        foreach ((SqueezeWay way, byte[] render) in renders.Where(r => r.Key != SqueezeWay.Today))
        {
            (double start, double db) = Widest(today, render, PairPicker.Starts(seconds));
            pairs.Add(new SqueezePair(song, SqueezeWay.Today, way, budget, start, db));
            logger.Information("{Song:l} at {Budget} bytes: {Way:l} is {Db:0.##} dB from today's way at {Start:0.#} s", song, budget, Squeeze.Name(way), db, start);
        }

        return pairs;
    }

    /// <summary>The clip start where <paramref name="a"/> and <paramref name="b"/> (whole-song WAV files) differ most, and that log-mel distance; 0 dB when they are the same.</summary>
    private static (double Start, double Db) Widest(byte[] a, byte[] b, IReadOnlyList<double> starts)
    {
        (double Start, double Db) widest = (starts.Count > 0 ? starts[0] : 0, 0);
        foreach (double start in starts)
        {
            byte[] clipA = ClipWav.Cut(a, start, PairPicker.ClipSeconds);
            byte[] clipB = ClipWav.Cut(b, start, PairPicker.ClipSeconds);
            if (clipA.AsSpan().SequenceEqual(clipB))
            {
                continue;
            }

            double db = SpectralMetrics.MelDistanceDb(WavFile.Read(clipA).ToMono(), WavFile.Read(clipB).ToMono());
            if (db > widest.Db)
            {
                widest = (start, db);
            }
        }

        return widest;
    }
}
