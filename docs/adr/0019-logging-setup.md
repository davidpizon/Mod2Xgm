# ADR 0019: Logging setup (T1.3)

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

Plan task T1.3: `--verbose` and conversion notes on top of Serilog, testable without the static logger.

## Decision

- A `LoggingLevelSwitch` controls the console logger; `--verbose` raises it to Debug.
- `ConversionNotes` logs each note at Warning immediately and keeps the rendered text (string values unquoted)
  for the summary. Stages take the logger and notes as parameters for testability.

## Consequences

AGENTS.md § Logging.
