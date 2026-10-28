# ADR 0031: MOD → song, effect decoding (T3.3)

- **Status:** Accepted; the "option later" for player rules and the 15-sample effect rule are superseded by [ADR 0032](0032-player-option.md)
- **Date:** 2026-10-27

## Context

Plan task T3.3: converting the MOD's cells, samples and effects into the song model.

## Decision

- **Scope: the whole `ModToSong` step** (David, over only an effect decoder): notes, instruments, samples and every
  cell's decoded effect. Order traversal, the loop point and timing stay in the tick player (T3.4), so `LoopOrder`
  is `null` and timing starts at speed 6 / 125 BPM.
- **ProTracker 2 decides** where players disagree (David, over OpenMPT or asking per case), e.g. zero parameters with
  no memory decode to no effect, and wave 3 ("random") plays square. **Rules are a setting** (`ModPlayer`, only
  `ProTracker2`); a command-line option is added when a real song needs another rule set (David, over adding
  `--mod-player` now or one switch per difference).
- **15-sample effects 1 and 2** are read as slides by default, with a conversion note; an Ultimate Soundtracker rule
  set is a future `ModPlayer` value (David: same approach as the rules setting; no guessing).
- Decided while implementing: `ModToSong` lives in `src/Mod`, not `src/Model` as architecture.md drew it, because the
  model may not reference MOD types. `8xx` (ignored by ProTracker 2) and `E8x` (unused) become `UnsupportedEffect`
  with a note; `Cxx` above 64 plays at 64; `Dxx` follows ProTracker's x × 10 + y and breaks to row 0 above row 63; a
  cell naming a sample above the module's count names no sample. Vibrato/tremolo depths are the table peak (255)
  times the depth, scaled as ProTracker does (period ÷ 128, volume ÷ 64). Each decoding change is one note per kind
  with a count and the first location. `ModToSong` isn't called by the conversion command yet; its first caller is
  the tick player or the DMF writer.

## Consequences

Format notes in [formats/mod.md](../formats/mod.md).
