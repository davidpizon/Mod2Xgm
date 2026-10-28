# ADR 0009: Libraries yes, external applications no

- **Status:** Accepted; replaces the earlier "no third-party dependencies without asking / never shell out" rule
- **Date:** 2026-10-27

## Context

An earlier rule banned dependencies without asking; FM emulation and other work call for libraries, but the converter
must stay one self-contained exe.

## Decision

- External **libraries** (NuGet packages, native C/C++ libraries via P/Invoke) may be used in the converter,
  provided they work under Native AOT on win-x64, linux-x64, osx-arm64 and are permissively licensed
  (LGPL only as a dynamically loaded native library; GPL needs approval).
- External **applications** are used only for testing (tests, accuracy harness) — never during conversion.
- Replaces the earlier "no third-party dependencies without asking / never shell out" rule.

## Consequences

Rules for adding a library are in AGENTS.md § Hard constraints.
