# ADR 0012: XGM2 extension

- **Status:** Accepted; supersedes the extension part of [ADR 0001](0001-initial-scope.md)
- **Date:** 2026-10-27

## Context

XGM and XGM2 outputs shared the `.xgm` extension, so the output alone didn't say which format it was.

## Decision

- `--format xgm2` writes **`.xgm2`** (was `.xgm` for both). XGM v1 keeps `.xgm`.
- The extension mismatch with SGDK (which expects `.xgm`) is **not a concern**; every XGM2 conversion prints
  `NOTE: SGDK expects XGM2 files to use the .XGM file extension.`
- Still open (unrelated to extension): rescomp documents only VGM input for `XGM2`; confirm SGDK can ingest
  our XGM2 data (T10.2).

## Consequences

Every XGM2 conversion prints the SGDK extension note ([cli.md](../cli.md)).
