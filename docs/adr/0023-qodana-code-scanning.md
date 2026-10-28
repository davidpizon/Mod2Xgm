# ADR 0023: Qodana code scanning

- **Status:** Accepted; the baseline plan continues in [ADR 0024](0024-nightly-audit.md)
- **Date:** 2026-10-27

## Context

JetBrains' .NET inspections catch issues the compiler analyzers don't.

## Decision

- `.github/workflows/code_quality.yml` replicates the TotallyHot-ArcRouter Qodana workflow: Qodana Community for
  .NET in Docker, SARIF paths normalized to repository files, uploaded to GitHub code scanning (alongside CodeQL).
- No baseline yet (new project); add `.qodana/qodana.sarif.json` from a `main` run when findings accumulate.
- `QODANA_TOKEN` is optional for the Community linter.

## Consequences

Findings appear on each PR (AGENTS.md § Build, run, test).
