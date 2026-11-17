namespace Mod2Xgm.Model.Window;

/// <summary>
/// A <see cref="Playback"/> read as an endless timeline (docs/adr/0116-song-window.md): the first pass, then, for a song
/// that loops, its loop over and over. Positions on it are <em>timeline ticks</em>: tick <c>v</c> below the pass's
/// <see cref="TickCount"/> is the playback's own tick, and later ones repeat the loop. A row starts on a tick that is a
/// row's first (<see cref="PlaybackTick.Tick"/> 0) and not a repeat of a held row (<see cref="PlaybackTick.DelayRepeat"/>).
/// </summary>
internal sealed class PlaybackTimeline
{
    /// <summary>The most ticks a window may hold, as the tick player plays at most (over 11 hours at 50 Hz).</summary>
    public const int MaxTicks = 2_000_000;

    private readonly IReadOnlyList<PlaybackTick> _ticks;
    private readonly int[] _rowStarts;
    private readonly int _firstLoopRowStart;

    /// <summary>Reads <paramref name="playback"/>.</summary>
    /// <exception cref="WindowException">The playback has no ticks.</exception>
    public PlaybackTimeline(Playback playback)
    {
        ArgumentNullException.ThrowIfNull(playback);
        _ticks = playback.Ticks;
        if (_ticks.Count == 0)
        {
            throw new WindowException("The song plays nothing, so no part of it can be chosen.");
        }

        Playback = playback;
        TickCount = _ticks.Count;
        LoopTick = playback.LoopTick;
        PassSeconds = playback.DurationSeconds;
        _rowStarts = [.. Enumerable.Range(0, TickCount).Where(t => _ticks[t] is { Tick: 0, DelayRepeat: false })];
        _firstLoopRowStart = LoopTick is { } loop ? Array.FindIndex(_rowStarts, t => t >= loop) : -1;
    }

    /// <summary>The playback read.</summary>
    public Playback Playback { get; }

    /// <summary>Ticks in one pass through the song.</summary>
    public int TickCount { get; }

    /// <summary>The tick the song continues from after its pass, or <c>null</c> when it stops.</summary>
    public int? LoopTick { get; }

    /// <summary>Length of one pass, in seconds.</summary>
    public double PassSeconds { get; }

    /// <summary>True when the song repeats from <see cref="LoopTick"/>.</summary>
    public bool Loops => LoopTick is not null;

    /// <summary>Ticks in the repeated part (0 when the song stops).</summary>
    public int CycleTicks => LoopTick is { } loop ? TickCount - loop : 0;

    /// <summary>Length of the repeated part, in seconds (0 when the song stops).</summary>
    public double CycleSeconds => LoopTick is { } loop ? PassSeconds - _ticks[loop].TimeSeconds : 0;

    /// <summary>The playback tick timeline tick <paramref name="v"/> plays.</summary>
    public int RealTick(int v) => v < TickCount || LoopTick is not { } loop ? Math.Min(v, TickCount - 1) : loop + ((v - TickCount) % CycleTicks);

    /// <summary>The playback tick timeline tick <paramref name="v"/> plays.</summary>
    public PlaybackTick TickAt(int v) => _ticks[RealTick(v)];

    /// <summary>When timeline tick <paramref name="v"/> starts, from the song's start; the end of the pass for a song that stops.</summary>
    public double TimeOf(int v)
    {
        if (v < TickCount)
        {
            return _ticks[v].TimeSeconds;
        }

        if (LoopTick is not { } loop)
        {
            return PassSeconds;
        }

        int pass = (v - TickCount) / CycleTicks;
        return PassSeconds + (pass * CycleSeconds) + (_ticks[RealTick(v)].TimeSeconds - _ticks[loop].TimeSeconds);
    }

    /// <summary>The timeline ticks rows start on, from <paramref name="from"/> on, in order: endless for a song that loops.</summary>
    public IEnumerable<int> RowStarts(int from)
    {
        int first = Array.BinarySearch(_rowStarts, from);
        for (int i = first < 0 ? ~first : first; i < _rowStarts.Length; i++)
        {
            yield return _rowStarts[i];
        }

        if (LoopTick is not { } loop)
        {
            yield break;
        }

        for (int pass = Math.Max(0, (from - TickCount) / CycleTicks); ; pass++)
        {
            int passStart = TickCount + (pass * CycleTicks);
            for (int i = _firstLoopRowStart; i < _rowStarts.Length; i++)
            {
                int v = passStart + (_rowStarts[i] - loop);
                if (v >= from)
                {
                    yield return v;
                }
            }
        }
    }

    /// <summary>
    /// Where <paramref name="position"/> is on the timeline: the row start nearest to a time (the earlier one on a tie), or
    /// the first time a tracker position plays at or after <paramref name="notBefore"/>. For an end (<paramref name="isEnd"/>),
    /// a song that stops also ends at <see cref="TickCount"/>, and a time past it is cut to it (<c>Clamped</c>).
    /// </summary>
    /// <exception cref="WindowException">The song never gets there.</exception>
    public (int Tick, bool Clamped) Resolve(SongPosition position, int notBefore, bool isEnd, string option)
    {
        ArgumentNullException.ThrowIfNull(position);
        return position switch
        {
            TimePosition time => ResolveTime(time, isEnd, option),
            TrackerPosition tracker => (ResolveTracker(tracker, notBefore, option), false),
            _ => throw new ArgumentException("Unknown position.", nameof(position)),
        };
    }

    private (int Tick, bool Clamped) ResolveTime(TimePosition time, bool isEnd, string option)
    {
        if (!Loops && time.Seconds >= PassSeconds)
        {
            return isEnd
                ? (TickCount, time.Seconds > PassSeconds)
                : throw new WindowException($"{option} {time.Display}: the song ends at {SongTime.Format(PassSeconds)}.");
        }

        int previous = -1;
        foreach (int v in Candidates(isEnd))
        {
            if (v > MaxTicks)
            {
                break;
            }

            double at = TimeOf(v);
            if (at >= time.Seconds)
            {
                return (previous >= 0 && time.Seconds - TimeOf(previous) <= at - time.Seconds ? previous : v, false);
            }

            previous = v;
        }

        if (previous >= 0 && !Loops)
        {
            return (previous, false);
        }

        throw new WindowException($"{option} {time.Display} is further than this converter plays a song ({MaxTicks} ticks).");
    }

    /// <summary>Row starts from the beginning, then, for an end in a song that stops, the song's end.</summary>
    private IEnumerable<int> Candidates(bool isEnd)
    {
        foreach (int v in RowStarts(0))
        {
            yield return v;
        }

        if (isEnd && !Loops)
        {
            yield return TickCount;
        }
    }

    private int ResolveTracker(TrackerPosition tracker, int notBefore, string option)
    {
        int limit = notBefore + TickCount + CycleTicks;
        foreach (int v in RowStarts(notBefore))
        {
            if (v > limit || v > MaxTicks)
            {
                break;
            }

            PlaybackTick tick = TickAt(v);
            if (tick.Order == tracker.Order && tick.Row == tracker.Row)
            {
                return v;
            }
        }

        string after = notBefore > 0 ? " after --start" : string.Empty;
        throw new WindowException($"{option} {tracker.Display}: the song never plays order {tracker.Order} row {tracker.Row}{after}.");
    }
}
