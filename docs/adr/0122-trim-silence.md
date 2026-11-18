# ADR 0122: `--trim-silence` leaves out the silence at a song's start or end

- **Status:** Accepted
- **Date:** 2026-11-17

## Context

David: "I would like a CLI option to trim the dead audio at the beginning/end/both of the song." brun_jul converted with
`--end 2:05` ends with 3.6 s in which nothing sounds (every channel's volume has reached 0); other MODs start with empty
rows. Plan task T18.3, on top of the song window (T18.1, [ADR 0116](0116-song-window.md)).

## Decision

**`--trim-silence start|end|both` moves the window's ends in past the silence there** (`SongWindow.Trim`, `SilenceScan`):

- **Silence is read from the MOD's playback**, not from a render: a tick is silent when no channel plays a part of a sample
  at a volume above 0. A channel plays the part it started, then the parts that follow it as each runs out, so a one-shot
  that has played out is silent; a looped sample sounds for as long as its volume is above 0. The same answer holds for
  DMF, XGM, XGM2 and the WAV, as they all play the window cut from it.
- **It never cuts a sound.** The start moves back to the row start at or before the first tick that sounds; the end moves
  to the first row start after the last tick that sounds (the end is exclusive, as `--end`'s is).
- **It works on the part `--start` and `--end` chose**, after they resolve and before `--loop-from` and `--fade-out` are
  checked against the window, so a fade fades the trimmed end.
- **A window that loops keeps its end**, the song's own loop or `--loop-from` (and no fade): the silence before the jump back
  is part of the loop's timing. Its start never moves past the song's loop point, which the window goes back to.
- Each move is a conversion note giving the silence left out and the new start or end (order, row, time). Nothing sounding in
  the window is a usage error (exit 1).

The ticks a song plays its loop again (past the first pass) read the silence of the loop's own ticks.

## Options considered

- **From the MOD's playback, on row starts (chosen).** Exact for every format, and the cut lands where a tracker would put
  it, so DMF patterns stay whole rows.
- **From the rendered audio, by a level threshold.** Would also catch a sample that is quiet but not silent, and the tail of
  an FM release; but it trims only the WAV, needs the render before the files are written, and the DMF and XGM2 would not
  match it.
- **Trim at the exact tick.** Shorter by up to a row, but a window cut mid-row breaks the row grid the DMF writes.
- **Also trim a looping song's end.** Shortens every turn of the loop, which changes the music; `--end` does that when wanted.

## Consequences

- `--trim-silence` alone makes a window of the whole song (the `window` phase and report line run), so its conversion notes
  and summary show what was left out.
- A note at volume 0 that a later volume command brings up counts as silent until then; a sample whose frames are zeros
  (a silent sample) counts as sound. Neither has come up; a level threshold would be a later change.
- The analysis cache key already holds the window's ticks, so a trimmed conversion never reuses an untrimmed analysis.
