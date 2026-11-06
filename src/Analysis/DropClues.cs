using Mod2Xgm.Model;
using Serilog;

namespace Mod2Xgm.Analysis;

/// <summary>One song track: a sample on a channel, as in docs/adr/0073-ch3-locked-pairs.md.</summary>
/// <param name="Sample">Index into <c>Song.Samples</c>.</param>
/// <param name="Channel">Song channel.</param>
internal readonly record struct SongTrack(int Sample, int Channel);

/// <summary>How a track copies its twin (docs/adr/0084-drop-clues.md).</summary>
internal enum TwinKind
{
    /// <summary>The same notes at the same time, on the same side (or the middle): the track can go.</summary>
    Double,

    /// <summary>The same notes at the same time, on the opposite side: the two can merge into one voice in the middle.</summary>
    Mirror,

    /// <summary>The same notes a steady delay later and quieter: the track can go.</summary>
    Echo,
}

/// <summary>One note a track shares with its twin.</summary>
/// <param name="Note">The track's note.</param>
/// <param name="Twin">The twin's note it copies.</param>
internal sealed record TwinNote(SongNote Note, SongNote Twin);

/// <summary>
/// A track that mostly copies another of the same sample, so it is cheap to drop or merge (plan T8.7,
/// docs/adr/0084-drop-clues.md). A pair where each covers the other gives one clue each way.
/// </summary>
/// <param name="Kind">How it copies.</param>
/// <param name="Track">The track that could go.</param>
/// <param name="Twin">The track that would keep playing its notes.</param>
/// <param name="Matches">Every note it shares with the twin, in song order: M9 can use the ones in a section.</param>
/// <param name="Notes">How many notes the track plays in all.</param>
/// <param name="DelayTicks">How many ticks after the twin it plays; 0 unless an echo.</param>
/// <param name="DelaySeconds">The same, as the mean time between the shared notes.</param>
/// <param name="QuieterDb">How much quieter than the twin it starts its shared notes, on average (negative when louder).</param>
internal sealed record TwinClue(TwinKind Kind, SongTrack Track, SongTrack Twin, IReadOnlyList<TwinNote> Matches, int Notes, int DelayTicks, double DelaySeconds, double QuieterDb)
{
    /// <summary>The share of the track's notes the twin also plays, 0–1.</summary>
    public double Share => (double)Matches.Count / Notes;
}

/// <summary>How loud one track is and how often others cover it (plan T8.7, docs/adr/0084-drop-clues.md).</summary>
/// <param name="Track">The track.</param>
/// <param name="Notes">How many notes it plays.</param>
/// <param name="LevelDb">Its mean level while it sounds, in dB of full scale.</param>
/// <param name="BelowLoudestDb">How far that is below the song's loudest track.</param>
/// <param name="BuriedNotes">Its notes covered by something louder (<see cref="DropClues.BuriedByDb"/>), in song order.</param>
internal sealed record TrackLevel(SongTrack Track, int Notes, double LevelDb, double BelowLoudestDb, IReadOnlyList<SongNote> BuriedNotes)
{
    /// <summary>The share of its notes that are buried, 0–1.</summary>
    public double BuriedShare => (double)BuriedNotes.Count / Notes;

    /// <summary>True when the whole track is very quiet against the song's loudest (<see cref="DropClues.QuietBelowDb"/>).</summary>
    public bool Quiet => BelowLoudestDb >= DropClues.QuietBelowDb;

    /// <summary>True when most of its notes are buried (<see cref="DropClues.MinShare"/>).</summary>
    public bool Buried => BuriedShare >= DropClues.MinShare;
}

/// <summary>
/// How cheap each song track is to drop or thin out when the Genesis runs out of channels (plan T8.7, pass 1 of
/// docs/multi-pass.md, docs/adr/0084-drop-clues.md). Clues only: each stays separate with its own number, and nothing is
/// dropped here. They say what to try first, not that a change is safe: the allocator (M9) has to judge the result.
/// </summary>
/// <param name="Tracks">The level of every track with sound, ordered by channel and sample.</param>
/// <param name="Twins">Every track that copies another, ordered by the track's channel and sample, then its twin's.</param>
internal sealed record DropClues(IReadOnlyList<TrackLevel> Tracks, IReadOnlyList<TwinClue> Twins)
{
    /// <summary>A track copies another only when that also plays at least this share of its notes (David): dropping it loses at most one note in four.</summary>
    public const double MinShare = 0.75;

    /// <summary>Fewest shared notes that prove anything, as for locked pairs (<see cref="LockedPairs.MinOccurrences"/>).</summary>
    private const int MinMatches = LockedPairs.MinOccurrences;

    /// <summary>Two notes have the same pitch when at most this far apart, as for locked pairs (<see cref="LockedPairs.MaxSpreadCents"/>).</summary>
    private const double MaxPitchCents = LockedPairs.MaxSpreadCents;

    /// <summary>An echo comes back within this long (David): later than that, it is a part of its own.</summary>
    private const double MaxEchoSeconds = 1;

    /// <summary>An echo is at least this much quieter than what it copies: less is a repeat as loud, whatever the rounding of a tracker's volumes.</summary>
    private const double MinEchoDropDb = 1;

    /// <summary>
    /// A track at least this far below the song's loudest is very quiet: set by David's listening, which left 22.6 dB unheard
    /// and 21.4 dB heard (docs/adr/0084-drop-clues.md#listening).
    /// </summary>
    public const double QuietBelowDb = 22;

    /// <summary>
    /// A moment of a note is buried when another track is at least this much louder (David: any louder sound, whatever its
    /// pitch). The strictest cutoff tried that marks any track (15 dB marks none); David still heard one go, so this only
    /// says which tracks to try first (docs/adr/0084-drop-clues.md#listening).
    /// </summary>
    public const double BuriedByDb = 12;

    /// <summary>A note is buried when at least this share of its energy is: the tail of a drum under a pad doesn't bury its hit.</summary>
    private const double BuriedEnergyShare = 0.75;

    /// <summary>The clues of <paramref name="song"/>'s analyzed samples with sound.</summary>
    public static DropClues Find(SongAnalysis song)
    {
        ArgumentNullException.ThrowIfNull(song);
        Dictionary<int, SampleAnalysis> sounding = song.Samples.Where(a => a.Kind != SampleKind.Silent).ToDictionary(a => a.Sample);
        SongNote[] notes = [.. song.Notes.Where(n => sounding.ContainsKey(n.Sample))];
        return new DropClues(Levels(song.Song, song.Playback, notes, sounding), Copies(song.Playback, notes));
    }

    /// <summary>Every track that copies another: all pairs of tracks of one sample, each way round.</summary>
    private static TwinClue[] Copies(Playback playback, SongNote[] notes)
    {
        SongNote[][] tracks = [.. notes
            .GroupBy(n => (n.Channel, n.Sample))
            .OrderBy(g => g.Key.Channel)
            .ThenBy(g => g.Key.Sample)
            .Select(g => g.OrderBy(n => n.FirstTick).ToArray())
            .Where(t => t.Length >= MinMatches)];
        List<TwinClue> found = [];
        foreach (SongNote[] track in tracks)
        {
            foreach (SongNote[] twin in tracks)
            {
                if (track[0].Sample == twin[0].Sample && track[0].Channel != twin[0].Channel)
                {
                    AddIf(found, Together(playback, track, twin));
                    AddIf(found, Echo(playback, track, twin));
                }
            }
        }

        return [.. found];
    }

    private static void AddIf(List<TwinClue> found, TwinClue? clue)
    {
        if (clue is not null)
        {
            found.Add(clue);
        }
    }

    /// <summary>The clue that <paramref name="track"/> plays <paramref name="twin"/>'s notes at the same time, or <c>null</c>.</summary>
    private static TwinClue? Together(Playback playback, SongNote[] track, SongNote[] twin)
    {
        Dictionary<int, SongNote> at = twin.ToDictionary(n => n.FirstTick);
        TwinNote[] matches = [.. track
            .Where(n => at.TryGetValue(n.FirstTick, out SongNote? other) && SamePitch(n, other))
            .Select(n => new TwinNote(n, at[n.FirstTick]))];
        if (!Enough(matches.Length, track.Length))
        {
            return null;
        }

        bool opposite = MeanPan(playback, matches.Select(m => m.Note)) * MeanPan(playback, matches.Select(m => m.Twin)) < 0;
        return Clue(opposite ? TwinKind.Mirror : TwinKind.Double, track, twin, matches, 0, playback);
    }

    /// <summary>The clue that <paramref name="track"/> plays <paramref name="twin"/>'s notes a steady delay later and quieter, or <c>null</c>.</summary>
    private static TwinClue? Echo(Playback playback, SongNote[] track, SongNote[] twin)
    {
        Dictionary<int, List<TwinNote>> byDelay = [];
        int first = 0;
        foreach (SongNote note in track)
        {
            while (first < twin.Length && Seconds(playback, twin[first], note) > MaxEchoSeconds)
            {
                first++;
            }

            for (int t = first; t < twin.Length && twin[t].FirstTick < note.FirstTick; t++)
            {
                if (SamePitch(note, twin[t]))
                {
                    int delay = note.FirstTick - twin[t].FirstTick;
                    if (!byDelay.TryGetValue(delay, out List<TwinNote>? matches))
                    {
                        byDelay[delay] = matches = [];
                    }

                    matches.Add(new TwinNote(note, twin[t]));
                }
            }
        }

        if (byDelay.Count == 0)
        {
            return null;
        }

        // The steadiest delay; of equal ones, the shortest.
        KeyValuePair<int, List<TwinNote>> best = byDelay.OrderByDescending(d => d.Value.Count).ThenBy(d => d.Key).First();
        if (!Enough(best.Value.Count, track.Length))
        {
            return null;
        }

        TwinClue clue = Clue(TwinKind.Echo, track, twin, best.Value, best.Key, playback);
        return clue.QuieterDb >= MinEchoDropDb ? clue : null;
    }

    private static TwinClue Clue(TwinKind kind, SongNote[] track, SongNote[] twin, IReadOnlyList<TwinNote> matches, int delay, Playback playback) => new(
        kind,
        new SongTrack(track[0].Sample, track[0].Channel),
        new SongTrack(twin[0].Sample, twin[0].Channel),
        matches,
        track.Length,
        delay,
        matches.Average(m => Seconds(playback, m.Twin, m.Note)),
        matches.Average(m => VolumeDb(m.Twin.Volume) - VolumeDb(m.Note.Volume)));

    private static bool Enough(int matches, int notes) => matches >= MinMatches && (double)matches / notes >= MinShare;

    private static bool SamePitch(SongNote a, SongNote b) => Math.Abs(1200 * Math.Log2(a.RateHz / b.RateHz)) <= MaxPitchCents;

    private static double Seconds(Playback playback, SongNote from, SongNote to) => playback.Ticks[to.FirstTick].TimeSeconds - playback.Ticks[from.FirstTick].TimeSeconds;

    private static double MeanPan(Playback playback, IEnumerable<SongNote> notes) => notes.Average(n => playback.Ticks[n.FirstTick].Channels[n.Channel].Pan);

    private static double VolumeDb(double volume) => volume <= 0 ? SampleAnalyzer.FloorDb : Math.Max(SampleAnalyzer.FloorDb, 20 * Math.Log10(volume));

    /// <summary>The level of every track: each tick's level of each note, then how much of each note others cover.</summary>
    private static TrackLevel[] Levels(Song song, Playback playback, SongNote[] notes, Dictionary<int, SampleAnalysis> sounding)
    {
        double[][] levels = [.. Enumerable.Range(0, song.Channels.Count).Select(_ => Enumerable.Repeat(SampleAnalyzer.FloorDb, playback.Ticks.Count).ToArray())];
        foreach (SongNote note in notes)
        {
            FillLevels(levels[note.Channel], playback, note, sounding[note.Sample].Envelope);
        }

        List<(SongTrack Track, int Notes, double LevelDb, IReadOnlyList<SongNote> Buried)> tracks = [];
        foreach (IGrouping<SongTrack, SongNote> track in notes.GroupBy(n => new SongTrack(n.Sample, n.Channel)).OrderBy(g => g.Key.Channel).ThenBy(g => g.Key.Sample))
        {
            double energy = 0;
            double seconds = 0;
            List<SongNote> buried = [];
            foreach (SongNote note in track)
            {
                (double noteEnergy, double noteSeconds, bool isBuried) = Measure(levels, playback, note);
                energy += noteEnergy;
                seconds += noteSeconds;
                if (isBuried)
                {
                    buried.Add(note);
                }
            }

            double level = energy > 0 && seconds > 0 ? Math.Max(SampleAnalyzer.FloorDb, 10 * Math.Log10(energy / seconds)) : SampleAnalyzer.FloorDb;
            tracks.Add((track.Key, track.Count(), level, buried));
        }

        double loudest = tracks.Count == 0 ? 0 : tracks.Max(t => t.LevelDb);
        return [.. tracks.Select(t => new TrackLevel(t.Track, t.Notes, t.LevelDb, loudest - t.LevelDb, t.Buried))];
    }

    /// <summary>Sets <paramref name="channel"/>'s level on each tick of <paramref name="note"/>: where its sample's envelope has got to, at the note's volume.</summary>
    private static void FillLevels(double[] channel, Playback playback, SongNote note, AmplitudeEnvelope envelope)
    {
        double position = note.StartFrame / envelope.RateHz;
        for (int t = note.FirstTick; t < note.FirstTick + note.Ticks; t++)
        {
            PlaybackTick tick = playback.Ticks[t];
            ChannelTick sound = tick.Channels[note.Channel];
            int window = (int)(position / envelope.HopSeconds);
            double sample = envelope.LevelsDb.Count == 0 ? SampleAnalyzer.FloorDb : envelope.LevelsDb[Math.Min(window, envelope.LevelsDb.Count - 1)];
            channel[t] = Math.Max(SampleAnalyzer.FloorDb, sample + VolumeDb(sound.Volume));
            position += sound.Pitch.PlaybackRateHz * tick.DurationSeconds / envelope.RateHz;
        }
    }

    /// <summary>The energy and length of <paramref name="note"/>, and whether louder tracks cover most of that energy.</summary>
    private static (double Energy, double Seconds, bool Buried) Measure(double[][] levels, Playback playback, SongNote note)
    {
        double energy = 0;
        double covered = 0;
        double seconds = 0;
        for (int t = note.FirstTick; t < note.FirstTick + note.Ticks; t++)
        {
            double own = levels[note.Channel][t];
            double duration = playback.Ticks[t].DurationSeconds;
            double tickEnergy = Math.Pow(10, own / 10) * duration;
            energy += tickEnergy;
            seconds += duration;
            for (int c = 0; c < levels.Length; c++)
            {
                if (c != note.Channel && levels[c][t] - own >= BuriedByDb)
                {
                    covered += tickEnergy;
                    break;
                }
            }
        }

        return (energy, seconds, energy > 0 && covered / energy >= BuriedEnergyShare);
    }
}

/// <summary>Logs the drop clues at <c>Debug</c> level (<c>--verbose</c>, docs/adr/0084-drop-clues.md).</summary>
internal static class DropClueReport
{
    /// <summary>One line per clue, then how many there are.</summary>
    public static void Log(ILogger logger, DropClues clues)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clues);
        foreach (TwinClue c in clues.Twins)
        {
            logger.Debug(
                "Drop clue: sample {Sample} on channel {Channel} {Kind:l} channel {TwinChannel} on {Matches} of its {Notes} notes, {Delay} ticks later, {Quieter:0.#} dB quieter",
                c.Track.Sample + 1,
                c.Track.Channel + 1,
                KindName(c.Kind),
                c.Twin.Channel + 1,
                c.Matches.Count,
                c.Notes,
                c.DelayTicks,
                c.QuieterDb);
        }

        foreach (TrackLevel t in clues.Tracks.Where(t => t.Quiet || t.Buried))
        {
            logger.Debug(
                "Drop clue: sample {Sample} on channel {Channel} is {Clue:l}: {Below:0.#} dB below the loudest track, {Buried} of its {Notes} notes buried",
                t.Track.Sample + 1,
                t.Track.Channel + 1,
                t is { Quiet: true, Buried: true } ? "quiet and buried" : t.Quiet ? "quiet" : "buried",
                t.BelowLoudestDb,
                t.BuriedNotes.Count,
                t.Notes);
        }

        logger.Debug("{Count} drop clues (nothing is dropped: the optimizer weighs them)", clues.Twins.Count + clues.Tracks.Count(t => t.Quiet) + clues.Tracks.Count(t => t.Buried));
    }

    /// <summary>The kind of copy as a word, for logs and the harness's CSV.</summary>
    public static string KindName(TwinKind kind) => kind switch
    {
        TwinKind.Double => "doubles",
        TwinKind.Mirror => "mirrors",
        _ => "echoes",
    };
}
