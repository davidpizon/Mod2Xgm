# ADR 0002: Every warning is an error

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

A converter shipped as one Native AOT executable needs trim/AOT analyzer warnings to be fixed, not ignored.

## Decision

- Build: `TreatWarningsAsErrors`, `MSBuildTreatWarningsAsErrors`, `IsAotCompatible`, `AnalysisLevel=latest`,
  `EnforceCodeStyleInBuild` in `src/Mod2Xgm.csproj`. Suppressions need explicit user approval.

## Consequences

Suppressions need David's explicit approval (AGENTS.md § Every warning is an error).
