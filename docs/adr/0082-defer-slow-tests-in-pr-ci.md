# ADR 0082: Pull request CI defers tests of 5 s or more

- **Status:** Accepted
- **Date:** 2026-11-06

## Context

David was concerned by how long the tests take and asked for GitHub's automated tests to defer the long-running ones.
Measured with the xUnit runner on a developer machine, 28 of about 2,200 tests took 5 seconds or more. They add up to
1,053 of the 1,166 seconds of test time (90 %), and one test alone takes 235 s. Nearly all are FM, CH3 or PSG fits and
the candidate cache. So most of them are slow because of the computing they do, not because they do I/O, and the
`Category=Integration` trait ([ADR 0080](0080-testing-principles.md)) can't be used to select them: it also covers
many fast tests (goldens, probes) that a pull request should keep running.

## Decision

(David, 2026-11-04: defer the long-running tests. The rule and the schedule below are the agent's proposal.)

- **A test of 5 seconds or more carries `[Trait("Speed", "Slow")]`** on the method, whatever its `Category`.
- **Pull request CI skips them** with `--filter-not-trait "Speed=Slow"`.
- **The whole suite runs on pushes to `main`, every night (09:00 UTC) and on manual runs** of the CI workflow, which
  can target any branch.
- **The coverage job always skips them**, on every trigger (David, 2026-11-06: fix why the cloud tests take so long).
  On `main` at e03f532 the same tests took 1m28s in `Build and test` and 11m58s under coverage: instrumentation slows
  the YM3438 render loop inside the fits about 8 times. No cache is involved: CI keeps none, and only
  `CandidateCacheTests` uses one, in an empty temporary folder, because the cache is what it tests.

## Options considered

- **A `Speed=Slow` trait, by measured time (chosen):** selects exactly what costs the time, and leaves every fast
  integration test in pull requests.
- **Skip `Category=Integration` in pull requests:** the name suggests it, but it would drop the goldens and probes,
  which are cheap and catch real regressions, and keep the slow fits that aren't tagged integration.
- **Keep the coverage job on the whole suite:** the number would be a little higher, but the job would stay as slow as
  before, and it isn't needed: coverage without the slow tests is 88.7 %, above the required 80 %.
- **Mock the search's renders:** those tests check that the fits get close to real chip audio, so a fake chip would
  check nothing. Their render budgets were cut where the claim doesn't need them instead.
- **Make the slow tests faster:** still worth doing test by test, but a fit's cost is what those tests exercise.

## Consequences

- In local runs `Mod2Xgm.Tests` went from 4m17s to about 34 s, and `Mod2Xgm.Harness.Tests` from 1m06s to about 14 s.
- A pull request can break a long-running test and still be green; the break then shows up on `main` or in the night's
  run. After changing what those tests cover, run them before opening the PR or start the workflow on the branch.
- The coverage number leaves those tests out everywhere, so a pull request and `main` are measured alike, and the
  10-point check versus `main` (AGENTS.md) uses the same command. Lines only the slow tests reach count as uncovered.
- A new test of 5 s or more has to be tagged by hand; nothing checks it. Re-measure if the suite slows down again.
