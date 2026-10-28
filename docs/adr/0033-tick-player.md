# ADR 0033: Tick player (T3.4)

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

Plan task T3.4: playing the song tick by tick, so the renderers and writers can follow it exactly.

## Decision

- **A port of pt2-clone's ProTracker 2.3 replayer** (`src/pt2_replayer.c`, BSD-3-Clause), quirks and bugs included
  (David, over writing it from the docs or "simple first, exact later").
- **It reads the MOD's raw cells** (David, over playing the `Song`): the port works on the same numbers ProTracker did.
  It lives in `src/Mod`; the `Song` keeps feeding the DMF writer.
- **Output: the full picture of every channel on every tick** (David, over a list of changes): `Playback` in `src/Model`
  — pitch, volume, pan, the sample part that starts and the part that repeats (Paula's registers), plus the LED filter.
- **Once through, with the loop point marked** (David, over twice through or a fixed length). The end follows
  pt2-clone's MOD2WAV row-visit rule; `F00` ends without a loop.
- **Checked with hand-worked test songs now; the openmpt123 corpus comparison moves to the harness (T6.3)** (David):
  the corpus doesn't exist before M6.
- Decided while implementing: ProTracker's exact CIA tick rate (`709 379 / (1 773 447 / BPM + 1)`), BPM changes one
  tick late; NTSC input scales it by 6/5 (David's 60 ticks/s rule). Ultimate Soundtracker cells are turned into
  ProTracker commands as OpenMPT does and played by the same engine at speed 6 and the byte-471 tempo, with samples
  starting at their loop start (matching `ModToSong`). Not reproduced, with a note: `EFx` invert loop (it rewrites
  sample data while playing). Not emulated: `E8x` Karplus-Strong (off by default in pt2-clone too). Empty sample slots
  play silence (ProTracker would play whatever memory follows). Fitting ticks onto Genesis frames (`--output-region`)
  stays with the writers. `Song.LoopOrder` stays `null`: a loop can start mid-pattern, so the writers take the loop
  point from `Playback`.

## Consequences

pt2-clone's license is in THIRD-PARTY-NOTICES.md.
