# ADR 0020: MOD reader (T2.1, T2.2)

- **Status:** Accepted; the 15-sample rejection is superseded by [ADR 0025](0025-fifteen-sample-soundtracker-detection.md)
- **Date:** 2026-10-27

## Context

Plan tasks T2.1–T2.2: how faithful the reader is and how it handles damaged files.

## Decision

- `ModModule` is a faithful image of the file (no interpretation); sample lengths and loops in bytes.
- Pattern count: choose between "all 128 order entries" and "played orders" by which matches the file size.
- Truncated sample data and bad loops are repaired and reported as conversion notes; structural damage is exit 3.
- Inputs over 64 MB are rejected as not a MOD; unreadable files exit 2.
- 15-sample Soundtracker files were rejected until T2.3 (see [ADR 0025](0025-fifteen-sample-soundtracker-detection.md)).

## Consequences

Format notes in [formats/mod.md](../formats/mod.md).
