# ADR 0014: Code coverage is an agent requirement

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

GitHub's code coverage ruleset isn't available for this repository.

## Decision

- The GitHub **Code coverage** ruleset is disabled: `actions/upload-code-coverage` returned HTTP 404 (code
  coverage isn't available for this repository).
- Instead, **agents enforce it**: line coverage of `Mod2Xgm` ≥ 80 % and no drop > 10 points vs `main`, measured
  locally and stated in each PR description (AGENTS.md § Testing expectations).
- CI's `Code coverage` job reports the number (job summary, `Line coverage` annotation, artifact) and **fails
  below 80 %** (added the same day: "Require 80% code coverage"). The 10-point drop stays an agent check.

## Consequences

AGENTS.md § Testing expectations.
