# ADR 0006: PCM budget and mid-song swapping confirmed

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

XGM/XGM2 PCM shares ROM with the rest of a game, and a track can need more sounds than the slots allow.

## Decision

- Default PCM byte budget for XGM/XGM2: **128 KB per song** (proposed 2 MB, then 256 KB — both too large
  when a ROM holds several songs). Rationale: 3.125 % of a 4 MB cart, ≈ 9.9 s of 13.3 kHz PCM; budget
  pressure is handled by consolidation (lower rates, dedupe, merges). Overridable with `--max-pcm-bytes`.
- **Mid-song sound swapping confirmed:** when slots run out, a track switches to a substitute sound (other
  sample or FM/PSG approximation) for a section of orders, never playing a wrong pitch.

## Consequences

The budget is overridable with `--max-pcm-bytes` ([sample-capacity.md](../sample-capacity.md)).
