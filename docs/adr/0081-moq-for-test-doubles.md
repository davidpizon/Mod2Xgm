# ADR 0081: Moq for test doubles (test-only)

- **Status:** Accepted
- **Date:** 2026-11-04

## Context

[ADR 0080](0080-testing-principles.md) asks tests to isolate their subject from collaborators behind an interface:
the reporter (`IConversionReporter`), progress (`IConversionProgress`, `IPhase`, `IWorkCounter`) and the channel mapper
(`IChannelMapper`). Until now the tests wrote each fake by hand (for example a `RecordingReporter` in
`PhaseTrackerTests`). AGENTS.md requires approval and an ADR for every new library.

## Decision

(David, 2026-11-04: "Adopt Moq (test-only)".)

- **Moq 4.21.0** (BSD-3-Clause, with its dependency Castle.Core, Apache-2.0) is pinned in `Directory.Packages.props`
  under *Tests*. It's referenced only by `tests/Mod2Xgm.Tests` and `tests/Mod2Xgm.Harness.Tests`, never by `src/` or
  `tools/`, so the AOT binary doesn't change.
- Moq builds its proxies in the dynamic assembly `DynamicProxyGenAssembly2`, and the interfaces are `internal`. So
  `src/Mod2Xgm.csproj` adds `<InternalsVisibleTo Include="DynamicProxyGenAssembly2" />`. This grants access only to
  that generated assembly and changes nothing at run time.
- Use: stub queries with `Setup`/`Returns`, verify commands with `Verify`, loose mocks by default
  ([testing-principles.md](../testing-principles.md#test-doubles-with-moq)).
- Hand-written fakes stay where they record detail to assert on (`CollectingSink`), or where Moq can't help
  (`IDacSource` takes `Span<byte>`, a `ref struct` Moq can't match).

## Options considered

- **Moq (chosen):** the most widely used .NET mocking library, the syntax the principles are written in, and
  permissive licenses. Version 4.20.0 shipped SponsorLink, which read the developer's git email at build time. It was
  removed in 4.20.2, and 4.21.0 doesn't include it.
- **NSubstitute (BSD-3):** a terser API, but the principles were written for Moq.
- **Hand-written fakes only:** no new dependency, but more code per test and no `Verify`.

## Consequences

- `THIRD-PARTY-NOTICES.md` lists Moq and Castle.Core under *Used only for building and testing*.
- Dependabot updates Moq with the other test tooling. Check its release notes for any return of build-time
  telemetry before merging an update.
- Tests run under the JIT, never AOT, so Moq's run-time code generation is fine there. It must never be referenced
  from `src/` (AGENTS.md hard constraint 1).
