# ADR 0043: DMF timing (T5.4)

- **Status:** Accepted
- **Date:** 2026-10-28

## Context

Plan task T5.4: the DMF must last as long as the MOD ("within 1 % of openmpt123"). A MOD has two dials, speed (ticks
per row) and tempo (BPM, so ticks per second: BPM × 2 / 5, × 6/5 for an NTSC Amiga). A DMF has one tick rate for the
whole song (custom Hz, whole numbers 1–999, or the region's 50/60) and two tick times (ticks per row, 1–255) that
alternate row by row, set from the start and by effects `09xx` / `0Fxx`; it has no tempo effect
([formats/dmf.md](../formats/dmf.md#4-module-information)). So a MOD's tempo changes can only be copied by changing
how many DMF ticks a row gets.

## Decision

`DmfTimingPlanner.Plan(GenesisPlan, input tick-rate scale, output region, notes)` returns a `DmfTimingPlan`
(David chose the approach and the scope, 2026-10-28):

- **Pick the song's most-used tick rate** (the speed/tempo pair that lasts longest, rows read top to bottom up to the
  first `F00`) and use it as the DMF's tick rate, rounded to whole Hz. At the song's own rate one MOD tick is one DMF
  tick, so per-tick effects (arpeggio, slides, vibrato, retrigger) need no scaling.
- **Every speed/tempo pair gets the tick times that make its rows as long as the MOD's**: tick time 1 + tick time 2 =
  `round(2 × Hz × speed / tick rate)`, the longer one first, so one row can be half a tick off. A change of either dial
  is a pair of effects (`09xx`, `0Fxx`) on its row; the plan returns them keyed by pattern and row (`TickTimes`), and
  the song's first pair goes into the module's tick times. Tempo applies from the row it is set on (as in OpenMPT,
  the accuracy reference), not from the second tick as ProTracker does.
- **Custom Hz is off only when the rate is the output region's frame rate** (50 PAL, 60 NTSC), so a PAL MOD on an
  NTSC Genesis, or the reverse, writes custom Hz (matching the table in [cli.md](../cli.md#regions)). Time base is 0.
- **The average length error stays within 1 %**, the plan's bar. When whole-Hz rounding or half-tick steps would miss
  it (e.g. 33 BPM = 13.2 Hz), the tick rate becomes a multiple (×2, ×3 …, up to 999 Hz) of the song's, and the plan's
  `TicksPerSourceTick` says which, for the effect translation (T5.5) to scale per-tick effects. The best multiple
  found is used when none reaches 1 %, with a note.
- **Looping**: DefleMask always loops to order 0 (or a `0Bxx` target), keeping its speeds. When the song ends in a
  different pair than the loop target's row 0 leaves, that row sets its pair again.
- **Conversion notes** (never silent): rounded Hz, a raised tick rate, a song with several tempos, a song that both
  changes timing and jumps or loops (timing follows rows top to bottom, so a change that depends on the way through
  the jumps may be off), and a length error of 0.1 % or more.
- **Not in scope**: writing the effects into cells and laying out orders for jumps (T5.5/T5.6), pattern delay
  (`EEx`) and pattern loops (`E6x`), which change a MOD's length and are translated or noted there.
- **Checked against the tick player**, not openmpt123: the DMF's computed length is within 1 % of
  `Playback.DurationSeconds` on songs with tempo and speed changes. The openmpt123 comparison joins the harness (T6.3).

## Options considered

- **Most-used rate, tick times for the rest (chosen):** exact on most songs (most MODs sit at 125 BPM or hold a
  multiple-of-5 tempo, which is a whole Hz), and cheap in effects.
- **Start tempo only, ignoring later tempo changes:** simplest, but a song that spends most of its time at another
  tempo ends up with the wrong length, which the plan forbids.
- **Always a high tick rate (e.g. a common multiple):** finest tick times, but arpeggio can't be scaled in DMF, and
  every song would need scaled effects. Only used when the song needs it.
- **Follow `Playback` (the tick player's real path through jumps) for the weights and states:** more exact for
  songs with jumps, but the plan has already lost its order/row identity by the time the DMF is laid out (T5.5). Revisit
  with T5.5 if jumps with different tempos on different paths turn up in the corpus.

## Consequences

- T5.5 uses `DmfTimingPlan.Changes` to fill a speed effect pair into a cell (any channel with a free effect column) and
  scales per-tick effects by `TicksPerSourceTick`; T5.6 builds the `DmfFile` and passes `Timing` through.
- The tolerance (1 %) is one constant, `DmfTimingPlanner.Tolerance`; tighten it if the corpus (T6.3) shows audible
  drift.
- `docs/formats/dmf.md` and the plan are updated in the same PR.
