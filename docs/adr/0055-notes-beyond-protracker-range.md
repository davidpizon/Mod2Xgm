# ADR 0055: Notes beyond ProTracker's range

- **Status:** Accepted; amends [ADR 0033](0033-tick-player.md); "every other song is unchanged", the 2^(−finetune/96) tuning, the semitone arpeggio and the unsnapped glissando are superseded by [ADR 0060](0060-openmpt-pitch-and-loops.md)
- **Date:** 2026-10-30

## Context

The tick player is a port of ProTracker 2's replayer, quirks included ([ADR 0033](0033-tick-player.md)). ProTracker
knows three octaves (C-1 to B-3, periods 856 to 113). Some MODs were written in trackers with more octaves and hold
notes outside that range. ProTracker looks such a note up in its period table, finds nothing and plays period 0
(silence) or clamps a slide at 113, which gives a stuck tone.

The in-app MOD renderer (T7.1, [ADR 0054](0054-mod-renderer.md)) showed it in `10kdub`: the melody was missing, and a
slide stuck at the 12 s mark. David listened to both versions: "The sound is wrong. That is a strike against using the
real Amiga as a standard." OpenMPT plays those notes at their pitch.

## Decision

- **A song with any note outside every ProTracker finetune row plays its notes as OpenMPT does** (David). Outside the
  rows means a note period below 108 (B-3 at finetune +7) or above 907 (C-1 at finetune −8), in a pattern the song
  plays. Then:
  - such a note snaps to the nearest note of OpenMPT's six-octave table (C-0 = 1712 … B-5 = 28) and finetune tunes it by
    2^(−finetune/96);
  - slides (`1xx`, `2xx`, `3xx`, and their fine forms) run from period 14 to 3424, and a slide past 0 stops at 14 instead
    of wrapping as in ProTracker;
  - above ProTracker's top note, arpeggio moves by semitone ratios and glissando doesn't snap, since ProTracker's table
    has no notes there.
- **Every other song is unchanged:** ProTracker's tables and its 113 / 856 limits, bugs included.
- **One conversion note says so,** with the number of notes beyond the range, e.g. `12 notes lie beyond ProTracker's
  range … they play at their own pitch and slides reach periods 14–3424, as in OpenMPT`.
- The limits were measured with libopenmpt 0.8.9.

## Options considered

- **Switch to OpenMPT's rules only for songs that need them** (chosen): songs within ProTracker's range keep their exact
  ProTracker timing and quirks, and the songs that left it sound as their author heard them.
- **Keep ProTracker's behaviour:** faithful to a real Amiga, but David heard it and called it wrong.
- **OpenMPT's rules for every song:** changes ProTracker songs that are right today, e.g. the slide clamps at 113.
- **Drop the notes with a conversion note:** the melody would still be missing.

## Consequences

- `src/Mod/ModTickPlayer.cs` holds OpenMPT's table and the extended limits; ADR 0033's "quirks and bugs included" now has
  this one exception.
- The renderer's harness check no longer exempts songs with high notes.
- DMF output doesn't change: the DMF writer reads the `Song`, not the tick player
  ([architecture.md](../architecture.md)). The in-app renderer and, later, the XGM writers (M10, M11) get these periods
  from the tick player, so those writers must handle pitches beyond three octaves.
