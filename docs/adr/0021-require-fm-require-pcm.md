# ADR 0021: --require-fm / --require-pcm

- **Status:** Superseded by [ADR 0041](0041-require-dac-require-psg.md)
- **Date:** 2026-10-27

## Context

Users need a way to force how a sample is rendered when the mapper's choice sounds wrong.

## Decision

- Two new sample-list options pin a sample's rendering: `--require-fm` (always FM, never PCM/PSG) and
  `--require-pcm` (always digitized PCM, never FM/PSG). Hard constraints for the mapper and optimizer.
- A sample in both lists is a usage error (exit 1). So is a sample that is both ignored and required.
- Required PCM that overlaps beyond the PCM voices: the least important note is cut (noted), never moved to FM.
  Required PCM that can't fit the slots/bytes after PCM-preserving consolidation: exit 6.

## Consequences

Options in [cli.md](../cli.md); capacity rules in [sample-capacity.md](../sample-capacity.md).
