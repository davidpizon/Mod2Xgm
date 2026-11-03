# ADR 0077: Console output modes for people and agents (M17)

- **Status:** Accepted (not built yet: plan M17); supersedes the "no option suppresses it" rule of [ADR 0008](0008-report-on-every-conversion.md) and the report line format of [ADR 0028](0028-console-report-sections-1-2.md)
- **Date:** 2026-11-02

## Context

The converter prints Serilog lines (`INF …`, `WRN …`) and nothing about where it is in the run. With the optimizer
(plan M9) a conversion may take minutes ([multi-pass.md](../multi-pass.md#time-and-quality)), so David wants to see
which step is running, how many are left and about how long it will take, drawn with progress bars
(Spectre.Console suggested). The converter is also driven by AI agents, which need output that doesn't depend on
a terminal, a way to opt out of widgets, and never a prompt that waits for input. Hard constraints: Native AOT with
zero warnings on three platforms, no reflection, no network.

## Decision

All decided by David on 2026-11-02 unless marked; the full design is [cli-output.md](../cli-output.md).

- **One switch, `--progress fancy|text|json|silent`, default `fancy`.** No `--json` / `--machine` / `--quiet`
  switches and **no auto mode**: the converter never picks a mode by itself.
- `fancy` is for people: Spectre.Console tables, a step bar and a total bar, colored notes, a timing table. When the
  output isn't a terminal it stays `fancy`, and Spectre itself drops animation and color and prints the static final
  frames.
- `text` is for agents: a new compact line format (keyword + `key=value`), approved from a sample. It replaces the
  `INF`/`WRN` lines; golden report tests are regenerated.
- `json` is for programs: every message (report, progress, notes, timings, result, errors) as NDJSON, schema `v1`.
- `silent` prints **nothing**, errors and crashes included; only the exit code tells. This is the one mode in which
  the MOD metadata and sample table are not printed (supersedes ADR 0008's "no option suppresses it").
- The report tables are drawn as Spectre tables in `fancy` (not just the progress bar).
- **Never interactive**, in any mode: no prompts or menus; a missing argument exits 1 with the argument named and
  the usage line (a JSON error object in `json`). `--progress` is pre-scanned so parse errors come out in the chosen
  mode.
- **Parsing stays on System.CommandLine.** Spectre.Console.Cli (`CommandApp`) is ruled out: it is marked not
  AOT-compatible (reflection over settings types). Only the core **Spectre.Console** package is used for drawing,
  and only if a spike (T17.1) shows an AOT publish with zero warnings on win-x64, linux-x64 and osx-arm64. If it
  fails, the next AOT-clean progress package is tried (SpProgress, ShellProgressBar, Chuckie.ConsoleProgressBar —
  none documents AOT support), and only then a small renderer of our own (David's order).
- **Time estimates count work and remember past runs:** the current step from its work done, later steps from
  seconds-per-unit history kept in `timings.json` at the root of the candidate cache folder; `--no-cache` turns the
  history off.
- The **harness** gets the same modes.
- *(Proposed by Claude; David had no preference)*: streams per mode as in
  [cli-output.md](../cli-output.md#streams) (`json` all on stdout; `fancy` all on stdout until the error), and the
  timing table printed at the end of every `fancy` and `text` run.

## Options considered

- **`--progress` with four modes** (chosen).
- `--json`, `--machine`, `--quiet` as separate switches: three switches that can contradict each other; David
  preferred one.
- An auto mode that picks `text` when output is redirected: rejected by David; the caller says what it wants, and
  Spectre already behaves well without a terminal.
- **Spectre.Console.Cli `CommandApp`** for clean validation messages: not AOT-compatible, and System.CommandLine
  already gives exit 1 with a message; the agent-friendly part (naming the missing argument, JSON errors) is ours to
  add either way.
- Keeping `INF`/`WRN` lines as the agent format: rejected by David for a compact format with stable keywords.
- `--progress silent` still printing errors: rejected by David; silent means silent.
- ETA from fixed weights per step: wrong as soon as a song is unusual; from counting only: no total estimate until a
  step starts.

## Consequences

- New `src/Output/` layer (reporters, `TerminalProbe`, `PhaseTracker`, `EtaEstimator`, `ReporterSink`); the Serilog
  console sink leaves the converter. Stages gain an optional `IWorkCounter`.
- Report code (`ModReport`, `DmfReport`, `SampleAnalysisReport`, `LockedPairReport`) produces typed rows instead of
  text lines.
- New dependency (after T17.1): Spectre.Console (MIT) in `Directory.Packages.props` and `THIRD-PARTY-NOTICES.md`;
  test-only Spectre.Console.Testing.
- [cli.md](../cli.md), AGENTS.md's required behavior #1 and the golden report tests change when M17 lands.
- The optimizer (M9) reports its passes as phases through the same layer.
- Plan tasks T17.1–T17.8 in [implementation-plan.md](../implementation-plan.md#m17--console-output-for-people-and-agents).
