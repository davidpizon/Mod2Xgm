# ADR 0011: XGM v1.01 spec saved

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

The XGM v1 writer needs the primary spec in the repository.

## Decision

- SGDK `xgm.txt` (XGM v1.01) saved as `formats/xgm.txt`; `formats/xgm.md` filled from it.
- Settled: 63 samples (4-byte SID entries, empty = `$FFFF`/`$0001`), **8-bit signed** PCM at ~14 kHz,
  256-byte aligned address and size, FM6 replaced by the driver (music gets FM1–5), single-frame `$00` wait.
- CH3 special mode for XGM: expressible, but enabled only after a probe ROM confirms (register `$27` also
  controls the YM timers).

## Consequences

CH3 special mode for XGM v1 waits for a probe ROM.
