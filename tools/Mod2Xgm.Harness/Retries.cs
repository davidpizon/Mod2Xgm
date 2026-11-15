using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Mod2Xgm.Analysis;
using Mod2Xgm.Output;
using Mod2Xgm.Render;
using Serilog;

namespace Mod2Xgm.Harness;

/// <summary>The kinds of fix a retry tries at a place David rejected (David, 2026-11-14, docs/adr/0112-retry-list.md).</summary>
internal enum RetryKind
{
    /// <summary>One sound that plays there must stay a recording (<c>--require-dac</c>), a less important one left out if there's no room.</summary>
    KeepRecording,

    /// <summary>No pre-mixes (<c>rule:pre-mix=off</c>).</summary>
    NoPreMix,

    /// <summary>No layers: no PSG tone or second FM voice under a note (<c>rule:layers=off</c>).</summary>
    NoLayers,

    /// <summary>Neither pre-mixes nor layers.</summary>
    NoPreMixNoLayers,

    /// <summary>The loudest recording there plays quieter (<c>rule:quieter</c>).</summary>
    RecordingQuieter,

    /// <summary>The whole song plays quieter (<c>rule:song-quieter</c>).</summary>
    SongQuieter,
}

/// <summary>
/// A place a <c>neither</c> answer sent back (docs/adr/0111-neither-and-listening-models.md): one song's clip, and the rows
/// whose versions were both rejected there.
/// </summary>
/// <param name="Song">The MOD's file name.</param>
/// <param name="StartSeconds">Where the clip starts.</param>
/// <param name="Seconds">How long it is.</param>
/// <param name="Format">The <c>--format</c> it was heard in.</param>
/// <param name="Rejected">Its <c>neither</c> rows, in the answers file's order.</param>
internal sealed record RetryPlace(string Song, double StartSeconds, double Seconds, string Format, IReadOnlyList<ListeningRow> Rejected)
{
    /// <summary>The place in logs and file names: the song's stem and the start, <c>hit@170</c>.</summary>
    public string Name => Path.GetFileNameWithoutExtension(Song) + "@" + StartSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The hashes of every clip David rejected there.</summary>
    public IReadOnlySet<string> RejectedClips => Rejected.SelectMany(r => new[] { r.Sha256A, r.Sha256B }).Where(h => h.Length > 0).ToHashSet(StringComparer.Ordinal);
}

/// <summary>One try at a place: its kind, its recipe, and what it changes in words.</summary>
/// <param name="Kind">The kind of fix.</param>
/// <param name="Recipe">How it is made (<see cref="Recipe"/>).</param>
/// <param name="What">What it changes, for the table.</param>
internal sealed record RetryTry(RetryKind Kind, string Recipe, string What);

/// <summary>A try once made and measured.</summary>
/// <param name="Try">The try.</param>
/// <param name="Distance">Its clip's CLAP distance from the original's clip; smaller is closer.</param>
/// <param name="SameAsDefault">True when its clip is byte for byte today's default's: it changes nothing there.</param>
internal sealed record RetryOutcome(RetryTry Try, double Distance, bool SameAsDefault);

/// <summary>What the retry list chose for one place.</summary>
/// <param name="Place">The place.</param>
/// <param name="DefaultRejected">True when today's default makes a clip David rejected there: the place is still as bad.</param>
/// <param name="RejectedDistance">The CLAP distance of the closest clip David rejected: a try as far or further is screened out.</param>
/// <param name="DefaultDistance">Today's default's CLAP distance.</param>
/// <param name="Outcomes">Every try made, closest first.</param>
/// <param name="Chosen">The tries put to the next round, closest first.</param>
/// <param name="Doubted">True when no try passed the screen, so the closest tries were put to the round anyway (David).</param>
internal sealed record RetryChoice(RetryPlace Place, bool DefaultRejected, double RejectedDistance, double DefaultDistance, IReadOnlyList<RetryOutcome> Outcomes, IReadOnlyList<RetryOutcome> Chosen, bool Doubted);

/// <summary>
/// The retry list (plan T9.4s2, docs/adr/0112-retry-list.md): for each place whose last answer was <c>neither</c>, different
/// kinds of fix from today's default, screened by CLAP against the clips David rejected there, the most promising put to
/// the next round.
/// </summary>
internal static class RetryList
{
    /// <summary>The sounds that play most at a place each get a <see cref="RetryKind.KeepRecording"/> try: at most this many.</summary>
    public const int MaxKept = 3;

    /// <summary>How many dB quieter <see cref="RetryKind.RecordingQuieter"/> and <see cref="RetryKind.SongQuieter"/> try: a little and clearly (David chose both).</summary>
    public static IReadOnlyList<double> QuieterDb { get; } = [3, 6];

    /// <summary>
    /// The places on the list: grouped by song (any case), start, length and format, those whose last answered row is
    /// <c>neither</c>. A later answer of <c>a</c>, <c>b</c> or <c>same</c> there means a retry passed, and takes it off.
    /// </summary>
    public static IReadOnlyList<RetryPlace> Places(IReadOnlyList<ListeningRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return [.. rows
            .Where(r => r.Pick.Length > 0)
            .GroupBy(r => (Song: r.Song.ToUpperInvariant(), r.StartSeconds, r.Seconds, Format: r.Format.ToUpperInvariant()))
            .Where(g => g.Last().Pick == "neither")
            .Select(g => new RetryPlace(g.First().Song, g.Key.StartSeconds, g.Key.Seconds, g.First().Format, [.. g.Where(r => r.Pick == "neither")]))];
    }

    /// <summary>
    /// How much each sample sounds in the clip from <paramref name="start"/> for <paramref name="seconds"/>, loudest first:
    /// for each of its notes, the seconds it plays inside the clip times its MOD volume times its sample's RMS level. Silent
    /// samples are left out; ties go to the lower sample number.
    /// </summary>
    public static IReadOnlyList<(int Sample, double Amount)> Sounding(SongAnalysis analysis, double start, double seconds)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        Dictionary<int, double> rms = [];
        Dictionary<int, double> amount = [];
        double end = start + seconds;
        foreach (SongNote note in analysis.Notes)
        {
            double from = analysis.Playback.Ticks[note.FirstTick].TimeSeconds;
            double overlap = Math.Min(end, from + note.Seconds) - Math.Max(start, from);
            if (overlap <= 0 || note.Volume <= 0)
            {
                continue;
            }

            if (!rms.TryGetValue(note.Sample, out double level))
            {
                float[] data = analysis.Song.Samples[note.Sample].Data;
                level = data.Length == 0 ? 0 : Math.Sqrt(data.Sum(x => (double)x * x) / data.Length);
                rms[note.Sample] = level;
            }

            amount[note.Sample] = amount.GetValueOrDefault(note.Sample) + (overlap * note.Volume * level);
        }

        return [.. amount.Where(a => a.Value > 0).OrderByDescending(a => a.Value).ThenBy(a => a.Key).Select(a => (a.Key, a.Value))];
    }

    /// <summary>
    /// Every try at a place, from today's default (<see cref="PairPicker.DefaultRecipe"/>): each of the
    /// <see cref="MaxKept"/> loudest sounds kept a recording, pre-mixes and layers off, the loudest sound and the whole song
    /// <see cref="QuieterDb"/> quieter. <paramref name="sounding"/> is <see cref="Sounding"/>'s list; sample numbers in
    /// recipes are 1-based.
    /// </summary>
    public static IReadOnlyList<RetryTry> Tries(IReadOnlyList<(int Sample, double Amount)> sounding)
    {
        ArgumentNullException.ThrowIfNull(sounding);
        string d = PairPicker.DefaultRecipe;
        List<RetryTry> tries = [];
        foreach ((int sample, _) in sounding.Take(MaxKept))
        {
            string n = Number(sample + 1);
            tries.Add(new RetryTry(RetryKind.KeepRecording, $"{d} --require-dac {n}", $"sample {n} kept a recording"));
        }

        tries.Add(new RetryTry(RetryKind.NoPreMix, $"{d} rule:pre-mix=off", "no pre-mixes"));
        tries.Add(new RetryTry(RetryKind.NoLayers, $"{d} rule:layers=off", "no layers"));
        tries.Add(new RetryTry(RetryKind.NoPreMixNoLayers, $"{d} rule:pre-mix=off rule:layers=off", "no pre-mixes or layers"));
        if (sounding.Count > 0)
        {
            string loudest = Number(sounding[0].Sample + 1);
            foreach (double db in QuieterDb)
            {
                tries.Add(new RetryTry(RetryKind.RecordingQuieter, $"{d} rule:quieter={loudest}:{Number(db)}", $"sample {loudest} {Number(db)} dB quieter"));
            }
        }

        foreach (double db in QuieterDb)
        {
            tries.Add(new RetryTry(RetryKind.SongQuieter, $"{d} rule:song-quieter={Number(db)}", $"the whole song {Number(db)} dB quieter"));
        }

        return tries;
    }

    /// <summary>
    /// A <see cref="RetryKind.KeepRecording"/> try that found no room (exit 6) again with the sound that plays least at the
    /// place, of those it doesn't keep, left out; <c>null</c> when there is no other sound, or it is no such try.
    /// </summary>
    public static RetryTry? WithRoom(RetryTry keep, IReadOnlyList<(int Sample, double Amount)> sounding)
    {
        ArgumentNullException.ThrowIfNull(keep);
        ArgumentNullException.ThrowIfNull(sounding);
        if (keep.Kind != RetryKind.KeepRecording || keep.Recipe.Contains("--ignore-samples", StringComparison.Ordinal))
        {
            return null;
        }

        string kept = keep.Recipe[(keep.Recipe.LastIndexOf(' ') + 1)..];
        (int Sample, double Amount)[] others = [.. sounding.Where(s => Number(s.Sample + 1) != kept)];
        if (others.Length == 0)
        {
            return null;
        }

        string least = Number(others[^1].Sample + 1);
        return keep with { Recipe = $"{keep.Recipe} --ignore-samples {least}", What = $"{keep.What}, sample {least} left out" };
    }

    /// <summary>
    /// Screens and ranks one place's tries: a try that changes nothing is dropped, one as far from the MOD as
    /// <paramref name="rejectedDistance"/> or further is screened out, and the closest <paramref name="count"/> left are
    /// chosen. When none passes, the closest are chosen anyway and the choice is marked doubted (David, 2026-11-14).
    /// </summary>
    public static (IReadOnlyList<RetryOutcome> Chosen, bool Doubted) Choose(IReadOnlyList<RetryOutcome> outcomes, double rejectedDistance, int count)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        RetryOutcome[] changed = [.. Ranked(outcomes).Where(o => !o.SameAsDefault)];
        RetryOutcome[] passed = [.. changed.Where(o => o.Distance < rejectedDistance)];
        return passed.Length > 0 ? ([.. passed.Take(count)], false) : ([.. changed.Take(count)], changed.Length > 0);
    }

    /// <summary>Closest first; ties in the order the tries were made.</summary>
    public static IReadOnlyList<RetryOutcome> Ranked(IReadOnlyList<RetryOutcome> outcomes) =>
        [.. outcomes.Select((o, i) => (o, i)).OrderBy(x => x.o.Distance).ThenBy(x => x.i).Select(x => x.o)];

    /// <summary>
    /// How many tries a place puts to the round: two against each other where today's default is still a rejected clip,
    /// one against today's default where the default has changed since (David: check it in the round).
    /// </summary>
    public static int Wanted(bool defaultRejected) => defaultRejected ? 2 : 1;

    /// <summary>
    /// The next round's questions, one per place with a choice, the versions' order by a hash of the place: the two chosen
    /// tries, or today's default and the chosen try. A place with nothing to ask is left out.
    /// </summary>
    public static IReadOnlyList<ListeningRow> Rows(IReadOnlyList<RetryChoice> choices, string round, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(choices);
        List<ListeningRow> rows = [];
        foreach (RetryChoice c in choices)
        {
            (string first, string second) = c.DefaultRejected switch
            {
                true when c.Chosen.Count >= 2 => (c.Chosen[0].Try.Recipe, c.Chosen[1].Try.Recipe),
                false when c.Chosen.Count >= 1 => (PairPicker.DefaultRecipe, c.Chosen[0].Try.Recipe),
                _ => (string.Empty, string.Empty),
            };
            if (first.Length == 0)
            {
                continue;
            }

            bool swap = PairPicker.ChangedFirst(c.Place.Song, c.Place.StartSeconds);
            rows.Add(new ListeningRow(
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                round,
                "q" + Number(rows.Count + 1),
                c.Place.Song,
                c.Place.StartSeconds,
                c.Place.Seconds,
                c.Place.Format,
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

    /// <summary>
    /// <c>retries.tsv</c>: every try at every place, closest first, with its CLAP distance, whether it changes anything,
    /// passed the screen and went to the round.
    /// </summary>
    public static string Table(IReadOnlyList<RetryChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        StringBuilder table = new("place\tsong\tstart_s\tdefault\tkind\twhat\trecipe\tclap_distance\trejected_distance\tchanged\tpassed\tchosen\n");
        foreach (RetryChoice c in choices)
        {
            string state = c.DefaultRejected ? "rejected" : "changed";
            table.AppendJoin('\t', c.Place.Name, c.Place.Song, Number(c.Place.StartSeconds), state, "default", "today's default", PairPicker.DefaultRecipe, Distance(c.DefaultDistance), Distance(c.RejectedDistance), "-", "-", c.DefaultRejected ? "no" : "yes").Append('\n');
            foreach (RetryOutcome o in Ranked(c.Outcomes))
            {
                table.AppendJoin(
                    '\t',
                    c.Place.Name,
                    c.Place.Song,
                    Number(c.Place.StartSeconds),
                    state,
                    Name(o.Try.Kind),
                    o.Try.What,
                    o.Try.Recipe,
                    Distance(o.Distance),
                    Distance(c.RejectedDistance),
                    o.SameAsDefault ? "no" : "yes",
                    !o.SameAsDefault && o.Distance < c.RejectedDistance ? "yes" : "no",
                    c.Chosen.Contains(o) ? (c.Doubted ? "doubted" : "yes") : "no").Append('\n');
            }
        }

        return table.ToString();
    }

    /// <summary>A kind's name in the table: <c>keep-recording</c>.</summary>
    public static string Name(RetryKind kind) => kind switch
    {
        RetryKind.KeepRecording => "keep-recording",
        RetryKind.NoPreMix => "no-pre-mix",
        RetryKind.NoLayers => "no-layers",
        RetryKind.NoPreMixNoLayers => "no-pre-mix-no-layers",
        RetryKind.RecordingQuieter => "recording-quieter",
        _ => "song-quieter",
    };

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Distance(double value) => double.IsFinite(value) ? value.ToString("0.######", CultureInfo.InvariantCulture) : "-";
}

/// <summary>
/// <c>--retry</c> (docs/harness.md#retries): for each place on the retry list, makes today's default, the clips David
/// rejected and every try, measures each with CLAP against the original, and writes <c>listening/retries.tsv</c> and the
/// next round's rows, <c>listening/retry-round.tsv</c>.
/// </summary>
/// <param name="songs">The harness's inputs.</param>
/// <param name="outputDirectory">Where <c>listening/</c> goes.</param>
/// <param name="reference">Renders the originals.</param>
/// <param name="clap">The CLAP model; <see cref="IListeningModel"/> runs are made one at a time.</param>
/// <param name="logger">Where each place's result is logged.</param>
internal sealed class RetryRun(IReadOnlyList<string> songs, string outputDirectory, IReferenceRenderer reference, IListeningModel clap, ILogger logger)
{
    /// <summary>The rate the converter's <c>--render-wav</c> writes, and the original is rendered at.</summary>
    private const int ClipRate = 44_100;

    private readonly Lock _model = new();

    /// <summary>Makes the retry list of the answers file at <paramref name="answersPath"/>; always 0.</summary>
    public int Run(string answersPath, DateOnly date, int jobs, IConversionProgress progress)
    {
        ArgumentNullException.ThrowIfNull(answersPath);
        ArgumentNullException.ThrowIfNull(progress);
        IReadOnlyList<ListeningRow> answered = AnswersFile.Parse(File.ReadAllText(answersPath));
        IReadOnlyList<RetryPlace> places = RetryList.Places(answered);
        progress.Begin(new RunInfo(answersPath, RunInfo.HarnessFormat, "normal", [.. places.Select((p, i) => new PhaseInfo(i + 1, "place:" + p.Name, p.Name, string.Empty))], Math.Max(1, Math.Min(jobs, places.Count))));
        progress.SetSongSize(SongSize.Rows, 1);
        string folder = Path.Combine(outputDirectory, "listening");
        Directory.CreateDirectory(Path.Combine(folder, "retry", "work"));
        ConcurrentDictionary<RetryPlace, RetryChoice> choices = [];
        Parallel.ForEach(places, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, jobs) }, place =>
        {
            IPhase phase = progress.Start("place:" + place.Name);
            try
            {
                choices[place] = Place(place, Path.Combine(folder, "retry"));
            }
            catch (Exception ex) when (ex is HarnessException or ConversionException or IOException)
            {
                logger.Warning("{Place:l}: no retries ({Reason:l})", place.Name, ex.Message);
            }

            phase.Done();
        });

        RetryChoice[] made = [.. places.Where(choices.ContainsKey).Select(p => choices[p])];
        string round = PairPicker.NextRound(answered);
        IReadOnlyList<ListeningRow> rows = RetryList.Rows(made, round, date);
        File.WriteAllText(Path.Combine(folder, "retries.tsv"), RetryList.Table(made));
        File.WriteAllText(Path.Combine(folder, "retry-round.tsv"), AnswersFile.Format(rows));
        foreach (RetryChoice c in made)
        {
            logger.Information(
                "{Place:l}: today's default {State:l}; {Passed} of {Tries} tries closer than the rejected clips{Doubt:l}; to the round: {Chosen:l}",
                c.Place.Name,
                c.DefaultRejected ? "is still a rejected clip" : "has changed",
                c.Outcomes.Count(o => !o.SameAsDefault && o.Distance < c.RejectedDistance),
                c.Outcomes.Count,
                c.Doubted ? " (none passed: CLAP doubts the chosen)" : string.Empty,
                c.Chosen.Count == 0 ? "nothing" : string.Join("; ", c.Chosen.Select(o => o.Try.What)));
        }

        logger.ForContext(new ReportRow("result", ("output", folder), ("places", places.Count), ("questions", rows.Count))).Information(
            "Retry list: {Places} places, {Questions} questions for {Round:l} in {Folder}",
            places.Count,
            rows.Count,
            round,
            Path.Combine(folder, "retry-round.tsv"));
        return 0;
    }

    /// <summary>One place: today's default, the rejected clips and every try, made, measured and chosen from.</summary>
    private RetryChoice Place(RetryPlace place, string folder)
    {
        string songPath = SongPath(place.Song);
        string stem = Path.Combine(folder, "work", place.Name);
        StereoAudio original = Cut(reference.Render(File.ReadAllBytes(songPath), ClipRate).Audio, place.StartSeconds, place.Seconds);
        WavFile.Write(Path.Combine(folder, place.Name + "-o.wav"), original);

        SongAnalysis? analysis = null;
        byte[] defaultClip = Clip(songPath, place, PairPicker.DefaultRecipe, stem + "-default", a => analysis = a);
        bool defaultRejected = place.RejectedClips.Contains(ClipWav.Sha256(defaultClip));
        double defaultDistance = Measure(original, defaultClip);

        double rejected = double.PositiveInfinity;
        foreach ((ListeningRow row, int i) in place.Rejected.Select((r, i) => (r, i)))
        {
            foreach ((string recipe, string version) in new[] { (row.RecipeA, "a"), (row.RecipeB, "b") })
            {
                rejected = Math.Min(rejected, Measure(original, Clip(songPath, place, recipe, $"{stem}-rejected{i}{version}", null)));
            }
        }

        IReadOnlyList<(int Sample, double Amount)> sounding = analysis is null ? [] : RetryList.Sounding(analysis, place.StartSeconds, place.Seconds);
        List<RetryOutcome> outcomes = [];
        foreach ((RetryTry t, int i) in RetryList.Tries(sounding).Select((t, i) => (t, i)))
        {
            RetryTry made = t;
            byte[] clip;
            try
            {
                clip = Clip(songPath, place, t.Recipe, $"{stem}-try{i}", null);
            }
            catch (HarnessException) when (RetryList.WithRoom(t, sounding) is { } roomy)
            {
                // The kept recording found no room (exit 6): leave out the sound that plays least there.
                made = roomy;
                try
                {
                    clip = Clip(songPath, place, roomy.Recipe, $"{stem}-try{i}room", null);
                }
                catch (HarnessException ex)
                {
                    logger.Warning("{Place:l}: \"{Recipe:l}\" made no render ({Reason:l})", place.Name, roomy.Recipe, ex.Message);
                    continue;
                }
            }
            catch (HarnessException ex)
            {
                logger.Warning("{Place:l}: \"{Recipe:l}\" made no render ({Reason:l})", place.Name, t.Recipe, ex.Message);
                continue;
            }

            bool same = clip.AsSpan().SequenceEqual(defaultClip);
            outcomes.Add(new RetryOutcome(made, same ? defaultDistance : Measure(original, clip), same));
            File.WriteAllBytes(Path.Combine(folder, $"{place.Name}-try{i}.wav"), clip);
        }

        (IReadOnlyList<RetryOutcome> chosen, bool doubted) = RetryList.Choose(outcomes, rejected, RetryList.Wanted(defaultRejected));
        return new RetryChoice(place, defaultRejected, rejected, defaultDistance, RetryList.Ranked(outcomes), chosen, doubted);
    }

    /// <summary>The place's clip of <paramref name="songPath"/> made with <paramref name="recipe"/>.</summary>
    private static byte[] Clip(string songPath, RetryPlace place, string recipe, string stem, Action<SongAnalysis>? analyzed) =>
        ClipWav.Cut(File.ReadAllBytes(ListeningRemake.Render(songPath, place.Format, recipe, stem, analyzed, place.Name)), place.StartSeconds, place.Seconds);

    /// <summary>The CLAP distance from the original's clip to <paramref name="clip"/> (a WAV file), one model run at a time.</summary>
    private double Measure(StereoAudio original, byte[] clip)
    {
        lock (_model)
        {
            return EmbeddingDistance.Measure(clap, original, WavFile.Read(clip));
        }
    }

    /// <summary>The stretch of <paramref name="audio"/> from <paramref name="start"/> for <paramref name="seconds"/>.</summary>
    private static StereoAudio Cut(StereoAudio audio, double start, double seconds)
    {
        int from = Math.Min(audio.Left.Length, (int)Math.Round(start * ClipRate));
        int length = Math.Min(audio.Left.Length - from, (int)Math.Round(seconds * ClipRate));
        return new StereoAudio(audio.Left.AsSpan(from, length).ToArray(), audio.Right.AsSpan(from, length).ToArray(), ClipRate);
    }

    private string SongPath(string song) =>
        songs.FirstOrDefault(s => string.Equals(Path.GetFileName(s), song, StringComparison.OrdinalIgnoreCase))
        ?? throw new HarnessException($"{song}: not among the harness's inputs");
}
