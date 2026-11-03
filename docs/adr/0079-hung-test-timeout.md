# ADR 0079: A test that runs 10 minutes is stopped, with a dump naming it

- **Status:** Accepted
- **Date:** 2026-11-03

## Context

Some tests hang or take far too long, and nothing stopped them: a local `dotnet test` waited for ever, and a CI job
waited until GitHub's six-hour job limit. Tests run on Microsoft.Testing.Platform (MTP) with xUnit v3
([testing.md](../testing.md#code-coverage)). xUnit's own `Timeout` works only on async tests, and most tests here are
synchronous.

## Decision

(David, 2026-11-03: "stop them at around 10 minutes".)

- **Both test projects load MTP's hang-dump extension** (`Microsoft.Testing.Extensions.HangDump`, the version of the
  platform xunit.v3 brings) and run with `--hangdump --hangdump-timeout 10m` by default, set in each project's
  `TestingPlatformCommandLineArguments`, so plain `dotnet test`, CI and IDEs all get it.
- **The limit is per test, not per run:** the timer restarts whenever a test finishes, so a long suite of quick tests
  is never stopped. Checked with a 20 s limit: three 14 s tests in a row passed; a test sleeping three minutes was
  stopped after about 20 s.
- **A stopped run fails** (exit code 7) and leaves `TestResults/<project>_<pid>_hang.log`, which names the tests
  still running, and a full memory dump beside it. CI uploads the log (not the dump, which is hundreds of MB) as the
  `hung-tests-<os>` artifact. `TestResults/` is ignored by Git.

## Options considered

- **The hang-dump extension, per test (chosen):** works for synchronous tests, needs no change to any test, and says
  which test hung.
- **xUnit `[Fact(Timeout = …)]`:** async tests only, and every test would need it.
- **MTP's `--timeout`:** one limit for the whole run, which would stop a healthy suite that just takes long.
- **A CI `timeout-minutes` only:** CI only, and it says nothing about which test hung.

## Consequences

- A single test may not run more than 10 minutes on any machine; a test that needs longer must be split, made
  smaller, or moved to the accuracy harness.
- The stopped run loses the rest of its results; rerun after fixing or skipping the hung test.
- Changing the limit: edit `--hangdump-timeout` in both test projects. The extension's version follows
  Microsoft.Testing.Platform when xunit.v3 is updated.
