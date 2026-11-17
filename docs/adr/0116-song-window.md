# ADR 0116: Converting a window of the song: cut the song model and slice the playback

- **Status:** Accepted
- **Date:** 2026-11-17

## Context

David wanted a MOD converted up to a set time (a game's jingle ended at 2:05). Without options for this, the only way
was to patch an `F00` into a copy of the MOD by hand. Plan task T18.1 adds options to convert a window of the song.
David decided how they behave (2026-11-17):

- `--start` and `--end` take a time (`125`, `2:05`, `2:05.250`) **or** a tracker position (`o11r25`).
- Positions **snap to the nearest row start**, and a conversion note gives the row and time used.
- After `--end` the song **stops**. A separate `--loop-from` makes it loop instead.
- `--fade-out <time>` fades to silence, **evenly in dB**.
- An `--end` past the end of a song that stops is **cut to the song's end with a note**, not refused.
- A DMF that can't really stop is left as it is for now (follow-up T18.2).

The outputs read the song in two ways:

- The XGM2 writer and the WAV render follow the tick player's `Playback` tick by tick (`GenesisStream`,
  `GenesisPlanPlayer`).
- The DMF writer reads the plan's cells order by order and never sees the `Playback` (`DmfTimingPlanner`,
  `DmfSongBuilder`).
- The tick player plays a `ModModule`, not a `Song`.

So the window must be cut where all three see it. It must also carry in everything a later start inherits: speed and
tempo, effect memory, instruments, notes still sounding, how far into their samples they are, waveforms and the filter.

## Decision

One new stage, `SongWindow.Cut` (`src/Model/Window`), runs right after the tick player. It returns a new `Song` and a
new `Playback` that agree with each other, and every later stage reads them as an ordinary song.

- **The playback is sliced, not played again.** `PlaybackTimeline` reads the whole song's playback as an endless
  timeline: the first pass, then the loop again and again.
  - A row starts on a tick with `Tick` 0 that doesn't repeat a row held by `EEx`. The new `PlaybackTick.DelayRepeat`
    marks those repeats.
  - The window takes the ticks from start to end, with times counted from the window's start and positions renumbered
    to the new rows.
  - Pitch, volume, pan, vibrato phase, pattern loops and the filter are exactly as in the whole song.
- **The rows are written again in play order.** Each row played in the window becomes one row of the new song, 64 to a
  pattern.
  - Jumps, breaks, pattern loops and stops are removed: the rows are already in play order.
  - `EEx` stays, as its repeats are in the slice.
  - Flow effects are added only where needed:
    - a `PatternBreak(0)` before the loop row, which starts an order of its own (a DMF drops a break that comes with a
      jump);
    - on the last row, a `StopSong`, or a `PositionJump` to the loop's order.
- **The start's state goes into the first rows.** `CarriedState` follows the cells from the song's start to the
  window's start: instruments, effect memory, the tone-portamento target, waveform and glissando settings, speed and
  tempo.
  - Every "same as last time" parameter in the window is filled in from it.
  - The window's `Timing` is the speed and tempo at its start. The loop row gets back the speed and tempo it had the
    first time, if the end changed them.
  - The first note in the window with no instrument gets the one chosen before.
  - A note still sounding at the start is written again on the first row: same pitch, instrument and volume, with a
    `SampleOffset` at the frame it had reached. The window's first tick starts that part of the sample.
  - A tone portamento running into the window gets its target note written on its first row there.
  - A filter that is on gets a `SetLowPassFilter(true)`.
- **Snapping and ends.**
  - A time goes to the nearest row start, the earlier one on a tie.
  - A tracker position is the first time that row plays (at or after `--start`, for the end and the loop).
  - `--end` is exclusive: that row isn't played.
  - Without `--end`, the window keeps the song's own ending: it stops where the song stops, or plays to the end of the
    first pass and loops to the song's loop point.
  - A `--start` inside the loop **rotates** it: the window plays one whole turn of the loop from there and loops back
    to its start.
  - An `--end` past the first pass plays the loop again to reach it.
- **The fade** drops 48 dB evenly over its length, and the last tick is silent.
  - It scales every tick's channel volumes, which the XGM2 and the WAV follow.
  - On each row in the fade it sets each playing channel's volume to the faded one, rounded to 1/64, which is how a
    DMF fades: one step per row. A row with a fine volume slide on its first tick is left as it is, so the slide isn't
    counted twice.
- **Every lossy step or assumption is a conversion note:**
  - where each position snapped, and an end that was cut short;
  - how many notes were started again (FM envelopes restart) and how many portamento targets were written again;
  - how many patterns the window became;
  - a rotated loop, and a loop played again;
  - a vibrato or tremolo whose wave restarts in a DMF;
  - the fade's DMF row steps, DAC recordings that keep the volume they started at, notes that start during the fade
    (quieter recording copies), and tremolo in the fade.
- **The rest of the run.**
  - Report sections 1–2 still describe the whole MOD.
  - A `window` phase and a `Window:` report line (a `window` row in `text` and `json`) say what is converted.
  - The window goes into the candidate cache key, so a window never gets the whole song's candidates.
  - Usage errors (exit 1): options that conflict are found when the command line is read, orders the song doesn't
    have after report sections 1–2, and positions it never reaches once it is played.

## Options considered

- **Chosen: cut the `Song` and slice the `Playback` in one model stage.** The XGM2 and the WAV get the exact sound of
  the whole song, and the DMF reads ordinary cells that carry the state in. All three come from one plan.
- **Patch the MOD (an `F00`, or rows that set the state) and play it again**, as was done by hand for the 2:05 cut.
  Rejected:
  - A MOD cell holds one effect, so the first row couldn't set speed, tempo, volume, offset and waveforms together
    without extra rows, which would change the timing.
  - `9xx` moves in 256-byte steps.
  - The MOD can't restore a vibrato's phase, a portamento's current period or the filter.
- **Cut the `GenesisPlan` after mapping.** Rejected:
  - The optimizer would fit, allocate and check capacity for the whole song, so a short window would get no benefit.
  - Each mapper's plan would need its own state rebuilt.
  - The plan is per order, so it would end up rebuilding the same rows in play order, one layer later.
- **Cut at the exact frame, not the row.** Rejected by David: snapping keeps the DMF's rows whole and says what was
  used.

## Consequences

- New code in `src/Model/Window`: `PlaybackTimeline`, `CarriedState`, `SongWindow`, `FadeOut` and `SongPosition` with
  the request. The CLI options go in `src/Cli`, `ConversionCommand` gets the `window` phase, and `src/Output/WindowReport`
  is new.
- A window's DMF can have more orders than the MOD, because its patterns are no longer shared. The existing 127-order
  note covers a window that is too long.
- A DMF can't stop: after the window's end it jumps back to its start at full volume (the existing "song stops became
  jumps" note). T18.2 will add a silent pattern that jumps to itself (own ADR).
- An FM voice restarts its envelope where the window starts mid-note.
- The harness doesn't use windows; its runs are unchanged.
- Docs: [cli.md](../cli.md#converting-part-of-a-song), [cli-output.md](../cli-output.md) (phase, `window` row),
  [architecture.md](../architecture.md), [implementation-plan.md](../implementation-plan.md) (M18).
