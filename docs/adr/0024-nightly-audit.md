# ADR 0024: Nightly audit files issues for Qodana and NuGet findings

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

Agent sessions can't read code-scanning alerts (HTTP 403), so findings need a channel they can read.

## Decision

- Code-scanning alerts aren't readable from agent sessions (the API returns 403), so findings need another channel.
  `.github/workflows/nightly-audit.yml` (ported from TotallyHot-ArcRouter) runs Qodana and
  `dotnet list package --vulnerable` daily at 10:00 UTC and files one issue per finding (`audit:smell`,
  `audit:vulnerability`, `severity:*`), deduplicated by a fingerprint in the issue body. It never opens PRs or pushes.
- No baseline yet, by David's choice: the first run files every current Qodana finding. Once a baseline is committed
  (`.qodana/qodana.sarif.json`), both workflows use it and only findings outside it are filed.
- **License:** the ported files come from TotallyHot-ArcRouter (AGPL-3.0). David owns their copyright and licenses
  them under MIT in this repository (confirmed 2026-10-27), so they're not third-party code.
- The NuGet job is kept alongside Dependabot alerts so dependency findings are also readable as issues.
- Adapted for this repo: paths resolve against the checkout (solution at the root, projects in `src/` and `tests/`),
  issue text points at AGENTS.md, writes are spaced 1 s apart for GitHub's secondary rate limit, and CI runs the
  script's `--self-test`.

## Consequences

Read findings with `gh issue list --label audit:smell` (AGENTS.md).
