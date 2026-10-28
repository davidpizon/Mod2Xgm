# ADR 0029: Song model (IR) types (T3.1)

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

Plan task T3.1: the format-neutral song model between the MOD reader and the writers.

## Decision

- **Scope:** types only; `ModToSong` fills them in T3.2–T3.4 (David, over "also copy the easy parts now").
- **Sample sound:** `float[]` −1…+1 (David, over `short[]` and keeping `sbyte[]`): resampling and scoring work on
  it directly, and 8-bit input converts back exactly.
- **Pitch:** both a musical pitch (semitone + unrounded cents) and the exact playback rate in Hz (David, over either
  alone): writers place notes, renderers (T7.1) need period-exact pitch.
- **Effects:** one named record per kind (David, over a generic code + numbers), and the full ProTracker list now
  (David, over adding kinds in T3.3), so T3.3 may still adjust parameters. Units are neutral (volume 0–1, pan
  −1…+1, cents, ticks, rows); slides name their scale (`AmigaPeriod` or `Cents`) because an Amiga period slide can't
  be expressed exactly in cents; `null` parameters mean effect memory, resolved by the tick player (T3.4).
- Decided while implementing: 0-based numbering inside the IR; MOD `Cxx` becomes the cell's `Volume`, not an effect;
  the IR keeps every sample, ignored ones included (the budgeter applies `--ignore-samples`); the Genesis region
  stays an output option instead of a song field (architecture.md had listed it under `Timing`); no per-sample
  "source rate", since each note carries its exact rate. Invalid references throw `ArgumentException` (a reader bug,
  exit 5).
- "No MOD types leak" is enforced by a test that scans `src/Model` sources and checks every model member signature.

## Consequences

Layers in [architecture.md](../architecture.md).
