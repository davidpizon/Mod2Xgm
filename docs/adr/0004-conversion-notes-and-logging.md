# ADR 0004: Conversion notes and logging

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

Lossy conversion is expected and must never be silent; the word "warning" was already taken by build warnings.

## Decision

- Lossy-conversion diagnostics are called **conversion notes** ("warning" is reserved for build warnings,
  which are errors). They do **not** fail the conversion.
- Logging uses **Serilog** + **Serilog.Sinks.Console**, configured in code (AOT-safe). Conversion notes are
  logged at `Warning` level to the console (stderr).

## Consequences

Every lossy step, assumption and sign of a damaged input becomes a conversion note (AGENTS.md § Code conventions).
