# ADR 0003: DMF target version

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

DefleMask has saved several DMF file versions; the writer needs one target with a spec.

## Decision

- Write DMF **file version 24 (`0x18`)**, system `0x02` (Genesis, 10 channels), per `formats/DMF_SPECS.txt`
  (DefleMask v1.0.0+ spec, saved locally).

## Consequences

The test reader also opens versions 25–27 ([ADR 0035](0035-test-only-dmf-reader.md)).
