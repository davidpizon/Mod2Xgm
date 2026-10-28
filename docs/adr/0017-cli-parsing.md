# ADR 0017: CLI parsing (T1.1)

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

Plan task T1.1 needed an AOT-safe command-line parser and a policy for parse errors.

## Decision

- **System.CommandLine 2.0.13** added (MIT). Options are declared as text and converted/validated in
  `OptionsBinder`, which collects all errors; parse and validation errors exit 1.
- The library's default exception handler is off, so unexpected exceptions reach `Program.Run` and exit 5.
- `--reserve-sfx-channels` must be 0 for DMF.

## Consequences

CLI contract in [cli.md](../cli.md).
