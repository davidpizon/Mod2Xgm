# ADR 0005: Goal: most accurate sound; report; capacity

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

Conflicting goals (sound, size, editability, simplicity) needed a ranking, and MODs can carry more sample data than
the Genesis targets hold.

## Decision

- **Goal:** the most accurate *sounding* rendition of the MOD. Sound accuracy outranks size, editability and
  simplicity. Channel mapping is judged by this (measured with the accuracy harness).
- **Console report on every run:** MOD metadata, all sample/instrument names (incl. empty slots), and the
  output instrument/sample table.
- **Capacity warning** (conversion note, `Warning`) when samples exceed XGM/XGM2 (or DMF) capacity.
- **Consolidate by default** (dedupe, trim, pitch quantize, merge, mid-song sound swaps, voice stealing);
  `--no-consolidate` fails with exit 6 instead.
- **`--ignore-samples`** lets the user leave samples out.
- Note: a MOD has ≤ 31 samples, so capacity is exceeded through pitch variants (XGM/XGM2 can't pitch PCM),
  simultaneous voices, or bytes — not raw sample count.
- XGM2 single-track sample limit corrected to **122** (was wrongly given as 248).

## Consequences

Mapping choices are judged with the accuracy harness ([testing.md](../testing.md)).
