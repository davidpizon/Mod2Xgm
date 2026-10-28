# ADR 0001: Initial scope

- **Status:** Accepted; the output extension for XGM2 is superseded by [ADR 0012](0012-xgm2-extension.md)
- **Date:** 2026-10-27

## Context

The project started as a MOD → DefleMask converter; before any code, the outputs, the MOD variants read in v1 and the
XGM2 reference had to be fixed.

## Decision

- **XGM output is written natively**, no external `xgmtool`/`xgm2tool`. Reason: single self-contained
  AOT exe, no toolchain dependency.
- **Both XGM and XGM2**, selected by `--format xgm|xgm2`. (Extension changed — see [ADR 0012](0012-xgm2-extension.md).)
- **MOD input v1:** 4-channel 31-sample ProTracker (`M.K.`, `M!K!`, `FLT4`, `4CHN`) and 15-sample
  Soundtracker. 6/8+ channel MODs rejected for now.
- **XGM2 reference:** SGDK `bin/xgm2.txt`, format version `0x10`.
- **Channel mapping:** **open** — see [channel-mapping.md](../channel-mapping.md).
- The fileformat.info "DMF" page (corion) documents X-Tracker's DDMF format, **not** DefleMask — not used.

## Consequences

Channel mapping stays open (see [channel-mapping.md](../channel-mapping.md)).
