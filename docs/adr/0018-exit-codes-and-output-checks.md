# ADR 0018: Exit codes and output checks (T1.2)

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

Plan task T1.2: how expected failures reach the user, and how outputs are protected.

## Decision

- Expected failures throw `ConversionException` carrying the exit code; `Program.Run` prints the message only.
- The output path never equals the input (exit 4 even with `--force`); output directories aren't created.

## Consequences

Exit codes in [cli.md](../cli.md).
