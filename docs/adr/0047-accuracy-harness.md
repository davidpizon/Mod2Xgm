# ADR 0047: The accuracy harness (T6.3)

- **Status:** Accepted
- **Date:** 2026-10-29

## Context

Plan T6.3 asks for `tools/Mod2Xgm.Harness`: reference renders of the MOD with OpenMPT, renderings of the conversion
(DefleMask WAV exports now, emulator captures later), a CSV and markdown report, and the tick player's song length
checked against OpenMPT's to ±1 tick (moved here from T3.4). Acceptance: stable scores on reruns. The scoring itself
is `src/Scoring` ([ADR 0042](0042-scoring-metrics.md)); the songs are the corpus ([ADR 0046](0046-mod-corpus.md)).

openmpt123 wasn't installed on David's PC. DefleMask can only export WAVs by hand. Furnace, which plays DMF, has a
command line.

## Decision

All by David, 2026-10-29:

- **libopenmpt, called directly**, is the reference player (OpenMPT's engine, BSD-3-Clause), instead of running
  `openmpt123`. The harness calls six functions of its C API through source-generated P/Invoke (`LibraryImport`), with
  libopenmpt's default render settings (what `openmpt123 --render` plays). This gives the exact song length from the
  engine (`openmpt_module_get_duration_seconds`) and puts the audio straight into memory. There is no official NuGet
  package; the one .NET wrapper found (LibOpenMPT.NET, MPL-2.0) is unused and wasn't taken. libopenmpt is installed
  on the PC, not shipped. It is looked up on the search path, then in `Program Files\libopenmpt\bin\<cpu>` (the Windows
  package's layout), or in the folder `--libopenmpt` names.
- **Both DMF renderers, side by side.** Furnace renders every fresh conversion automatically. DefleMask exports made by
  hand are scored too when present, and checked against the conversion (*stale* when the DMF they came from differs).
  The report says which renderer made each number.
- **The harness converts by itself**, in-process, through `ConversionCommand.Execute` with the default options, so
  what it measures is exactly what `mod2xgm` writes.
- **Only its unit tests run in CI.** The full corpus run needs libopenmpt and Furnace and takes minutes, so it runs on
  David's PC. Tests that need libopenmpt or Furnace skip themselves where those are missing.
- **Reports stay in `artifacts/harness/`** (git-ignored). The first committed baseline is plan T6.4.
- **A length more than one tick off:** a clear tick-player bug is fixed in the same PR with a test; a judgment call (one
  of OpenMPT's own rules) goes to David.
- **The timeline is compared OpenMPT's way, row by row** (David, after the first corpus run). Seven songs were more than
  a tick off by plain length. None was a tick-player bug; each came from OpenMPT timing ticks differently from a real
  Amiga. OpenMPT makes a tick a whole number of samples at 48 kHz, so it runs up to 0.06 % fast at most tempos except
  125. It applies a tempo change one tick sooner than ProTracker's CIA timer. And it plays past `F00`. So the harness
  times the tick player's own ticks with OpenMPT's two timing rules (`OpenMptTiming`) and checks that every row starts
  within one tick of libopenmpt's time for it, and the song ends within one tick of libopenmpt's length. A song that
  stops at `F00` is compared up to the stop. The real Amiga's difference is reported alongside, not as an error. The
  tick player keeps ProTracker's timing ([ADR 0033](0033-tick-player.md)). With this, all 19 corpus songs match
  libopenmpt to the microsecond on every row.
- Claude's choices within that:
  - Scoring at **44.1 kHz mono**: libopenmpt renders at that rate, and DefleMask and Furnace export at it. Other rates
    are resampled with the converter's smooth resampler.
  - **One tick** is the song's longest tick, so one tick gained or lost anywhere passes.
  - libopenmpt's song time is counted in whole samples at 48 kHz. That rate is read off its row times (937 samples
    per tick at BPM 128), not documented; the exact match on the corpus confirms it.
  - Songs are measured several at a time (`--jobs`, default half the processors, at most 4). The reports keep corpus
    order, so they don't change with it.
  - Code coverage stays measured on `Mod2Xgm.Tests` alone. With two test projects, the CI coverage job would otherwise
    pick whichever project's report it found first.

## Options considered

- **Run openmpt123**, read its WAV: no binding code, but the length only comes from the WAV, and a program is started
  per song. Kept as a fallback idea, not built.
- **Both** libopenmpt and openmpt123: more code for no gain once libopenmpt is installed.
- **A NuGet wrapper:** none official; LibOpenMPT.NET is MPL-2.0 with no users.
- **A pure C# MOD player from NuGet** (e.g. a MikMod port): not OpenMPT, which ADRs 0030 and 0038 name as the reference.
- **DefleMask only:** most trustworthy, but every run needs clicking. **Furnace only:** automatic, but Furnace isn't
  DefleMask. Both, labelled, keeps the speed and the check.
- **Score files made elsewhere** (no in-process conversion): an extra manual step and a chance to score an old DMF.
- **Full corpus run in CI:** needs libopenmpt and Furnace on runners and minutes per run, for a check that guides
  design rather than gates merges.
- **Commit this run's report:** T6.4 records the baseline deliberately; a report per PR would churn.
- For the timeline: **loosen to 1 tick or 0.1 %** (simple, but a real error under 0.1 % slips through); **keep the
  plain ±1 tick and list the seven songs** (every run fails); **time the tick player like OpenMPT** (against ADR 0033:
  the converter follows what composers heard on an Amiga).

## Consequences

- New projects: `tools/Mod2Xgm.Harness` (console app) and `tests/Mod2Xgm.Harness.Tests`, both in `Mod2Xgm.slnx`.
  `src/Mod2Xgm.csproj` gives both access to its internals.
- `.github/workflows/ci.yml`: the coverage step runs `tests/Mod2Xgm.Tests` only; `docs/testing.md` says the same.
- libopenmpt is listed in `THIRD-PARTY-NOTICES.md` under tools that aren't shipped. The converter still never loads it
  or starts another program.
- How to set up and run the harness: [docs/harness.md](../harness.md).
- Plan T6.4 runs the harness for the simple mapper's baseline and calibrates the combined score. T7.1 (the in-app MOD
  renderer) is checked against the same libopenmpt reference. M10/M11 add emulator captures as another renderer.
