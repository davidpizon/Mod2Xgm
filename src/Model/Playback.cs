namespace Mod2Xgm.Model;

/// <summary>
/// A song played tick by tick (plan T3.4): the full picture of every channel on every tick, so the MOD renderer
/// (plan T7.1) and the XGM writers (plans M10–M11) can follow the song exactly without knowing its format. Built by a
/// format's tick player (MOD: <c>ModTickPlayer</c>, a port of ProTracker 2's replayer).
/// <para>The song plays once through. When it would repeat, <see cref="LoopTick"/> says which tick it continues from,
/// so writers can loop the output there; when it stops (MOD <c>F00</c>), there is no loop.</para>
/// </summary>
/// <param name="Ticks">Every tick, in playing order.</param>
/// <param name="LoopTick">Index into <paramref name="Ticks"/> the song continues from after the last tick, or <c>null</c> when it ends.</param>
/// <param name="End">Why the ticks end.</param>
internal sealed record Playback(IReadOnlyList<PlaybackTick> Ticks, int? LoopTick, PlaybackEnd End)
{
    /// <summary>Length of one pass through the song, in seconds.</summary>
    public double DurationSeconds => Ticks.Count == 0 ? 0 : Ticks[^1].TimeSeconds + Ticks[^1].DurationSeconds;

    /// <summary>Where the loop starts, in seconds, or <c>null</c> when the song doesn't loop.</summary>
    public double? LoopStartSeconds => LoopTick is { } tick ? Ticks[tick].TimeSeconds : null;

    /// <summary>
    /// The pitch each written note plays at, tuned the way the player tunes it (MOD: the instrument's finetune or an
    /// <c>E5x</c>, OpenMPT's periods; docs/adr/0061-dmf-pitch-from-tick-player.md), the first time its row plays. A tone
    /// portamento's note is its target. Notes on rows the song never plays are missing.
    /// </summary>
    public IReadOnlyDictionary<NotePosition, NotePitch> NotePitches { get; init; } = new Dictionary<NotePosition, NotePitch>();
}

/// <summary>Where a written note is: an index into <see cref="Song.Orders"/>, a row and a channel.</summary>
/// <param name="Order">Order (0-based).</param>
/// <param name="Row">Row (0-based).</param>
/// <param name="Channel">Song channel (0-based).</param>
internal readonly record struct NotePosition(int Order, int Row, int Channel);

/// <summary>Why a <see cref="Playback"/> ends.</summary>
internal enum PlaybackEnd
{
    /// <summary>The song would repeat from <see cref="Playback.LoopTick"/>.</summary>
    Loops,

    /// <summary>The song stops (MOD <c>F00</c>).</summary>
    Stops,

    /// <summary>The song didn't end within the tick limit; the ticks stop there (a conversion note says so).</summary>
    TooLong,
}

/// <summary>One tick: when it happens, where in the song it is, and every channel's state after it.</summary>
/// <param name="TimeSeconds">Start of the tick, from the start of the song.</param>
/// <param name="DurationSeconds">Length of the tick (the tempo can change from tick to tick).</param>
/// <param name="Order">Order being played (0-based).</param>
/// <param name="Row">Row being played (0-based).</param>
/// <param name="Tick">Tick within the row (0 = the row's first tick).</param>
/// <param name="LowPassFilter">True while the output's low-pass filter is on (MOD <c>E0x</c>, the Amiga's "LED" filter).</param>
/// <param name="Channels">One entry per song channel, in channel order.</param>
internal sealed record PlaybackTick(double TimeSeconds, double DurationSeconds, int Order, int Row, int Tick, bool LowPassFilter, IReadOnlyList<ChannelTick> Channels);

/// <summary>
/// A channel's sound during one tick, as the sound chip plays it (Amiga Paula for MODs): pitch, volume and pan, plus
/// which part of which sample it plays. A channel plays <see cref="Started"/> when it starts, then repeats
/// <see cref="Next"/> each time the playing part ends.
/// </summary>
/// <param name="Playing">True once the channel has started a sample and until the song ends; false before its first note.</param>
/// <param name="Pitch">The pitch, from the playback rate (the exact rate is <see cref="NotePitch.PlaybackRateHz"/>).</param>
/// <param name="Volume">0–1.</param>
/// <param name="Pan">−1 hard left … +1 hard right.</param>
/// <param name="Started">The part of a sample that starts playing on this tick (a new note, retrigger or delayed note), or <c>null</c>.</param>
/// <param name="Next">The part of a sample played after the current part ends (a sample's loop, or a swapped sample).</param>
internal readonly record struct ChannelTick(bool Playing, NotePitch Pitch, double Volume, double Pan, SampleBlock? Started, SampleBlock Next);

/// <summary>A stretch of one sample's frames.</summary>
/// <param name="Sample">Index into <see cref="Song.Samples"/>, or <c>null</c> for silence (an empty sample slot).</param>
/// <param name="StartFrame">First frame.</param>
/// <param name="Frames">Number of frames; a stretch of 2 frames or fewer at a sample's start is how one-shot samples end (silence follows).</param>
internal readonly record struct SampleBlock(int? Sample, int StartFrame, int Frames);
