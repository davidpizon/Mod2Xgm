namespace Mod2Xgm.Model;

/// <summary>
/// Something a cell does besides starting a note: one named type per kind (David, 2026-10-27), so writers can
/// <c>switch</c> over them and the compiler shows which kinds a writer ignores.
/// <para>Units are format-neutral: volumes 0–1, pan −1…+1, pitch in semitones or cents, times in ticks or rows. Pitch
/// slides also say which scale they move on (<see cref="PitchScale"/>), because Amiga-style players slide the period,
/// not the pitch, and the renderers must match that exactly. A parameter of <c>null</c> means "same as the last time
/// this channel used the effect" (effect memory); the tick player (plan T3.4) resolves it.</para>
/// <para>The list covers every ProTracker effect (docs/formats/mod.md#effects-protracker); readers fill it in plan T3.3.
/// The MOD command each kind comes from is named in its summary.</para>
/// </summary>
internal abstract record Effect;

/// <summary>The scale a pitch slide or vibrato moves on.</summary>
internal enum PitchScale
{
    /// <summary>Amiga period units: equal steps in period, so a slide sounds faster at high pitch (MOD, XM's Amiga mode, S3M).</summary>
    AmigaPeriod,

    /// <summary>Cents: equal steps in pitch (XM's linear mode, IT).</summary>
    Cents,
}

/// <summary>When a slide is applied within a row.</summary>
internal enum SlideTiming
{
    /// <summary>On every tick except the row's first (MOD <c>1xx</c>, <c>2xx</c>, <c>Axy</c>).</summary>
    EveryTickAfterFirst,

    /// <summary>Once, on the row's first tick (MOD "fine" slides <c>E1x</c>, <c>E2x</c>, <c>EAx</c>, <c>EBx</c>).</summary>
    OnceAtRowStart,
}

/// <summary>Shape of a vibrato or tremolo cycle.</summary>
internal enum Waveform
{
    /// <summary>Sine wave (the default).</summary>
    Sine,

    /// <summary>Ramp down (saw).</summary>
    RampDown,

    /// <summary>Square wave.</summary>
    Square,

    /// <summary>Random values.</summary>
    Random,
}

/// <summary>Cycles the note through three pitches, one per tick (MOD <c>0xy</c>).</summary>
/// <param name="FirstSemitones">Semitones above the note for the second tick of each cycle.</param>
/// <param name="SecondSemitones">Semitones above the note for the third tick of each cycle.</param>
internal sealed record Arpeggio(int FirstSemitones, int SecondSemitones) : Effect;

/// <summary>Slides the pitch up or down (MOD <c>1xx</c>/<c>2xx</c>, fine <c>E1x</c>/<c>E2x</c>).</summary>
/// <param name="Amount">Step per application on <paramref name="Scale"/>; positive raises the pitch. <c>null</c>: effect memory.</param>
/// <param name="Scale">What one unit of <paramref name="Amount"/> is.</param>
/// <param name="Timing">When the step is applied.</param>
internal sealed record PitchSlide(double? Amount, PitchScale Scale, SlideTiming Timing) : Effect;

/// <summary>Slides from the playing pitch toward this cell's note instead of restarting it (MOD <c>3xx</c>, and the slide half of <c>5xy</c>).</summary>
/// <param name="Speed">Step per tick (after the first) on <paramref name="Scale"/>. <c>null</c>: effect memory.</param>
/// <param name="Scale">What one unit of <paramref name="Speed"/> is.</param>
internal sealed record TonePortamento(double? Speed, PitchScale Scale) : Effect;

/// <summary>Wobbles the pitch (MOD <c>4xy</c>, and the vibrato half of <c>6xy</c>).</summary>
/// <param name="CyclesPerTick">Speed: waveform cycles per tick (MOD: x / 64). <c>null</c>: effect memory.</param>
/// <param name="Depth">Peak deviation on <paramref name="Scale"/>. <c>null</c>: effect memory.</param>
/// <param name="Scale">What one unit of <paramref name="Depth"/> is.</param>
internal sealed record Vibrato(double? CyclesPerTick, double? Depth, PitchScale Scale) : Effect;

/// <summary>Wobbles the volume (MOD <c>7xy</c>).</summary>
/// <param name="CyclesPerTick">Speed: waveform cycles per tick (MOD: x / 64). <c>null</c>: effect memory.</param>
/// <param name="Depth">Peak volume deviation, 0–1. <c>null</c>: effect memory.</param>
internal sealed record Tremolo(double? CyclesPerTick, double? Depth) : Effect;

/// <summary>Sets the channel's pan (MOD <c>8xx</c>, which ProTracker itself ignores but many players honour).</summary>
/// <param name="Pan">−1 hard left, 0 centre, +1 hard right.</param>
internal sealed record SetPan(double Pan) : Effect;

/// <summary>Starts this row's note part-way into the sample (MOD <c>9xx</c>).</summary>
/// <param name="Frame">First frame to play. <c>null</c>: effect memory.</param>
internal sealed record SampleOffset(int? Frame) : Effect;

/// <summary>Slides the volume (MOD <c>Axy</c>, and the volume half of <c>5xy</c>/<c>6xy</c>; fine <c>EAx</c>/<c>EBx</c>).</summary>
/// <param name="Amount">Step per application, as a fraction of full volume; positive is louder. <c>null</c>: effect memory.</param>
/// <param name="Timing">When the step is applied.</param>
internal sealed record VolumeSlide(double? Amount, SlideTiming Timing) : Effect;

/// <summary>After this row, continues at another order (MOD <c>Bxx</c>).</summary>
/// <param name="Order">Order index (0-based).</param>
internal sealed record PositionJump(int Order) : Effect;

/// <summary>After this row, continues at a row of the next order, or of the <see cref="PositionJump"/> target (MOD <c>Dxx</c>).</summary>
/// <param name="Row">Row index (0-based), already decoded from the source's notation.</param>
internal sealed record PatternBreak(int Row) : Effect;

/// <summary>Switches the output low-pass filter (MOD <c>E0x</c>, the Amiga LED filter).</summary>
/// <param name="On">True to filter.</param>
internal sealed record SetLowPassFilter(bool On) : Effect;

/// <summary>Makes tone portamento move in whole semitones (MOD <c>E3x</c>).</summary>
/// <param name="On">True for semitone steps.</param>
internal sealed record SetGlissando(bool On) : Effect;

/// <summary>Chooses the vibrato waveform (MOD <c>E4x</c>).</summary>
/// <param name="Shape">The waveform.</param>
/// <param name="Retrigger">True to restart the waveform on each new note.</param>
internal sealed record SetVibratoWaveform(Waveform Shape, bool Retrigger) : Effect;

/// <summary>Overrides the instrument's tuning for this note (MOD <c>E5x</c>).</summary>
/// <param name="Cents">Tuning offset in cents.</param>
internal sealed record SetFineTune(double Cents) : Effect;

/// <summary>Marks the row a <see cref="PatternLoop"/> on this channel returns to (MOD <c>E60</c>).</summary>
internal sealed record PatternLoopStart : Effect;

/// <summary>Repeats rows from this channel's <see cref="PatternLoopStart"/> (MOD <c>E6x</c>, x &gt; 0).</summary>
/// <param name="Count">How many more times to play the rows.</param>
internal sealed record PatternLoop(int Count) : Effect;

/// <summary>Chooses the tremolo waveform (MOD <c>E7x</c>).</summary>
/// <param name="Shape">The waveform.</param>
/// <param name="Retrigger">True to restart the waveform on each new note.</param>
internal sealed record SetTremoloWaveform(Waveform Shape, bool Retrigger) : Effect;

/// <summary>Restarts the note every few ticks (MOD <c>E9x</c>).</summary>
/// <param name="IntervalTicks">Ticks between restarts.</param>
internal sealed record Retrigger(int IntervalTicks) : Effect;

/// <summary>Silences the note on a tick of this row (MOD <c>ECx</c>).</summary>
/// <param name="Tick">The tick (0-based) on which the volume becomes 0.</param>
internal sealed record NoteCut(int Tick) : Effect;

/// <summary>Starts this row's note on a later tick (MOD <c>EDx</c>).</summary>
/// <param name="Tick">The tick (0-based) on which the note starts.</param>
internal sealed record NoteDelay(int Tick) : Effect;

/// <summary>Holds this row for extra row-lengths, effects still running (MOD <c>EEx</c>).</summary>
/// <param name="Rows">Extra rows to wait.</param>
internal sealed record PatternDelay(int Rows) : Effect;

/// <summary>Gradually inverts the sample's loop in place (MOD <c>EFx</c>, "funk repeat"/"invert loop").</summary>
/// <param name="Speed">Speed as stored (0 stops it); how fast it runs is the player's job.</param>
internal sealed record InvertLoop(int Speed) : Effect;

/// <summary>Sets the ticks per row (MOD <c>Fxx</c>, xx below 0x20).</summary>
/// <param name="TicksPerRow">New speed.</param>
internal sealed record SetSpeed(int TicksPerRow) : Effect;

/// <summary>Sets the tempo (MOD <c>Fxx</c>, xx 0x20 and up).</summary>
/// <param name="Bpm">New tempo; the tick rate becomes BPM × 2 / 5 Hz.</param>
internal sealed record SetTempo(int Bpm) : Effect;

/// <summary>Ends the song after this row (MOD <c>F00</c>).</summary>
internal sealed record StopSong : Effect;

/// <summary>
/// A command the reader recognised but that has no agreed meaning (e.g. MOD <c>E8x</c>, which ProTracker leaves unused). Kept, not dropped, so
/// mappers can report it as a conversion note.
/// </summary>
/// <param name="Description">What was found, for the note (e.g. "E8x with x = 3").</param>
internal sealed record UnsupportedEffect(string Description) : Effect;
