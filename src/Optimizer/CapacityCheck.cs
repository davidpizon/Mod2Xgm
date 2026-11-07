using System.Globalization;
using Mod2Xgm.Analysis;
using Mod2Xgm.Dmf;
using Mod2Xgm.Mapping;
using Mod2Xgm.Model;

// What makes one stored copy different from another: the sample (or the first with the same sound), and what the targets
// can't change while playing: the rate its notes start at, where they start (MOD 9xx) and their loudness in 3 dB steps.
using CopyKey = (int Sample, double RateHz, int StartFrame, int VolumeSteps);

namespace Mod2Xgm.Optimizer;

/// <summary>
/// What the song's recordings need from the target, against what the target holds (plan T9.2,
/// docs/adr/0086-capacity-check.md, docs/sample-capacity.md#capacity-check-always-runs). Only samples the mapper plays as
/// recordings count: FM and PSG notes store nothing. The count is after the free tidying (consolidation steps 0–3) and
/// before anything that could be heard.
/// </summary>
/// <param name="Limits">What the target holds.</param>
/// <param name="Slots">Stored PCM copies needed: one per sample, starting pitch, 3 dB of loudness and start point.</param>
/// <param name="Voices">The most recordings sounding at once.</param>
/// <param name="Bytes">Bytes those copies take on the target.</param>
internal sealed record CapacityCheck(TargetLimits Limits, int Slots, int Voices, long Bytes)
{
    /// <summary>Bytes in the KB the capacity report counts in.</summary>
    private const double BytesPerKb = 1024;

    /// <summary>True when the song's recordings fit the target.</summary>
    public bool Fits => Problems().Count == 0;

    /// <summary>Counts what the recordings of <paramref name="plan"/> need.</summary>
    /// <param name="plan">The mapped song; <see cref="GenesisPlan.PcmSamples"/> says which samples are recordings.</param>
    /// <param name="song">The song the plan was made from.</param>
    /// <param name="playback">The song played tick by tick.</param>
    /// <param name="limits">What the target holds.</param>
    public static CapacityCheck Measure(GenesisPlan plan, Song song, Playback playback, TargetLimits limits)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(limits);

        Dictionary<CopyKey, double> longest = Copies(SongNote.Find(song, playback).Where(n => plan.PcmSamples.Contains(n.Sample)), song, []);
        long bytes = longest.Sum(copy => (long)CopyBytes(copy.Key, copy.Value, song, limits.Target));
        int voices = SongVoices.Count(song, playback, plan.PcmSamples).MostAtOnce;
        return new CapacityCheck(limits, longest.Count, voices, bytes);
    }

    /// <summary>
    /// The copies <paramref name="notes"/> need, each with how long its longest note holds: one held length costs no extra
    /// copy, as a note can be stopped early. Notes too quiet or starting past the sample's end store nothing.
    /// </summary>
    /// <param name="notes">The notes played as recordings.</param>
    /// <param name="song">Their song.</param>
    /// <param name="stored">Which sample each byte-identical sample shares its copies with; filled in as samples are met.</param>
    internal static Dictionary<CopyKey, double> Copies(IEnumerable<SongNote> notes, Song song, Dictionary<int, int> stored)
    {
        Dictionary<CopyKey, double> longest = [];
        foreach (SongNote note in notes)
        {
            Sample sample = song.Samples[note.Sample];
            int steps = DmfPatternTranslator.DacVolumeSteps(note.Volume);
            if (note.StartFrame < sample.Length && steps <= DmfPatternTranslator.MaxDacVolumeSteps)
            {
                CopyKey key = (StoredAs(note.Sample, song, stored), note.RateHz, note.StartFrame, steps);
                longest[key] = Math.Max(longest.GetValueOrDefault(key), note.Seconds);
            }
        }

        return longest;
    }

    /// <summary>
    /// Says when the song doesn't fit. By default the run goes on to consolidate; with <paramref name="noConsolidate"/>
    /// it stops, as nothing may change the song to make it fit.
    /// </summary>
    /// <param name="noConsolidate"><c>--no-consolidate</c>.</param>
    /// <param name="notes">Collects the capacity warning.</param>
    /// <exception cref="ConversionException">The song doesn't fit and <paramref name="noConsolidate"/> is set (exit 6).</exception>
    public void Enforce(bool noConsolidate, ConversionNotes notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        if (Fits)
        {
            return;
        }

        string report = Report();
        notes.Add("{Report:l}", report);
        if (noConsolidate)
        {
            throw new ConversionException(ExitCode.DoesNotFit, $"{report}. --no-consolidate was given, so nothing was changed to make it fit and no file was written.");
        }

        notes.Add("Consolidating samples to fit (use --no-consolidate to fail instead)");
    }

    /// <summary>The capacity report: the target and each limit the song passes.</summary>
    public string Report() => $"{PcmTargets.Name(Limits.Target).ToUpperInvariant()} capacity exceeded: needs {string.Join(", ", Problems())}";

    /// <summary>Each limit the song passes, in words.</summary>
    private List<string> Problems()
    {
        List<string> problems = [];
        if (Slots > Limits.PcmSlots)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{Slots} sample slots (limit {Limits.PcmSlots})"));
        }

        if (Voices > Limits.PcmVoices)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{Voices} simultaneous PCM voices (limit {Limits.PcmVoices})"));
        }

        if (Limits.PcmBytes is { } budget && Bytes > budget)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{Bytes / BytesPerKb:0.#} KB PCM (budget {budget / BytesPerKb:0.#} KB)"));
        }

        return problems;
    }

    /// <summary>The first sample with the same sound as <paramref name="sample"/>: byte-identical samples share their copies (consolidation step 2).</summary>
    private static int StoredAs(int sample, Song song, Dictionary<int, int> stored)
    {
        if (stored.TryGetValue(sample, out int first))
        {
            return first;
        }

        Sample sound = song.Samples[sample];
        first = sample;
        foreach (int known in stored.Values.Where(known => SameSound(song.Samples[known], sound)))
        {
            first = known;
            break;
        }

        stored[sample] = first;
        return first;
    }

    private static bool SameSound(Sample a, Sample b) => a.Loop == b.Loop && a.Data.AsSpan().SequenceEqual(b.Data);

    /// <summary>
    /// The bytes one copy takes, with the free tidying done (consolidation step 3): a one-shot's silent tail is left out,
    /// and a loop is played out only as long as the copy's longest note.
    /// </summary>
    internal static int CopyBytes(CopyKey key, double seconds, Song song, PcmTarget target)
    {
        Sample sample = song.Samples[key.Sample];
        int frames = sample.Loop is null
            ? Math.Max(0, PcmFitter.TrimmedLength(sample) - key.StartFrame)
            : (int)Math.Ceiling(Math.Min(seconds, PcmFitter.MaxLoopSeconds) * key.RateHz) + 1;
        int points = (int)Math.Ceiling(frames * PcmTargets.PlayRateHz(target, key.RateHz, halfSpeed: false) / key.RateHz);
        return PcmTargets.Bytes(target, points);
    }
}
