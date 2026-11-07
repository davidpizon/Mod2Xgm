# ADR 0086: The capacity check counts only recordings, as the drivers store them

- **Status:** Accepted
- **Date:** 2026-11-06

## Context

Plan T9.2 asks for a capacity check, its warning, and `--no-consolidate` failing with exit 6. Until now the option was parsed and
logged but nothing read it. [sample-capacity.md](../sample-capacity.md#capacity-check-always-runs) says to compare the "required"
slots, voices and bytes with the limits ([ADR 0085](0085-target-limits.md)), but not what "required" means. A mapper has
already resolved its clashes by the time a plan exists, so simply reading the plan would always look as if the song fits.

SGDK's `xgm.txt` and `xgm2.txt` (David pointed to them for this) limit only stored samples: they have no PCM pitch, volume, or looping.

## Decision

`CapacityCheck` (`src/Optimizer/CapacityCheck.cs`) runs after mapping, on every format that gets that far:

- **Only recordings count (David).** A recording is a sample the mapper plays as PCM (`GenesisPlan.PcmSamples`), even if a clash
  moved some or all of its notes elsewhere, since those notes are what it wanted. FM and PSG notes store nothing.
- **Slots: one stored copy per sample, starting pitch, 3 dB of loudness and start point (David),** as the drivers have no pitch,
  volume or sample offset. Notes the DMF leaves silent (below −45 dB, or starting past the sample's end) store nothing.
- **Held length doesn't add a copy (implementation).** David's confirmed wording listed held length as one more copy, but a shorter note
  can stop a longer copy early (a stop command in both formats; a note-off on DMF), so it sets the copy's size only.
- **Voices: the most recordings sounding at once, tick by tick (David).** A kick still ringing when a snare starts counts as two,
  though they started apart, because you'd hear the cut.
- **Bytes: each copy on the target,** full rate, 256-byte blocks on XGM and XGM2, none on DMF ([ADR 0085](0085-target-limits.md)).
- **The count is after the free steps 0–3 of consolidation:** ignored and unplayed samples are never counted, byte-identical samples
  share copies, a one-shot's tail below −48 dB and loop data past a note's length aren't stored.
- **`--no-consolidate` fails on any limit passed, voices included (David).** Moving a drum to FM changes the song, so a DMF with two
  recordings at once stops with exit 6, with no file written. `--info` runs stop the same way.
- **The report lists only the limits passed,** one note at `Warning` level, then the "Consolidating" note if the run goes on.

## Options considered

- **The chosen one:** recordings only, counted as stored.
- **Every sample as if it stayed a recording:** nearly every song would "not fit", since a DMF plays one at a time, so the warning
  would say nothing.
- **Voices only for notes starting on the same row:** what the mapper looks at, but a cut-off tail would go unreported.
- **`--no-consolidate` stops only on slots and bytes:** fewer failures, but it would let the mapper quietly change the song.
- **Read the plan as mapped:** clashes are gone by then, so it would almost never report a problem.

## Consequences

- **Many songs with two sample tracks get the warning on DMF,** and stop under `--no-consolidate`. David accepted this: it is true of what
  you hear. The default run's output doesn't change, only its notes.
- **The warning says "Consolidating", yet only the simple mapper's existing moves run.** The real consolidation moves are T9.4.
- **A DMF that passes the check can still play a recording at the wrong pitch:** the simple mapper gives each DAC sample one pitch
  ([ADR 0040](0040-simple-mapper.md)), though the count gives each pitch a copy. The allocator (T9.3) fixes this.
- **Slides and volume changes inside a note add no copies:** consolidation step 4 is about those, and T9.4 decides.
- **XGM and XGM2 use the DMF's 3 dB loudness steps** until their writers exist (M10, M11); if a writer picks others, this count follows it.
- `--format xgm` or `xgm2` without `--render-wav` still stops with exit 1 before mapping, as there's no writer, so it never reaches the check.
- `--require-dac` samples alone not fitting stays with T9.3, whose acceptance names it.
