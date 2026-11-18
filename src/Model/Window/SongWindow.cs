namespace Mod2Xgm.Model.Window;

/// <summary>A song cut to a window, with its playback: every later stage reads them as an ordinary song (docs/adr/0116-song-window.md).</summary>
/// <param name="Song">The window, written as new patterns in play order.</param>
/// <param name="Playback">The window's ticks, taken from the whole song's playback.</param>
/// <param name="Summary">Where the window lies in the source, for the report and the candidate cache.</param>
internal sealed record WindowedSong(Song Song, Playback Playback, WindowSummary Summary);

/// <summary>Where a window lies in the source song and how it ends.</summary>
/// <param name="Start">Where it starts in the source.</param>
/// <param name="End">The source row it stops before, or <c>null</c> when it runs to the song's own end.</param>
/// <param name="EndSeconds">Where it ends, in source seconds.</param>
/// <param name="Loop">Where it loops back to in the source, or <c>null</c> when it stops.</param>
/// <param name="LoopSeconds">Where the loop starts in the window, in seconds, or <c>null</c> when it stops.</param>
/// <param name="FadeSeconds">Length of the fade-out, or <c>null</c> for none.</param>
/// <param name="Seconds">Length of the window.</param>
/// <param name="Ticks">The source's timeline ticks the window covers: start, end (exclusive) and loop (or −1), for the candidate cache.</param>
internal sealed record WindowSummary(
    SourcePoint Start,
    SourcePoint? End,
    double EndSeconds,
    SourcePoint? Loop,
    double? LoopSeconds,
    double? FadeSeconds,
    double Seconds,
    (int Start, int End, int Loop) Ticks);

/// <summary>A row in the source song: order, row and when it plays.</summary>
/// <param name="Order">Index into the source's orders.</param>
/// <param name="Row">Row within the order's pattern.</param>
/// <param name="Seconds">When it plays, from the source song's start.</param>
internal readonly record struct SourcePoint(int Order, int Row, double Seconds);

/// <summary>
/// Cuts a window out of a song (<c>--start</c>, <c>--end</c>, <c>--loop-from</c>, <c>--fade-out</c>; docs/adr/0116-song-window.md).
/// The playback is sliced, not played again, so every tick in the window sounds as it did in the whole song. The song is
/// written again as new patterns, one row per row played, in play order: jumps, breaks and pattern loops are played out,
/// and what the window's start inherits (speed, tempo, effect memory, instruments, notes still sounding) is written into
/// its first rows, so a DMF, which reads the cells, plays what the XGM2 and the WAV play.
/// </summary>
internal static class SongWindow
{
    /// <summary>Rows in each new pattern, as in a MOD.</summary>
    private const int PatternRows = 64;

    /// <summary>Tempos closer than this, in BPM, count as the same when deciding whether the loop row must set it again.</summary>
    private const double BpmTolerance = 1e-6;

    /// <summary>Cuts <paramref name="request"/>'s window from <paramref name="song"/> and its <paramref name="playback"/>.</summary>
    /// <exception cref="WindowException">The window can't be cut from this song; the message says why.</exception>
    public static WindowedSong Cut(Song song, Playback playback, SongWindowRequest request, ConversionNotes notes)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(notes);

        PlaybackTimeline timeline = new(playback);
        Bounds bounds = Resolve(timeline, request, notes);
        List<int> rows = [.. timeline.RowStarts(bounds.Start).TakeWhile(v => v < bounds.End)];
        Builder builder = new(song, timeline, bounds, rows, notes);
        WindowedSong windowed = builder.Build();
        return request.FadeOutSeconds is { } fade ? FadeOut.Apply(windowed, builder.RowTicks, fade, notes) : windowed;
    }

    /// <summary>The window on the source's timeline: start, end (exclusive) and the tick it loops to, or <c>null</c> when it stops.</summary>
    private sealed record Bounds(int Start, int End, int? Loop);

    private static Bounds Resolve(PlaybackTimeline timeline, SongWindowRequest request, ConversionNotes notes)
    {
        int start = 0;
        if (request.Start is { } startPosition)
        {
            start = timeline.Resolve(startPosition, 0, isEnd: false, "--start").Tick;
            NotePosition(timeline, "--start", startPosition, start, notes);
        }

        // Where the song's own ending takes the window: the end of the pass, then back to the loop, or, starting inside
        // the loop, one whole turn of it from the start.
        int naturalLoop = Math.Max(start, timeline.LoopTick ?? 0);
        int end = timeline.Loops ? naturalLoop + timeline.CycleTicks : timeline.TickCount;
        int? loop = timeline.Loops ? naturalLoop : null;
        if (request.End is { } endPosition)
        {
            (end, bool clamped) = timeline.Resolve(endPosition, start + 1, isEnd: true, "--end");
            if (end <= start)
            {
                throw new WindowException($"--end {endPosition.Display} is the same row as --start or before it, so the window would be empty.");
            }

            if (clamped)
            {
                notes.Add("--end {Requested:l} is past the song's end at {SongEnd:l}; the window ends there", endPosition.Display, SongTime.Format(timeline.PassSeconds));
            }
            else
            {
                NotePosition(timeline, "--end", endPosition, end, notes);
            }

            loop = null;
        }
        else if (timeline.LoopTick is { } songLoopStart && start > songLoopStart)
        {
            notes.Add(
                "--start is inside the song's loop (from order {Order} row {Row}), so the window plays one whole turn of the loop from --start and loops back to it",
                timeline.TickAt(songLoopStart).Order,
                timeline.TickAt(songLoopStart).Row);
        }

        if (end - start > PlaybackTimeline.MaxTicks)
        {
            throw new WindowException($"The window is longer than this converter plays a song ({PlaybackTimeline.MaxTicks} ticks).");
        }

        if (request.Trim != TrimSilence.None)
        {
            bool loops = (loop is not null || request.LoopFrom is not null) && request.FadeOutSeconds is null;

            // A window that plays the song's own loop goes back to it, so its start can't move past it.
            int latestStart = loop ?? end - 1;
            (start, end) = Trim(timeline, request.Trim, start, end, latestStart, loops, notes);
        }

        if (request.LoopFrom is { } loopPosition)
        {
            int at = timeline.Resolve(loopPosition, start, isEnd: false, "--loop-from").Tick;
            if (at < start || at >= end)
            {
                throw new WindowException($"--loop-from {loopPosition.Display} isn't inside the window ({SongTime.Format(timeline.TimeOf(start))} to {SongTime.Format(timeline.TimeOf(end))}).");
            }

            NotePosition(timeline, "--loop-from", loopPosition, at, notes);
            loop = at;
        }

        if (request.FadeOutSeconds is { } fade)
        {
            double seconds = timeline.TimeOf(end) - timeline.TimeOf(start);
            if (fade >= seconds)
            {
                throw new WindowException($"--fade-out {SongTime.Format(fade)} is as long as the window ({SongTime.Format(seconds)}) or longer.");
            }

            if (request.End is null && loop is not null)
            {
                notes.Add("--fade-out without --end: the song loops, so the fade ends its first pass at {End:l} and it stops there", SongTime.Format(timeline.TimeOf(end)));
            }

            loop = null;
        }

        if (end > timeline.TickCount && timeline.LoopTick is { } songLoop && (request.End is not null || start <= songLoop))
        {
            notes.Add("The window plays the song's loop again (from order {Order} row {Row}) to reach its end", timeline.TickAt(songLoop).Order, timeline.TickAt(songLoop).Row);
        }

        return new Bounds(start, end, loop);
    }

    /// <summary>
    /// Moves the window's ends in past its silence (<c>--trim-silence</c>, docs/adr/0122-trim-silence.md): the start back to the
    /// row start at or before the first tick that sounds, no later than <paramref name="latestStart"/>; the end to the first row
    /// start after the last tick that sounds. A window that loops keeps its end, as its silence is part of the turn.
    /// </summary>
    /// <exception cref="WindowException">Nothing sounds in the window.</exception>
    private static (int Start, int End) Trim(PlaybackTimeline timeline, TrimSilence trim, int start, int end, int latestStart, bool loops, ConversionNotes notes)
    {
        bool[] sounding = SilenceScan.Sounding(timeline.Playback);
        bool Sounds(int v) => sounding[timeline.RealTick(v)];
        int first = start;
        while (first < end && !Sounds(first))
        {
            first++;
        }

        if (first == end)
        {
            throw new WindowException("--trim-silence: nothing sounds in the part of the song converted.");
        }

        if (trim.HasFlag(TrimSilence.Start))
        {
            int row = timeline.RowStarts(start).TakeWhile(v => v <= Math.Min(first, latestStart)).DefaultIfEmpty(start).Last();
            if (row > start)
            {
                PlaybackTick at = timeline.TickAt(row);
                notes.Add(
                    "--trim-silence: {Seconds:l} of silence left out at the start; the song now starts at order {Order} row {Row}, {Time:l} into it",
                    SongTime.Format(timeline.TimeOf(row) - timeline.TimeOf(start)),
                    at.Order,
                    at.Row,
                    SongTime.Format(timeline.TimeOf(row)));
                start = row;
            }
        }

        if (trim.HasFlag(TrimSilence.End))
        {
            if (loops)
            {
                notes.Add("--trim-silence: the song loops, so its end keeps its silence: it is part of the loop's timing");
            }
            else
            {
                int last = end - 1;
                while (last > start && !Sounds(last))
                {
                    last--;
                }

                int row = timeline.RowStarts(last + 1).TakeWhile(v => v < end).DefaultIfEmpty(end).First();
                if (row < end)
                {
                    notes.Add(
                        "--trim-silence: {Seconds:l} of silence left out at the end; the song now ends at {Time:l} into it, before order {Order} row {Row}",
                        SongTime.Format(timeline.TimeOf(end) - timeline.TimeOf(row)),
                        SongTime.Format(timeline.TimeOf(row)),
                        timeline.TickAt(row).Order,
                        timeline.TickAt(row).Row);
                    end = row;
                }
            }
        }

        return (start, end);
    }

    /// <summary>Notes where a position landed: the row, and the time it plays at.</summary>
    private static void NotePosition(PlaybackTimeline timeline, string option, SongPosition position, int tick, ConversionNotes notes)
    {
        if (tick >= timeline.TickCount && !timeline.Loops)
        {
            notes.Add("{Option:l} {Requested:l} is the song's end, {Time:l} into the song", option, position.Display, SongTime.Format(timeline.TimeOf(tick)));
            return;
        }

        PlaybackTick at = timeline.TickAt(tick);
        notes.Add(
            "{Option:l} {Requested:l} is order {Order} row {Row}, {Time:l} into the song (positions snap to the nearest row start)",
            option,
            position.Display,
            at.Order,
            at.Row,
            SongTime.Format(timeline.TimeOf(tick)));
    }

    /// <summary>Writes the window's song and playback.</summary>
    private sealed class Builder(Song song, PlaybackTimeline timeline, Bounds bounds, List<int> rowStarts, ConversionNotes notes)
    {
        private readonly int _channels = song.Channels.Count;
        private readonly List<List<Row>> _patterns = [];
        private readonly Dictionary<NotePosition, NotePitch> _pitches = [];
        private readonly (int Order, int Row)[] _positions = new (int, int)[rowStarts.Count];

        /// <summary>The window's tick each new row starts on, in row order.</summary>
        public List<int> RowTicks { get; } = [];

        public WindowedSong Build()
        {
            if (rowStarts.Count == 0 || rowStarts[0] != bounds.Start)
            {
                throw new WindowException("The window holds no rows.");
            }

            CarriedState state = Carry(rowStarts[0]);
            Timing timing = new(state.Speed, state.Bpm);
            List<PlaybackTick> ticks = Ticks();
            int? loopIndex = bounds.Loop is { } loopTick ? rowStarts.IndexOf(loopTick) : null;
            WriteRows(state, ticks, loopIndex);

            int? loopOrder = loopIndex is { } li ? _positions[li].Order : null;
            int? newLoopTick = loopIndex is { } index ? RowTicks[index] : null;
            Song windowed = new(
                song.Title,
                song.Channels,
                [.. Enumerable.Range(0, _patterns.Count)],
                loopOrder,
                [.. _patterns.Select(p => new Pattern([.. p, .. Enumerable.Repeat(EmptyRow(), PatternRows - p.Count)]))],
                song.Instruments,
                song.Samples,
                timing);
            Playback cut = new(ticks, newLoopTick, newLoopTick is null ? PlaybackEnd.Stops : PlaybackEnd.Loops) { NotePitches = _pitches };
            notes.Add(
                "The window is written as {Patterns} new patterns in play order: jumps, breaks and pattern loops are played out, so no pattern is shared",
                _patterns.Count);
            return new WindowedSong(windowed, cut, Summary(cut));
        }

        /// <summary>The state the cells have set before the window, followed from the song's start.</summary>
        private CarriedState Carry(int start)
        {
            CarriedState state = new(_channels, song.Timing);
            foreach (int v in timeline.RowStarts(0).TakeWhile(v => v < start))
            {
                PlaybackTick tick = timeline.TickAt(v);
                state.Apply(SourceRow(tick), c => Pitch(tick, c));
            }

            return state;
        }

        /// <summary>The window's ticks, renumbered to the new rows, times from the window's start.</summary>
        private List<PlaybackTick> Ticks()
        {
            List<PlaybackTick> ticks = [];
            double time = 0;
            int row = -1;
            for (int v = bounds.Start; v < bounds.End; v++)
            {
                if (row + 1 < rowStarts.Count && rowStarts[row + 1] == v)
                {
                    row++;
                    RowTicks.Add(ticks.Count);
                }

                PlaybackTick tick = timeline.TickAt(v);
                ticks.Add(tick with { TimeSeconds = time, Order = row, Row = 0 });
                time += tick.DurationSeconds;
            }

            return ticks;
        }

        /// <summary>Writes every row and renumbers the ticks' orders and rows; <paramref name="loopIndex"/>'s row starts an order.</summary>
        private void WriteRows(CarriedState state, List<PlaybackTick> ticks, int? loopIndex)
        {
            bool[] instrumentSet = new bool[_channels];
            bool[] targetSet = new bool[_channels];
            int restarted = 0;
            int targets = 0;
            (int Speed, double Bpm) atLoop = default;
            for (int i = 0; i < rowStarts.Count; i++)
            {
                if (i == loopIndex)
                {
                    atLoop = (state.Speed, state.Bpm);
                    if (_patterns.Count > 0 && _patterns[^1].Count < PatternRows)
                    {
                        // The loop row starts an order (a DMF drops a break that comes with a jump), so the order before it is cut short.
                        _patterns[^1][^1] = WithEffect(_patterns[^1][^1], new PatternBreak(0));
                    }

                    _patterns.Add([]);
                }
                else if (_patterns.Count == 0 || _patterns[^1].Count == PatternRows)
                {
                    _patterns.Add([]);
                }

                _positions[i] = (_patterns.Count - 1, _patterns[^1].Count);
                PlaybackTick source = timeline.TickAt(rowStarts[i]);
                Row original = SourceRow(source);
                Cell[] cells = new Cell[_channels];
                for (int c = 0; c < _channels; c++)
                {
                    Cell cell = Strip(state.Resolve(original.Cells[c], c));
                    if (cell.Note is not null && Pitch(source, c) is { } pitch)
                    {
                        _pitches[new NotePosition(_positions[i].Order, _positions[i].Row, c)] = pitch;
                    }

                    if (i == 0 && cell.Note is null && Sounding(state, ticks, c) is { } restart)
                    {
                        cell = restart;
                        restarted++;
                    }
                    else if (cell is { Note: not null, Instrument: null } && !instrumentSet[c] && state.Instrument(c) is { } carried)
                    {
                        // The note keeps the instrument chosen before the window; written here, as the window has no earlier one.
                        cell = cell with { Instrument = carried, Volume = cell.Volume ?? VolumeBefore(rowStarts[i], c) };
                    }

                    if (cell.Note is null && cell.Effects.OfType<TonePortamento>().Any() && !targetSet[c] && state.PortaTarget(c) is { } target)
                    {
                        // A slide running into the window: its target note is written again, with no instrument, so it slides on.
                        cell = cell with { Note = target };
                        _pitches[new NotePosition(_positions[i].Order, _positions[i].Row, c)] = target;
                        targets++;
                    }

                    instrumentSet[c] |= cell.Instrument is not null;
                    targetSet[c] |= cell.Note is not null && cell.Effects.OfType<TonePortamento>().Any();
                    cells[c] = cell;
                }

                if (i == 0)
                {
                    cells = FirstRowState(cells, state);
                }

                state.Apply(original, c => Pitch(source, c));
                _patterns[^1].Add(new Row(cells));
            }

            Renumber(ticks);
            End(atLoop, state, loopIndex);
            if (restarted > 0)
            {
                notes.Add("{Count} notes still sounding at --start are started again on the window's first row, part-way into their samples; FM voices restart their envelopes there", restarted);
            }

            if (targets > 0)
            {
                notes.Add("{Count} tone portamentos running into the window get their target note written again", targets);
            }
        }

        /// <summary>
        /// The first row's cell for a channel whose note still sounds at the window's start: the note again at its pitch,
        /// instrument and volume, from the frame it has reached; the window's first tick starts that part of the sample.
        /// A tone portamento on that row is left out, as it would slide instead of starting the note.
        /// </summary>
        private Cell? Sounding(CarriedState state, List<PlaybackTick> ticks, int c)
        {
            if (bounds.Start == 0 || ticks[0].Channels[c].Started is not null || SamplePosition(c) is not { Sample: { } sample } block)
            {
                return null;
            }

            PlaybackTick before = timeline.TickAt(bounds.Start - 1);
            Cell cell = Strip(state.Resolve(SourceRow(timeline.TickAt(bounds.Start)).Cells[c], c));
            int instrument = InstrumentFor(sample, state.Instrument(c));
            List<Effect> effects = [.. cell.Effects.Where(e => e is not (TonePortamento or SampleOffset))];
            if (block.StartFrame > 0)
            {
                effects.Insert(0, new SampleOffset(block.StartFrame));
            }

            NotePitch pitch = before.Channels[c].Pitch;
            _pitches[new NotePosition(0, 0, c)] = pitch;
            ChannelTick[] channels = [.. ticks[0].Channels];
            channels[c] = channels[c] with { Started = block };
            ticks[0] = ticks[0] with { Channels = channels };
            return new Cell(pitch, instrument, cell.Volume ?? before.Channels[c].Volume, effects);
        }

        /// <summary>
        /// Where channel <paramref name="c"/>'s sample has got to at the window's start: the part it plays and the frame it
        /// reached in it, or <c>null</c> when it is silent. Adds up the frames played since the sample last started.
        /// </summary>
        private SampleBlock? SamplePosition(int c)
        {
            int from = bounds.Start - 1;
            while (from >= 0 && timeline.TickAt(from).Channels[c].Started is null)
            {
                from--;
            }

            if (from < 0 || timeline.TickAt(from).Channels[c].Started is not { } started || started.IsSilence)
            {
                return null;
            }

            SampleBlock block = started;
            double position = 0;
            for (int v = from; v < bounds.Start; v++)
            {
                PlaybackTick tick = timeline.TickAt(v);
                ChannelTick channel = tick.Channels[c];
                if (!channel.Playing)
                {
                    return null;
                }

                position += channel.Pitch.PlaybackRateHz * tick.DurationSeconds;
                while (position >= block.Frames)
                {
                    position -= block.Frames;
                    block = channel.Next;
                    if (block.IsSilence)
                    {
                        return null;
                    }
                }
            }

            int frame = (int)position;
            return block with { StartFrame = block.StartFrame + frame, Frames = block.Frames - frame };
        }

        /// <summary>The instrument that plays <paramref name="sample"/>: the channel's own when it does, else the first that does.</summary>
        private int InstrumentFor(int sample, int? carried)
        {
            if (carried is { } own && song.Instruments[own].Sample == sample)
            {
                return own;
            }

            for (int i = 0; i < song.Instruments.Count; i++)
            {
                if (song.Instruments[i].Sample == sample)
                {
                    return i;
                }
            }

            return carried ?? 0;
        }

        /// <summary>The first row with the waveform, glissando and filter settings made before the window.</summary>
        private Cell[] FirstRowState(Cell[] cells, CarriedState state)
        {
            for (int c = 0; c < _channels; c++)
            {
                Cell cell = cells[c];
                Effect[] settings = [.. state.Settings(c).Where(s => cell.Effects.All(e => e.GetType() != s.GetType()))];
                if (settings.Length > 0)
                {
                    cells[c] = cell with { Effects = [.. settings, .. cell.Effects] };
                }
            }

            if (bounds.Start > 0 && timeline.TickAt(bounds.Start - 1).LowPassFilter && !cells.Any(cell => cell.Effects.OfType<SetLowPassFilter>().Any()))
            {
                cells[0] = cells[0] with { Effects = [new SetLowPassFilter(true), .. cells[0].Effects] };
            }

            if (cells.Any(cell => cell.Effects.Any(e => e is Vibrato or Tremolo)))
            {
                notes.Add("A vibrato or tremolo running at --start restarts its wave on the window's first row in a DMF");
            }

            return cells;
        }

        /// <summary>Points every tick at its new order and row.</summary>
        private void Renumber(List<PlaybackTick> ticks)
        {
            for (int i = 0; i < RowTicks.Count; i++)
            {
                int last = i + 1 < RowTicks.Count ? RowTicks[i + 1] : ticks.Count;
                for (int t = RowTicks[i]; t < last; t++)
                {
                    ticks[t] = ticks[t] with { Order = _positions[i].Order, Row = _positions[i].Row };
                }
            }
        }

        /// <summary>The last row stops the song or jumps to the loop row, which gets back the speed and tempo it had the first time.</summary>
        private void End((int Speed, double Bpm) atLoop, CarriedState state, int? loopIndex)
        {
            List<Row> last = _patterns[^1];
            if (loopIndex is not { } index)
            {
                last[^1] = WithEffect(last[^1], new StopSong());
                return;
            }

            (int order, int row) = _positions[index];
            last[^1] = WithEffect(last[^1], new PositionJump(order));
            Row loopRow = _patterns[order][row];
            if (atLoop.Speed != state.Speed && !loopRow.Cells.Any(c => c.Effects.OfType<SetSpeed>().Any()))
            {
                loopRow = WithEffect(loopRow, new SetSpeed(atLoop.Speed));
            }

            if (Math.Abs(atLoop.Bpm - state.Bpm) > BpmTolerance && !loopRow.Cells.Any(c => c.Effects.OfType<SetTempo>().Any()))
            {
                loopRow = WithEffect(loopRow, new SetTempo((int)Math.Round(atLoop.Bpm)));
            }

            _patterns[order][row] = loopRow;
        }

        private WindowSummary Summary(Playback cut)
        {
            double startSeconds = timeline.TimeOf(bounds.Start);
            SourcePoint Point(int v) => new(timeline.TickAt(v).Order, timeline.TickAt(v).Row, timeline.TimeOf(v));
            SourcePoint? end = bounds.End >= timeline.TickCount && !timeline.Loops ? null : Point(bounds.End);
            SourcePoint? loop = bounds.Loop is { } l ? Point(l) : null;
            return new WindowSummary(
                Point(bounds.Start),
                end,
                timeline.TimeOf(bounds.End),
                loop,
                bounds.Loop is { } at ? timeline.TimeOf(at) - startSeconds : null,
                null,
                cut.DurationSeconds,
                (bounds.Start, bounds.End, bounds.Loop ?? -1));
        }

        /// <summary>The channel's volume on the tick before timeline tick <paramref name="v"/>, or <c>null</c> at the song's start.</summary>
        private double? VolumeBefore(int v, int c) => v > 0 ? timeline.TickAt(v - 1).Channels[c].Volume : null;

        /// <summary>The source row <paramref name="tick"/> plays.</summary>
        private Row SourceRow(PlaybackTick tick) => song.Patterns[song.Orders[tick.Order]].Rows[tick.Row];

        /// <summary>The pitch of the note written on <paramref name="tick"/>'s row on channel <paramref name="c"/>, as the tick player tuned it.</summary>
        private NotePitch? Pitch(PlaybackTick tick, int c) =>
            timeline.Playback.NotePitches.TryGetValue(new NotePosition(tick.Order, tick.Row, c), out NotePitch pitch) ? pitch : null;

        private Row EmptyRow() => new([.. Enumerable.Repeat(Cell.Empty, _channels)]);
    }

    /// <summary>The cell without the effects that move through the song: the window's rows are already in play order.</summary>
    private static Cell Strip(Cell cell) =>
        cell.Effects.Any(IsFlow) ? cell with { Effects = [.. cell.Effects.Where(e => !IsFlow(e))] } : cell;

    private static bool IsFlow(Effect effect) => effect is PositionJump or PatternBreak or StopSong or PatternLoopStart or PatternLoop;

    /// <summary><paramref name="row"/> with <paramref name="effect"/> added to its first cell.</summary>
    private static Row WithEffect(Row row, Effect effect)
    {
        Cell[] cells = [.. row.Cells];
        cells[0] = cells[0] with { Effects = [.. cells[0].Effects, effect] };
        return new Row(cells);
    }
}
