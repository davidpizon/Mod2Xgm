using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mod2Xgm.Analysis;
using Mod2Xgm.Optimizer;
using Mod2Xgm.Output;
using Mod2Xgm.Render;
using Mod2Xgm.Scoring;
using Serilog;

namespace Mod2Xgm.Harness;

/// <summary>The one change a picked pair makes to the default version (docs/adr/0102-pair-picker.md).</summary>
internal enum PairKind
{
    /// <summary><c>--mapping simple</c> instead of the optimizer.</summary>
    Simple,

    /// <summary><c>--max-pcm-bytes</c> at half of what the default version stores.</summary>
    Budget,

    /// <summary><c>--no-dac</c>: no recordings at all.</summary>
    NoDac,

    /// <summary>One dull sample stored at XGM2 half speed (<c>rule:half-speed</c>).</summary>
    HalfSpeed,
}

/// <summary>One pair the picker may ask about: a clip of the default version and the same clip with one change.</summary>
/// <param name="Song">The MOD's file name.</param>
/// <param name="Kind">The change.</param>
/// <param name="Recipe">The changed version's recipe.</param>
/// <param name="StartSeconds">Where the clip starts.</param>
/// <param name="Default">The default version's distances from the original's clip.</param>
/// <param name="Changed">The changed version's distances.</param>
internal sealed record PairCandidate(string Song, PairKind Kind, string Recipe, double StartSeconds, ListeningFeatures Default, ListeningFeatures Changed);

/// <summary>
/// Chooses the pairs the learned listening score is least sure of (plan T9.4k3, docs/adr/0102-pair-picker.md): without a
/// fit, those the seven measures disagree on most; with one, those whose chance is nearest ½.
/// </summary>
internal static class PairPicker
{
    /// <summary>The recipe of the default version every pair is compared with (David).</summary>
    public const string DefaultRecipe = "--mapping optimizer";

    /// <summary>The output format of every picked pair (David: as round 20; every change works there).</summary>
    public const string Format = "xgm2";

    /// <summary>How long each clip is, in seconds (David: as round 20).</summary>
    public const double ClipSeconds = 20;

    /// <summary>Seconds between the clip starts tried (David).</summary>
    public const double StartStepSeconds = 10;

    /// <summary>Most pairs of one kind in a round (David).</summary>
    public const int MaxPerKind = 3;

    /// <summary>A sample is dull when at most this share of its energy is above <see cref="HalfSpeedTopHz"/> (Claude).</summary>
    public const double DullShare = 0.01;

    /// <summary>Most dull samples of one song offered at half speed: the dullest first.</summary>
    public const int MaxHalfSpeedSamples = 2;

    /// <summary>XGM2's PCM blocks are 256 bytes (docs/formats/xgm.md), so a budget is a multiple of it.</summary>
    private const int BlockBytes = 256;

    /// <summary>SLEN, the sample block's size / 256, in the XGM2 header (docs/formats/xgm.md#xgm2-layout-from-xgm2txt-version-byte-0x10).</summary>
    private const int SlenOffset = 6;

    /// <summary>The highest frequency XGM2 half speed keeps: half its 6.65 kHz rate.</summary>
    public static double HalfSpeedTopHz { get; } = PcmTargets.PlayRateHz(PcmTarget.Xgm2, 1, halfSpeed: true) / 2;

    /// <summary>The kind's name in <c>candidates.tsv</c>.</summary>
    public static string Name(PairKind kind) => kind switch
    {
        PairKind.Simple => "simple",
        PairKind.Budget => "budget",
        PairKind.NoDac => "no-dac",
        _ => "half-speed",
    };

    /// <summary>The recipe of the change, as a recipe cell; <paramref name="value"/> is the budget in bytes or the sample (1-based).</summary>
    public static string RecipeOf(PairKind kind, long value = 0) => kind switch
    {
        PairKind.Simple => "--mapping simple",
        PairKind.Budget => string.Create(CultureInfo.InvariantCulture, $"{DefaultRecipe} --max-pcm-bytes {value}"),
        PairKind.NoDac => DefaultRecipe + " --no-dac",
        _ => string.Create(CultureInfo.InvariantCulture, $"{DefaultRecipe} rule:half-speed={value}"),
    };

    /// <summary>Half the PCM bytes an XGM2 file stores (its SLEN), rounded down to 256; 0 when it stores none.</summary>
    public static long HalfBudget(ReadOnlySpan<byte> xgm2)
    {
        if (xgm2.Length < SlenOffset + 2)
        {
            throw new HarnessException("not an XGM2 file: shorter than its header");
        }

        long bytes = BinaryPrimitives.ReadUInt16LittleEndian(xgm2[SlenOffset..]) * (long)BlockBytes;
        return bytes / 2 / BlockBytes * BlockBytes;
    }

    /// <summary>
    /// The share of <paramref name="spectrum"/>'s energy, over every frame, in mel bands centred above
    /// <see cref="HalfSpeedTopHz"/>; 1 for silence, so silence is never dull.
    /// </summary>
    public static double ShareAbove(RegionSpectrum spectrum)
    {
        ArgumentNullException.ThrowIfNull(spectrum);
        double total = 0;
        double above = 0;
        foreach (float[] frame in spectrum.MelBandsDb)
        {
            for (int b = 0; b < frame.Length; b++)
            {
                double power = frame[b] <= SampleAnalyzer.FloorDb ? 0 : Math.Pow(10, frame[b] / 10);
                total += power;
                if (BandCentreHz(b, frame.Length) > HalfSpeedTopHz)
                {
                    above += power;
                }
            }
        }

        return total > 0 ? above / total : 1;
    }

    /// <summary>
    /// The samples (0-based) of <paramref name="samples"/> dull enough for a half-speed pair, the dullest first, at most
    /// <see cref="MaxHalfSpeedSamples"/>: those whose most-played region has at most <see cref="DullShare"/> above
    /// <see cref="HalfSpeedTopHz"/>. Silent samples and samples the song never plays are left out.
    /// </summary>
    public static IReadOnlyList<int> DullSamples(IEnumerable<SampleAnalysis> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return [.. samples
            .Where(s => s.Kind != SampleKind.Silent && s.Regions.Sum(r => r.Notes) > 0)
            .Select(s => (s.Sample, Share: ShareAbove(s.Regions.MaxBy(r => r.Notes)!.Spectrum)))
            .Where(s => s.Share <= DullShare)
            .OrderBy(s => s.Share)
            .ThenBy(s => s.Sample)
            .Take(MaxHalfSpeedSamples)
            .Select(s => s.Sample)];
    }

    /// <summary>
    /// How sure the score is of <paramref name="pair"/>, 0 (a coin) to 1 (certain). With <paramref name="weights"/>, twice
    /// the distance of the chance the change wins from ½; without, how lopsided the measures' votes are:
    /// <c>|for default − for change| / votes cast</c>, 1 when none votes.
    /// </summary>
    public static double Sureness(PairCandidate pair, IReadOnlyList<double> weights)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(weights);
        if (weights.Count > 0)
        {
            double z = ListeningScore.Of(pair.Default, weights) - ListeningScore.Of(pair.Changed, weights);
            return Math.Abs((2 / (1 + Math.Exp(-z))) - 1);
        }

        IReadOnlyList<double> d = pair.Default.Values;
        IReadOnlyList<double> c = pair.Changed.Values;
        int forDefault = 0;
        int forChange = 0;
        for (int j = 0; j < d.Count; j++)
        {
            forDefault += d[j] < c[j] ? 1 : 0;
            forChange += c[j] < d[j] ? 1 : 0;
        }

        int votes = forDefault + forChange;
        return votes == 0 ? 1 : Math.Abs(forDefault - forChange) / (double)votes;
    }

    /// <summary>
    /// The <paramref name="count"/> pairs least sure first (<see cref="Sureness"/>, ties to the smaller log-mel difference),
    /// one per song and at most <see cref="MaxPerKind"/> per kind; songs in <paramref name="asked"/> only when the others
    /// run out.
    /// </summary>
    public static IReadOnlyList<PairCandidate> Pick(IEnumerable<PairCandidate> candidates, int count, IReadOnlySet<string> asked, IReadOnlyList<double> weights)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(asked);
        PairCandidate[] ordered = [.. Order(candidates, weights)];
        List<PairCandidate> picked = [];
        HashSet<string> songs = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<PairKind, int> kinds = [];
        foreach (bool askedToo in new[] { false, true })
        {
            foreach (PairCandidate pair in ordered)
            {
                if (picked.Count == count)
                {
                    return picked;
                }

                if ((!askedToo && asked.Contains(pair.Song)) || songs.Contains(pair.Song) || kinds.GetValueOrDefault(pair.Kind) >= MaxPerKind)
                {
                    continue;
                }

                picked.Add(pair);
                songs.Add(pair.Song);
                kinds[pair.Kind] = kinds.GetValueOrDefault(pair.Kind) + 1;
            }
        }

        return picked;
    }

    /// <summary>The candidates least sure first, ties to the smaller log-mel difference, then by song, kind, recipe and start.</summary>
    public static IEnumerable<PairCandidate> Order(IEnumerable<PairCandidate> candidates, IReadOnlyList<double> weights)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(weights);
        return candidates
            .OrderBy(p => Sureness(p, weights))
            .ThenBy(p => Math.Abs(p.Default.MelDb - p.Changed.MelDb))
            .ThenBy(p => p.Song, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Kind)
            .ThenBy(p => p.Recipe, StringComparer.Ordinal)
            .ThenBy(p => p.StartSeconds);
    }

    /// <summary>
    /// The round's rows for <paramref name="picked"/>, without hashes or answers: questions <c>q1</c> on, and A the
    /// changed version when the first byte of the SHA-256 of the song's name and start is odd.
    /// </summary>
    public static IReadOnlyList<ListeningRow> Rows(IReadOnlyList<PairCandidate> picked, string round, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(picked);
        return [.. picked.Select((p, i) =>
        {
            bool changedFirst = ChangedFirst(p.Song, p.StartSeconds);
            return new ListeningRow(
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                round,
                "q" + (i + 1).ToString(CultureInfo.InvariantCulture),
                p.Song,
                p.StartSeconds,
                ClipSeconds,
                Format,
                changedFirst ? p.Recipe : DefaultRecipe,
                changedFirst ? DefaultRecipe : p.Recipe,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);
        })];
    }

    /// <summary>The round after the highest <c>r&lt;n&gt;</c> in <paramref name="rows"/>; <c>r1</c> when there is none.</summary>
    public static string NextRound(IEnumerable<ListeningRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int highest = rows
            .Select(r => r.Round.StartsWith('r') && int.TryParse(r.Round.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
        return "r" + (highest + 1).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Clip starts every <see cref="StartStepSeconds"/> whose whole <see cref="ClipSeconds"/> fits in <paramref name="songSeconds"/>.</summary>
    public static IReadOnlyList<double> Starts(double songSeconds)
    {
        List<double> starts = [];
        for (double start = 0; start + ClipSeconds <= songSeconds; start += StartStepSeconds)
        {
            starts.Add(start);
        }

        return starts;
    }

    /// <summary><c>candidates.tsv</c>: every candidate, least sure first, with its sureness and both versions' features.</summary>
    public static string CandidatesTable(IEnumerable<PairCandidate> candidates, IReadOnlyList<double> weights)
    {
        StringBuilder table = new("song\tkind\trecipe\tstart_s\tsureness");
        foreach (string name in ListeningFeatures.Names)
        {
            table.Append("\tdefault_").Append(name);
        }

        foreach (string name in ListeningFeatures.Names)
        {
            table.Append("\tchanged_").Append(name);
        }

        table.Append('\n');
        foreach (PairCandidate p in Order(candidates, weights))
        {
            table.AppendJoin('\t', p.Song, Name(p.Kind), p.Recipe, Number(p.StartSeconds), Number(Sureness(p, weights)));
            foreach (double v in p.Default.Values.Concat(p.Changed.Values))
            {
                table.Append('\t').Append(Number(v));
            }

            table.Append('\n');
        }

        return table.ToString();
    }

    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static bool ChangedFirst(string song, double start)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(song.ToUpperInvariant() + "@" + start.ToString("R", CultureInfo.InvariantCulture)));
        return (hash[0] & 1) == 1;
    }

    /// <summary>The centre of mel band <paramref name="band"/> of <paramref name="bands"/> from 0 Hz to half <see cref="SampleAnalyzer.AnalysisRateHz"/>, as <see cref="MelAnalyzer"/> spaces them.</summary>
    private static double BandCentreHz(int band, int bands)
    {
        double top = 2595 * Math.Log10(1 + (SampleAnalyzer.AnalysisRateHz / 2.0 / 700));
        double mel = top * (band + 1) / (bands + 1);
        return 700 * (Math.Pow(10, mel / 2595) - 1);
    }
}

/// <summary>
/// <c>--pick</c> (docs/harness.md#picking-a-round): renders every song's default version and its changes, scores each
/// clip start of each, picks the pairs and writes <c>listening/picked.tsv</c> and <c>listening/candidates.tsv</c>.
/// </summary>
/// <param name="songs">The harness's inputs.</param>
/// <param name="outputDirectory">Where <c>listening/</c> goes.</param>
/// <param name="reference">Renders the originals.</param>
/// <param name="logger">Where each song's result is logged.</param>
internal sealed class PairPickRun(IReadOnlyList<string> songs, string outputDirectory, IReferenceRenderer reference, ILogger logger)
{
    /// <summary>The rate the converter's <c>--render-wav</c> writes.</summary>
    private const int ClipRate = 44_100;

    /// <summary>Picks <paramref name="count"/> pairs, given the answers file at <paramref name="answersPath"/>; always 0.</summary>
    public int Run(string answersPath, int count, DateOnly date, int jobs, IConversionProgress progress)
    {
        ArgumentNullException.ThrowIfNull(answersPath);
        ArgumentNullException.ThrowIfNull(progress);
        IReadOnlyList<ListeningRow> answered = AnswersFile.Parse(File.ReadAllText(answersPath));
        HashSet<string> asked = new(answered.Select(r => r.Song), StringComparer.OrdinalIgnoreCase);
        progress.Begin(new RunInfo(answersPath, RunInfo.HarnessFormat, "normal", [.. songs.Select((s, i) => new PhaseInfo(i + 1, CorpusRun.PhaseId(s), Path.GetFileNameWithoutExtension(s), string.Empty))], Math.Min(jobs, songs.Count)));
        progress.SetSongSize(SongSize.Rows, 1);
        string folder = Path.Combine(outputDirectory, "listening");
        Directory.CreateDirectory(Path.Combine(folder, "work"));
        ConcurrentBag<PairCandidate> candidates = [];
        Parallel.ForEach(songs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, jobs) }, song =>
        {
            IPhase phase = progress.Start(CorpusRun.PhaseId(song));
            try
            {
                foreach (PairCandidate c in Song(song, folder))
                {
                    candidates.Add(c);
                }
            }
            catch (Exception ex) when (ex is HarnessException or ConversionException or IOException)
            {
                logger.Warning("{Song:l}: no pairs ({Reason:l})", Path.GetFileName(song), ex.Message);
            }

            phase.Done();
        });

        IReadOnlyList<double> weights = ListeningWeights.Weights;
        IReadOnlyList<PairCandidate> picked = PairPicker.Pick(candidates, count, asked, weights);
        string round = PairPicker.NextRound(answered);
        File.WriteAllText(Path.Combine(folder, "candidates.tsv"), PairPicker.CandidatesTable(candidates, weights));
        File.WriteAllText(Path.Combine(folder, "picked.tsv"), AnswersFile.Format(PairPicker.Rows(picked, round, date)));
        foreach (PairCandidate p in picked)
        {
            logger.Information(
                "{Round:l}: {Song:l} from {Start} s, {Kind:l}, sureness {Sureness}",
                round,
                p.Song,
                p.StartSeconds,
                PairPicker.Name(p.Kind),
                PairPicker.Sureness(p, weights).ToString("0.###", CultureInfo.InvariantCulture));
        }

        logger.ForContext(new ReportRow("result", ("output", folder), ("picked", picked.Count), ("candidates", candidates.Count))).Information(
            "Picked {Picked} of {Candidates} pairs ({Rule:l}) into {Folder}",
            picked.Count,
            candidates.Count,
            weights.Count > 0 ? "the fit's chance nearest ½" : "the measures' votes most split",
            Path.Combine(folder, "picked.tsv"));
        return 0;
    }

    /// <summary>Every candidate pair of one song: each change against the default version at each clip start.</summary>
    private List<PairCandidate> Song(string songPath, string folder)
    {
        string song = Path.GetFileName(songPath);
        string stem = Path.Combine(folder, "work", "pick-" + Path.GetFileNameWithoutExtension(song));
        SongAnalysis? analysis = null;
        string defaultWav = ListeningRemake.Render(songPath, PairPicker.Format, PairPicker.DefaultRecipe, stem + "-default", a => analysis = a, song);
        byte[] defaultRender = File.ReadAllBytes(defaultWav);
        List<(PairKind Kind, string Recipe)> changes = [(PairKind.Simple, PairPicker.RecipeOf(PairKind.Simple)), (PairKind.NoDac, PairPicker.RecipeOf(PairKind.NoDac))];
        long budget = PairPicker.HalfBudget(File.ReadAllBytes(stem + "-default.out"));
        if (budget > 0)
        {
            changes.Add((PairKind.Budget, PairPicker.RecipeOf(PairKind.Budget, budget)));
        }

        if (analysis is not null)
        {
            changes.AddRange(PairPicker.DullSamples(analysis.Samples).Select(s => (PairKind.HalfSpeed, PairPicker.RecipeOf(PairKind.HalfSpeed, s + 1))));
        }

        StereoAudio original = reference.Render(File.ReadAllBytes(songPath), ClipRate).Audio;
        IReadOnlyList<double> starts = PairPicker.Starts(original.Left.Length / (double)ClipRate);
        Dictionary<double, (byte[] Clip, ListeningFeatures Features)> defaults = [];
        List<PairCandidate> pairs = [];
        foreach ((PairKind kind, string recipe) in changes)
        {
            byte[] render;
            try
            {
                render = File.ReadAllBytes(ListeningRemake.Render(songPath, PairPicker.Format, recipe, stem + "-" + PairPicker.Name(kind) + pairs.Count.ToString(CultureInfo.InvariantCulture), null, song));
            }
            catch (Exception ex) when (ex is HarnessException or ConversionException)
            {
                logger.Warning("{Song:l}: \"{Recipe:l}\" made no render ({Reason:l})", song, recipe, ex.Message);
                continue;
            }

            int before = pairs.Count;
            foreach (double start in starts)
            {
                byte[] changed = ClipWav.Cut(render, start, PairPicker.ClipSeconds);
                if (!defaults.TryGetValue(start, out var d))
                {
                    byte[] clip = ClipWav.Cut(defaultRender, start, PairPicker.ClipSeconds);
                    d = (clip, Features(original, clip, start));
                    defaults[start] = d;
                }

                if (changed.AsSpan().SequenceEqual(d.Clip))
                {
                    continue;
                }

                pairs.Add(new PairCandidate(song, kind, recipe, start, d.Features, Features(original, changed, start)));
            }

            logger.Information("{Song:l}: {Recipe:l}, {Count} of {Starts} starts differ", song, recipe, pairs.Count - before, starts.Count);
        }

        return pairs;
    }

    private static ListeningFeatures Features(StereoAudio original, byte[] clip, double start)
    {
        int from = (int)Math.Round(start * ClipRate);
        int length = Math.Min(original.Left.Length - from, (int)Math.Round(PairPicker.ClipSeconds * ClipRate));
        StereoAudio part = new(original.Left.AsSpan(from, length).ToArray(), original.Right.AsSpan(from, length).ToArray(), ClipRate);
        return ListeningFeatures.From(ListeningRemake.Measure(part, clip))
            ?? throw new HarnessException("the scorer made no perceptual features");
    }
}
