using System.Globalization;
using Mod2Xgm.Dmf;
using Mod2Xgm.Model;

// A copy a note asks for, as the capacity check counts them (docs/adr/0086-capacity-check.md).
using CopyKey = (int Sample, double RateHz, int StartFrame, int VolumeSteps);

namespace Mod2Xgm.Optimizer;

/// <summary>
/// A copy the target stores: what a <c>CopyKey</c> becomes once recordings are shrunk (plan T9.4a,
/// docs/sample-capacity.md#consolidation-default). Without a move it is the copy asked for.
/// </summary>
/// <param name="Sample">The sample whose sound is stored: another one when a look-alike was merged into it.</param>
/// <param name="RateHz">The rate its notes start at: a shared pitch's most played rate.</param>
/// <param name="StartFrame">Where its notes start (MOD <c>9xx</c>).</param>
/// <param name="VolumeSteps">Its loudness in <see cref="DmfPatternTranslator.DacVolumeStepDb"/> steps below full.</param>
/// <param name="HalfSpeed">True when XGM2 stores and plays it at half speed.</param>
internal readonly record struct StoredCopy(int Sample, double RateHz, int StartFrame, int VolumeSteps, bool HalfSpeed);

/// <summary>A way to make a sample's recordings smaller, each giving up a little sound (docs/sample-capacity.md#consolidation-default).</summary>
internal enum ShrinkMove
{
    /// <summary>A look-alike sample's notes play another sample's copies (step 6).</summary>
    Merge,

    /// <summary>Pitches close together share the copy of the most played one (step 4).</summary>
    SharePitch,

    /// <summary>Loudness copies are kept further apart, so notes play up to half a step louder or quieter.</summary>
    ShareLoudness,

    /// <summary>XGM2 stores the copies at half speed, 6.65 kHz, for half the bytes (step 5).</summary>
    HalfSpeed,
}

/// <summary>
/// How the shrinker works: which moves it may make, in the order it tries them, and how far each goes. Chosen by ear
/// (David, plan T9.4a; docs/adr/0091-shrinking-recordings.md).
/// </summary>
/// <param name="Order">The moves, cheapest to hear first; empty to shrink nothing and leave the allocator to move notes to FM/PSG.</param>
/// <param name="PitchShareCents">Most cents a note may be played off its pitch by <see cref="ShrinkMove.SharePitch"/>.</param>
/// <param name="LoudnessShareSteps">Loudness steps one copy covers under <see cref="ShrinkMove.ShareLoudness"/>.</param>
/// <param name="MergeCorrelation">How alike two samples must be, 0–1, for <see cref="ShrinkMove.Merge"/>.</param>
internal sealed record ShrinkRules(IReadOnlyList<ShrinkMove> Order, double PitchShareCents, int LoudnessShareSteps, double MergeCorrelation)
{
    /// <summary>
    /// The rules the converter uses: merge, share pitch, share loudness, but never half speed, which David heard as
    /// "very crunchy and compressed" and lost to moving notes to FM/PSG in every song of round 11; the gentle moves tied
    /// with moving notes in round 12 and keep a few more notes (docs/adr/0091-shrinking-recordings.md).
    /// </summary>
    public static ShrinkRules Default { get; } = new([ShrinkMove.Merge, ShrinkMove.SharePitch, ShrinkMove.ShareLoudness], 10, 2, 0.98);

    /// <summary>No move: every recording stays as the song asks for it.</summary>
    public static ShrinkRules None { get; } = new([], 0, 1, 1);

    /// <summary>
    /// Samples (0-based) XGM2 stores at half speed whether or not the song needs the room: a listening round's version,
    /// set only by the harness's recipes (docs/adr/0097-learned-listening-score.md), never by the command line.
    /// </summary>
    public IReadOnlySet<int> ForcedHalfSpeed { get; init; } = new HashSet<int>();
}

/// <summary>One move the shrinker made.</summary>
/// <param name="Move">What it did.</param>
/// <param name="Sample">The sample it changed.</param>
/// <param name="Into">For <see cref="ShrinkMove.Merge"/>, the sample whose copies it now plays; otherwise <paramref name="Sample"/>.</param>
/// <param name="SlotsSaved">Stored copies it saved.</param>
/// <param name="BytesSaved">Bytes it saved.</param>
internal sealed record ShrinkStep(ShrinkMove Move, int Sample, int Into, int SlotsSaved, long BytesSaved);

/// <summary>What the shrinker decided.</summary>
/// <param name="Stored">The copy each asked-for copy is played from.</param>
/// <param name="Bytes">The bytes each stored copy takes.</param>
/// <param name="Steps">The moves it made, in order; none when the recordings already fit.</param>
/// <param name="MergeGainDb">For each merged sample, the gain its notes need on the copies it plays, in dB.</param>
internal sealed record ShrinkResult(IReadOnlyDictionary<CopyKey, StoredCopy> Stored, IReadOnlyDictionary<StoredCopy, int> Bytes, IReadOnlyList<ShrinkStep> Steps, IReadOnlyDictionary<int, double> MergeGainDb)
{
    /// <summary>Copies the target stores.</summary>
    public int Slots => Bytes.Count;

    /// <summary>Bytes the stored copies take.</summary>
    public long TotalBytes => Bytes.Values.Sum(b => (long)b);
}

/// <summary>
/// Makes the song's recordings fit the target's PCM slots and bytes by shrinking them before any note has to give its
/// recording up (plan T9.4a): one move at a time, the cheapest to hear first, on the least important sample first,
/// until they fit. Recordings that already fit get no move (David, docs/multi-pass.md#idea). Too many recordings at once
/// is left to the allocator (plan T9.4b).
/// </summary>
internal static class RecordingShrinker
{
    /// <summary>The shortest a look-alike may be against the other, as a share of its length.</summary>
    private const double MergeLengthRatio = 0.9;

    /// <summary>Cents in an octave.</summary>
    private const double CentsPerOctave = 1200;

    /// <summary>Shrinks the copies <paramref name="wanted"/> asks for until they fit <paramref name="limits"/>, or no move is left.</summary>
    /// <param name="wanted">Each note played as a recording: the copy it asks for and how long it holds.</param>
    /// <param name="importance">How much each sample's notes matter, summed (docs/sample-capacity.md#importance).</param>
    /// <param name="song">The song.</param>
    /// <param name="limits">What the target holds.</param>
    /// <param name="rules">The moves allowed and how far they go.</param>
    /// <param name="requiredDac">Samples in <c>--require-dac</c>: never merged into another sample's sound.</param>
    public static ShrinkResult Shrink(
        IReadOnlyList<(CopyKey Key, double Seconds)> wanted,
        IReadOnlyDictionary<int, double> importance,
        Song song,
        TargetLimits limits,
        ShrinkRules rules,
        IReadOnlySet<int> requiredDac)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(importance);
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(requiredDac);
        Plan plan = new(wanted, song, limits, rules);
        (Dictionary<CopyKey, StoredCopy> stored, Dictionary<StoredCopy, int> bytes) = plan.Make();
        List<ShrinkStep> steps = [];
        List<int> samples = [.. wanted.Select(w => w.Key.Sample).Distinct().OrderBy(importance.GetValueOrDefault).ThenBy(s => s)];
        foreach (int sample in samples.Where(rules.ForcedHalfSpeed.Contains).Order())
        {
            if (plan.Allows(ShrinkMove.HalfSpeed, sample, requiredDac, out int into))
            {
                plan.Apply(ShrinkMove.HalfSpeed, sample, into);
                (Dictionary<CopyKey, StoredCopy> tried, Dictionary<StoredCopy, int> triedBytes) = plan.Make();
                steps.Add(new ShrinkStep(ShrinkMove.HalfSpeed, sample, into, bytes.Count - triedBytes.Count, bytes.Values.Sum(b => (long)b) - triedBytes.Values.Sum(b => (long)b)));
                (stored, bytes) = (tried, triedBytes);
            }
        }

        foreach (ShrinkMove move in rules.Order)
        {
            foreach (int sample in samples)
            {
                if (Fits(bytes, limits))
                {
                    return new ShrinkResult(stored, bytes, steps, plan.GainDb);
                }

                if (!plan.Allows(move, sample, requiredDac, out int into))
                {
                    continue;
                }

                plan.Apply(move, sample, into);
                (Dictionary<CopyKey, StoredCopy> tried, Dictionary<StoredCopy, int> triedBytes) = plan.Make();
                int slotsSaved = bytes.Count - triedBytes.Count;
                long bytesSaved = bytes.Values.Sum(b => (long)b) - triedBytes.Values.Sum(b => (long)b);
                if (slotsSaved <= 0 && bytesSaved <= 0)
                {
                    plan.Undo(move, sample);
                    continue;
                }

                steps.Add(new ShrinkStep(move, sample, into, slotsSaved, bytesSaved));
                (stored, bytes) = (tried, triedBytes);
            }
        }

        return new ShrinkResult(stored, bytes, steps, plan.GainDb);
    }

    /// <summary>Adds a conversion note for each move in <paramref name="result"/>.</summary>
    public static void Report(ShrinkResult result, Song song, ShrinkRules rules, ConversionNotes notes)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(notes);
        foreach (ShrinkStep step in result.Steps)
        {
            string sample = Label(step.Sample, song);
            double kb = step.BytesSaved / 1024.0;
            switch (step.Move)
            {
                case ShrinkMove.Merge:
                    notes.Add("Sample {Sample:l} plays the copies of look-alike sample {Into:l} to fit: {Slots} slots, {Kb:0.#} KB saved", sample, Label(step.Into, song), step.SlotsSaved, kb);
                    break;
                case ShrinkMove.SharePitch:
                    notes.Add("Sample {Sample:l}: pitches within {Cents} cents share one copy to fit, so some notes play up to that far off: {Slots} slots, {Kb:0.#} KB saved", sample, rules.PitchShareCents, step.SlotsSaved, kb);
                    break;
                case ShrinkMove.ShareLoudness:
                    notes.Add("Sample {Sample:l}: one copy per {Db} dB of loudness to fit, so some notes play up to {Half} dB off: {Slots} slots, {Kb:0.#} KB saved", sample, rules.LoudnessShareSteps * DmfPatternTranslator.DacVolumeStepDb, rules.LoudnessShareSteps * DmfPatternTranslator.DacVolumeStepDb / 2, step.SlotsSaved, kb);
                    break;
                default:
                    notes.Add("Sample {Sample:l}: stored at half speed (6.65 kHz) to fit, losing its highs: {Kb:0.#} KB saved", sample, kb);
                    break;
            }
        }
    }

    private static bool Fits(Dictionary<StoredCopy, int> bytes, TargetLimits limits) =>
        bytes.Count <= limits.PcmSlots && (limits.PcmBytes is not { } budget || bytes.Values.Sum(b => (long)b) <= budget);

    private static string Label(int sample, Song song) =>
        string.Create(CultureInfo.InvariantCulture, $"{sample + 1} \"{song.Samples[sample].Name}\"");

    /// <summary>
    /// The loudness, in <see cref="DmfPatternTranslator.DacVolumeStepDb"/> steps below full, of the copy a note plays: the
    /// note's own steps, less a merged look-alike's gain, then rounded to a multiple of <paramref name="share"/>. The
    /// shrinker counts copies with it and the player plays them with it, so the two always agree.
    /// </summary>
    /// <param name="steps">The note's steps, as the capacity check counts them.</param>
    /// <param name="gainDb">How much louder the note's own sample is than the one it plays (0 without a merge).</param>
    /// <param name="share">Steps one copy covers; 1 for a copy per step.</param>
    internal static int StoredSteps(int steps, double gainDb, int share)
    {
        // The other sample at the loudness that sounds as this one: a louder own sample needs fewer steps down.
        steps = Math.Clamp(steps - (int)Math.Round(gainDb / DmfPatternTranslator.DacVolumeStepDb, MidpointRounding.AwayFromZero), 0, DmfPatternTranslator.MaxDacVolumeSteps);
        return share <= 1 ? steps : Math.Min(DmfPatternTranslator.MaxDacVolumeSteps, (int)Math.Round(steps / (double)share, MidpointRounding.AwayFromZero) * share);
    }

    /// <summary>True when both are one-shots, or both loop over about the same stretch: a merge never swaps one loop for another.</summary>
    private static bool LoopsAlike(Sample a, Sample b) => (a.Loop, b.Loop) switch
    {
        (null, null) => true,
        ({ } x, { } y) => Math.Abs(x.Start - y.Start) <= (1 - MergeLengthRatio) * Math.Max(a.Length, b.Length)
            && Math.Abs(x.Length - y.Length) <= (1 - MergeLengthRatio) * Math.Max(x.Length, y.Length),
        _ => false,
    };

    /// <summary>
    /// How alike <paramref name="a"/> and <paramref name="b"/> are, 0–1: the correlation of their sound at the same rate,
    /// their levels set aside; 0 when one loops and the other doesn't, or their lengths are far apart.
    /// </summary>
    internal static double Likeness(Sample a, Sample b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.IsEmpty || b.IsEmpty || !LoopsAlike(a, b) || Math.Min(a.Length, b.Length) < MergeLengthRatio * Math.Max(a.Length, b.Length))
        {
            return 0;
        }

        int n = Math.Min(a.Length, b.Length);
        double ab = 0;
        double aa = 0;
        double bb = 0;
        for (int i = 0; i < n; i++)
        {
            ab += a.Data[i] * (double)b.Data[i];
            aa += a.Data[i] * (double)a.Data[i];
            bb += b.Data[i] * (double)b.Data[i];
        }

        return aa <= 0 || bb <= 0 ? 0 : Math.Max(0, ab / Math.Sqrt(aa * bb));
    }

    /// <summary>The level of <paramref name="a"/> against <paramref name="b"/> in dB, over the length they share.</summary>
    internal static double LevelDb(Sample a, Sample b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        int n = Math.Min(a.Length, b.Length);
        double aa = 0;
        double bb = 0;
        for (int i = 0; i < n; i++)
        {
            aa += a.Data[i] * (double)a.Data[i];
            bb += b.Data[i] * (double)b.Data[i];
        }

        return aa <= 0 || bb <= 0 ? 0 : 10 * Math.Log10(aa / bb);
    }

    /// <summary>The moves made so far, and what they turn each asked-for copy into.</summary>
    private sealed class Plan(IReadOnlyList<(CopyKey Key, double Seconds)> wanted, Song song, TargetLimits limits, ShrinkRules rules)
    {
        private readonly Dictionary<int, int> _mergedInto = [];
        private readonly HashSet<int> _sharePitch = [];
        private readonly HashSet<int> _shareLoudness = [];
        private readonly HashSet<int> _halfSpeed = [];

        /// <summary>For each merged sample, the gain its notes need on the other's copies, in dB.</summary>
        public Dictionary<int, double> GainDb { get; } = [];

        /// <summary>True when <paramref name="move"/> can be made on <paramref name="sample"/>; for a merge, <paramref name="into"/> is the sample it would play.</summary>
        public bool Allows(ShrinkMove move, int sample, IReadOnlySet<int> requiredDac, out int into)
        {
            into = sample;
            bool dmf = limits.Target == PcmTarget.Dmf;
            switch (move)
            {
                case ShrinkMove.Merge:
                    if (requiredDac.Contains(sample) || _mergedInto.ContainsKey(sample) || _mergedInto.ContainsValue(sample))
                    {
                        return false;
                    }

                    int? best = wanted.Select(w => w.Key.Sample).Distinct()
                        .Where(o => o != sample && !_mergedInto.ContainsKey(o))
                        .Where(o => !dmf || Math.Abs(LevelDb(song.Samples[sample], song.Samples[o])) < DmfPatternTranslator.DacVolumeStepDb / 2)

                        // Never into a quieter look-alike: its notes would need more than full volume, which no copy holds.
                        .Where(o => LevelDb(song.Samples[sample], song.Samples[o]) < DmfPatternTranslator.DacVolumeStepDb / 2)

                        // Nor into one too short for a note's start (9xx): that note would play nothing.
                        .Where(o => Covers(sample, o))
                        .Select(o => (Other: o, Likeness: Likeness(song.Samples[sample], song.Samples[o])))
                        .Where(p => p.Likeness >= rules.MergeCorrelation)
                        .OrderByDescending(p => p.Likeness)
                        .ThenBy(p => p.Other)
                        .Select(p => (int?)p.Other)
                        .FirstOrDefault();
                    into = best ?? sample;
                    return best is not null;
                case ShrinkMove.SharePitch:
                    return rules.PitchShareCents > 0 && !_sharePitch.Contains(sample);
                case ShrinkMove.ShareLoudness:
                    // A DMF writer makes its loudness copies 3 dB apart from the notes' volumes; it can't hold this.
                    return !dmf && rules.LoudnessShareSteps > 1 && !_shareLoudness.Contains(sample);
                default:
                    return limits.Target == PcmTarget.Xgm2 && !_halfSpeed.Contains(sample);
            }
        }

        /// <summary>True when every note of <paramref name="sample"/> that stores sound still stores some from <paramref name="other"/>, at its own start.</summary>
        private bool Covers(int sample, int other) => wanted
            .Where(w => w.Key.Sample == sample)
            .All(w => CapacityCheck.CopyBytes(w.Key, w.Seconds, song, limits.Target) == 0
                || CapacityCheck.CopyBytes((other, w.Key.RateHz, w.Key.StartFrame, w.Key.VolumeSteps), w.Seconds, song, limits.Target) > 0);

        public void Apply(ShrinkMove move, int sample, int into)
        {
            switch (move)
            {
                case ShrinkMove.Merge:
                    _mergedInto[sample] = into;
                    GainDb[sample] = LevelDb(song.Samples[sample], song.Samples[into]);
                    break;
                case ShrinkMove.SharePitch:
                    _sharePitch.Add(sample);
                    break;
                case ShrinkMove.ShareLoudness:
                    _shareLoudness.Add(sample);
                    break;
                default:
                    _halfSpeed.Add(sample);
                    break;
            }
        }

        public void Undo(ShrinkMove move, int sample)
        {
            switch (move)
            {
                case ShrinkMove.Merge:
                    _mergedInto.Remove(sample);
                    GainDb.Remove(sample);
                    break;
                case ShrinkMove.SharePitch:
                    _sharePitch.Remove(sample);
                    break;
                case ShrinkMove.ShareLoudness:
                    _shareLoudness.Remove(sample);
                    break;
                default:
                    _halfSpeed.Remove(sample);
                    break;
            }
        }

        /// <summary>What every asked-for copy becomes under the moves made, and the bytes of each stored copy.</summary>
        public (Dictionary<CopyKey, StoredCopy> Stored, Dictionary<StoredCopy, int> Bytes) Make()
        {
            // Merge and loudness first: they decide which copies a shared pitch is chosen among.
            Dictionary<CopyKey, CopyKey> step1 = [];
            foreach ((CopyKey key, _) in wanted)
            {
                step1[key] = Merged(key);
            }

            Dictionary<CopyKey, double> rate = SharedRates(step1);
            Dictionary<CopyKey, StoredCopy> stored = [];
            Dictionary<StoredCopy, double> longest = [];
            foreach ((CopyKey key, double seconds) in wanted)
            {
                CopyKey k = step1[key];
                StoredCopy copy = new(k.Sample, rate.GetValueOrDefault(k, k.RateHz), k.StartFrame, k.VolumeSteps, _halfSpeed.Contains(k.Sample));
                stored[key] = copy;
                longest[copy] = Math.Max(longest.GetValueOrDefault(copy), seconds);
            }

            Dictionary<StoredCopy, int> bytes = longest.ToDictionary(
                c => c.Key,
                c => CapacityCheck.CopyBytes((c.Key.Sample, c.Key.RateHz, c.Key.StartFrame, c.Key.VolumeSteps), c.Value, song, limits.Target, c.Key.HalfSpeed));
            return (stored, bytes);
        }

        /// <summary>The copy <paramref name="key"/> plays after a merge and the loudness share: still at its own pitch.</summary>
        private CopyKey Merged(CopyKey key)
        {
            int sample = key.Sample;
            double gainDb = 0;
            if (_mergedInto.TryGetValue(sample, out int into))
            {
                gainDb = GainDb[sample];
                sample = into;
            }

            int steps = StoredSteps(key.VolumeSteps, gainDb, _shareLoudness.Contains(sample) ? rules.LoudnessShareSteps : 1);
            return (sample, key.RateHz, key.StartFrame, steps);
        }

        /// <summary>
        /// For samples sharing pitches: each copy's rate, the most played rate within <see cref="ShrinkRules.PitchShareCents"/>
        /// of it among copies with the same start and loudness, taken most played first.
        /// </summary>
        private Dictionary<CopyKey, double> SharedRates(Dictionary<CopyKey, CopyKey> step1)
        {
            Dictionary<CopyKey, double> rate = [];
            Dictionary<CopyKey, int> plays = [];
            foreach ((CopyKey key, _) in wanted)
            {
                CopyKey k = step1[key];
                plays[k] = plays.GetValueOrDefault(k) + 1;
            }

            foreach (IGrouping<(int Sample, int StartFrame, int VolumeSteps), CopyKey> group in plays.Keys
                .Where(k => _sharePitch.Contains(k.Sample))
                .GroupBy(k => (k.Sample, k.StartFrame, k.VolumeSteps)))
            {
                List<CopyKey> left = [.. group.OrderByDescending(k => plays[k]).ThenBy(k => k.RateHz)];
                while (left.Count > 0)
                {
                    double centre = left[0].RateHz;
                    List<CopyKey> near = [.. left.Where(k => Math.Abs(Math.Log2(k.RateHz / centre)) * CentsPerOctave <= rules.PitchShareCents)];
                    foreach (CopyKey k in near)
                    {
                        rate[k] = centre;
                        left.Remove(k);
                    }
                }
            }

            return rate;
        }
    }
}
