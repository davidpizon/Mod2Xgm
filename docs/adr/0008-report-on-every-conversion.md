# ADR 0008: Report on every conversion

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

Sample names often hold credits and the sample table explains capacity problems, so users need them even when a
conversion fails.

## Decision

- Whenever a MOD file is converted, the MOD metadata and full sample table are read and printed —
  mandatory, every output format, no flag to suppress, printed before mapping so they appear even if
  conversion fails. `--info` became a dry run (full report + notes, no file written).

## Consequences

`--info` is a dry run ([cli.md](../cli.md)).
