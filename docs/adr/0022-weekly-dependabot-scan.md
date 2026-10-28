# ADR 0022: Weekly Dependabot scan

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

Dependencies need regular updates and security alerts.

## Decision

- `.github/dependabot.yml`: weekly (Mondays 06:00 Pacific) version updates for NuGet (central
  `Directory.Packages.props`) and GitHub Actions; test tooling, Serilog and actions grouped into single PRs.
- Security alerts and security updates are repository settings (Settings → Code security), enabled by David.

## Consequences

AGENTS.md § Git workflow.
