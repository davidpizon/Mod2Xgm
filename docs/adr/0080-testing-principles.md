# ADR 0080: Tests follow written principles: AAA, `sut`, `Method_Scenario_Expected`, unit vs integration

- **Status:** Accepted
- **Date:** 2026-11-04

## Context

By early November 2026 the suite had 945 tests in two projects. They were written plan task by plan task, without a
shared style. Names were sentences (`EachNoteIsLoggedAsAWarning`). Arrange, act and assert weren't marked. The object
under test had many names. Some tests looped over cases or asserted with `Assert.True(a == b)`. Unit tests and tests
that read goldens or run the whole CLI weren't told apart. David asked for the principles of production-grade unit
testing to be written down and the test projects refactored to follow them.

## Decision

(David, 2026-11-04.)

- **[testing-principles.md](../testing-principles.md) is the standard** for every test in `tests/`. It covers AAA
  with comments, `sut` for the object under test, `Method_Scenario_ExpectedBehavior` names (XML doc comments still
  required), determinism and no shared state, specific assertions, and no logic in tests (cases become `[Theory]`
  rows, collections use `Assert.All`; a loop may build input in Arrange).
- **All existing tests are refactored in one change** (rename all; fix violations), not only new ones.
- **Tests that do I/O are integration tests**, tagged `[Trait("Category", "Integration")]`: goldens, probes, corpus,
  end-to-end CLI runs, the harness tests. They stay in their projects, so coverage is still measured on
  `Mod2Xgm.Tests` alone ([ADR 0047](0047-accuracy-harness.md)). All other rules apply to them.
- **"Public API" means the type's non-`private` members.** Nearly everything in `src/` is `internal` and visible to the
  tests through `InternalsVisibleTo`. Tests never use reflection to reach `private` members.
- **Test doubles use Moq** for interfaces ([ADR 0081](0081-moq-for-test-doubles.md)). Data and maths are never mocked.

## Options considered

- **Rename all and fix violations in one change (chosen):** the whole suite reads one way at once, at the cost of
  a very large diff.
- **Fix violations only:** small diff, but two naming styles would live side by side for a long time.
- **Pilot a few folders first:** less risk, but a style David already chose would be reviewed twice.
- **Move I/O tests to a separate integration project:** a cleaner split, but it changes CI and coverage
  measurement (ADR 0047) for little gain over a trait.

## Consequences

- Every new or changed test follows the checklist at the end of testing-principles.md, and review checks it.
- `--filter-not-trait "Category=Integration"` runs the fast unit tests alone while iterating.
- Test names change everywhere. Filters by old names (in notes or scripts) must be updated. No CI job filters by
  test name.
