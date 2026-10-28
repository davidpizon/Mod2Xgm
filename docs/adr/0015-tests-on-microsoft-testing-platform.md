# ADR 0015: Tests on Microsoft.Testing.Platform (PR #7)

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

xunit.v3 4.x doesn't run under the .NET 10 SDK's VSTest-based `dotnet test`.

## Decision

- xunit.v3 4.x runs on Microsoft.Testing.Platform; the .NET 10 SDK's VSTest-based `dotnet test` rejects it
  ("Testing with VSTest target is no longer supported"). `global.json` now selects the MTP runner.
- Coverage moves from coverlet.collector (a VSTest data collector) to **Microsoft.Testing.Extensions.CodeCoverage**
  (Cobertura output; settings in `tests/coverage.runsettings`). Test-only, not shipped.

## Consequences

Coverage command in [testing.md](../testing.md#code-coverage).
