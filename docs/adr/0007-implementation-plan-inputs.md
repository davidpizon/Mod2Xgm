# ADR 0007: Implementation plan inputs

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

Inputs David gave for [implementation-plan.md](../implementation-plan.md): order of outputs, first mapper, libraries,
CI and tools.

## Decision

- Build order: **DMF first**, then XGM2, then XGM v1 ([implementation-plan.md](../implementation-plan.md)).
- DMF mapping: simple mapper first (busiest percussive channel → DAC, rest FM from a patch library), then
  auto-hybrid chosen by the accuracy harness.
- **System.CommandLine** approved for CLI parsing; **xUnit v3** for tests.
- CI: GitHub Actions on Windows, Linux, macOS; AOT artifacts per OS.
- DMF spec gaps closed with the DefleMask manual and Furnace's DMF loader (reference only, GPL — no code copied).
- Harness tools available locally: openmpt123/libopenmpt, DefleMask WAV export, SGDK + BlastEm/Genesis Plus GX.
- Code is written by Claude Code on David's PC, one plan task per PR.

## Consequences

Library approvals continue in later ADRs (e.g. [ADR 0017](0017-cli-parsing.md)).
