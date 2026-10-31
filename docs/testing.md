# Testing

Test projects live in **`tests/`**, one folder each (see [`tests/README.md`](../tests/README.md)). The main one is
`tests/Mod2Xgm.Tests` (**xUnit v3**); `tests/Mod2Xgm.Harness.Tests` tests the accuracy harness. Both are in the root
`Mod2Xgm.slnx`. Run with `dotnet test`.

## Layers
1. **Reader unit tests** — build MOD bytes in code (a small `ModBuilder` test helper) and assert
   every field: variant detection, 15-sample heuristics, bad tags, truncated files, loop in bytes vs words.
2. **Model tests** — period→note, finetune, Fxx speed/BPM split, Bxx/Dxx flow and loop detection.
3. **Writer unit tests** — exact bytes for headers, tables, alignment, endianness; DMF decompresses
   with `ZLibStream` and re-parses with the test-only `DmfReader` (`tests/Mod2Xgm.Tests/Dmf/`, plan T4.2). It reads
   file versions 24–27 (so DefleMask-saved probe files open too) and is strict: any value DMF or DefleMask doesn't
   allow throws, naming the field and byte offset. `DmfBuilder` builds DMF bytes in code for its own tests.
   The DMF probe files (plan T4.3, `tests/golden/dmf-probes/`) are goldens too: built by `DmfProbes`, compared
   decompressed (zlib output can differ by platform), each with a `.txt` description; DefleMask's "Save As" copies in
   `deflemask/` must describe the same song (`DmfDescription`). The effect probes (plan T5.5,
   `tests/golden/dmf-effect-probes/`) are MODs built by `DmfEffectProbes` and converted by the real pipeline
   (`DmfConversion`); the MOD, the DMF and a description with the conversion notes are goldens, and the WAV renders
   (openmpt123, Furnace, DefleMask) are remade by hand as their README says.
4. **Golden-file tests** — whole conversions of generated MODs compared to checked-in expected outputs
   under `tests/golden/`. Updating goldens is a deliberate act (`UPDATE_GOLDEN=1 dotnet test`). Tests compare
   through `tests/Mod2Xgm.Tests/Golden.cs`, which finds the folder from the repo root and ignores CR/LF
   differences (Windows checkouts get CRLF). First goldens: console report sections 1–2 (`tests/golden/report/`).
   The YM3438 probes (plan T7.2, `tests/golden/ym3438/`, [ADR 0056](adr/0056-ym3438-model.md)) are register-write
   scripts built by `Ym3438Probes`; the port must play each one exactly as ymfm does and within a log-mel bar of
   Nuked-OPN2. Their reference outputs are made by `tools/ym3438-reference/run.sh`, as their README says.
5. **CLI tests** — argument parsing (incl. `--ignore-samples` lists, ranges, hex), output naming, exit
   codes, no overwrite without `--force`, console report content (metadata, empty-slot names, statuses).

## Code coverage

Requirement: **≥ 80 % line coverage** of the `Mod2Xgm` assembly and **no more than a 10-point drop** versus
`main` on every pull request. **CI enforces the 80 %** (the `Code coverage` job fails below it); the agent
also measures it before opening a PR, checks the drop versus `main`, and states the number in the PR
description (AGENTS.md § Testing expectations). It is not a GitHub ruleset.

Tests run on **Microsoft.Testing.Platform** (MTP), selected in the root `global.json` (`"test": { "runner":
"Microsoft.Testing.Platform" }`). xunit.v3 4.x requires it: with the .NET 10 SDK, the old VSTest-based
`dotnet test` refuses MTP test projects.

Coverage comes from **Microsoft.Testing.Extensions.CodeCoverage** (Cobertura XML), configured by
`tests/coverage.runsettings` (measures `Mod2Xgm.dll` only). The `Code coverage` job in
`.github/workflows/ci.yml` runs on pull requests and pushes to `main`. It writes the line coverage to the job
summary and a `Line coverage` annotation, **fails below 80 %** (`MIN_LINE_COVERAGE`), and keeps the report as
the `coverage-cobertura` artifact.

Coverage is measured on `tests/Mod2Xgm.Tests` only: it is the project that tests the converter, and with every test
project in one run the reports of both would land in the same folder and CI could pick the wrong one
([ADR 0047](adr/0047-accuracy-harness.md)). Measure locally (from the repository root):

```
dotnet test --project tests/Mod2Xgm.Tests --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml --coverage-settings tests/coverage.runsettings --results-directory artifacts/coverage
```

Read the line coverage (the first `line-rate` attribute; 0.934 = 93.4 %):

```powershell
# PowerShell
$r = Get-ChildItem artifacts/coverage -Recurse -Filter coverage.cobertura.xml | Select-Object -First 1
'{0:P1}' -f [double]([xml](Get-Content $r.FullName)).coverage.'line-rate'
```

```bash
# bash
grep -o 'line-rate="[0-9.]*"' "$(find artifacts/coverage -name coverage.cobertura.xml | head -n 1)" | head -n 1
```

Only the `Mod2Xgm` assembly is measured. Don't exclude code from coverage to pass the gate — write the test,
or make the code testable (e.g. `Program.Run` takes the work as a delegate so the error path is covered).

## Accuracy harness (drives design decisions)

The goal is the most accurate **sounding** output, so it is measured, not guessed:

`tools/Mod2Xgm.Harness` does all of it on the corpus in one command; [harness.md](harness.md) says how to set it up and
run it ([ADR 0047](adr/0047-accuracy-harness.md)).

1. Render the MOD with a reference player: libopenmpt, OpenMPT's engine (what `openmpt123 --render` plays). The
   tick player's timeline is checked against libopenmpt's, row by row, to one tick ([harness.md](harness.md#song-timeline)).
2. Render the output: Furnace (automatic) and DefleMask WAV exports (by hand) for `.dmf`; emulator audio capture
   (BlastEm / Genesis Plus GX) of an SGDK test ROM for `.xgm` / `.xgm2` (plans M10/M11).
3. Align and compare with objective metrics — multi-resolution log-mel spectral distance and onset/timing
   error — plus a short A/B listening check.
   `src/Scoring` (`SongScorer.Compare`) does the comparing: it lines the two renderings up, then reports the log-mel
   distance in dB (0 = identical, 6 = 6 dB off everywhere), the MFCC (timbre) distance, note-start precision/recall/F1
   and timing error, and a combined number (lower is closer; since T6.4 the log-mel distance alone, which agrees only
   weakly with listening so far). Limits: mono only, one constant lag, both renderings at the same sample rate. Metric
   choices: [ADR 0042](adr/0042-scoring-metrics.md), calibration and baseline: [ADR 0049](adr/0049-simple-mapper-baseline.md).
   `BalanceMetrics` measures what a whole-mix score hardly sees: each instrument's loudness against the original's,
   from stems (each MOD sample alone on both sides; the harness's `--balance`,
   [ADR 0051](adr/0051-instrument-balance-metric.md)).
4. Keep a small corpus of freely licensed MODs with scores per strategy; a change to mapping or consolidation
   must not lower the corpus score without a recorded reason in an ADR ([adr/](adr/README.md)).

Unit tests for the capacity logic: build a song in code that needs more slots / voices / bytes than the
target allows and assert the capacity warning, the consolidation steps taken, and exit 6 with `--no-consolidate`.

External applications (openmpt123, DefleMask, SGDK, BlastEm, Genesis Plus GX) are allowed **here and in
`tools/` only** — the converter itself never launches another program.

## Manual / oracle checks (not CI)
- Open `.dmf` in DefleMask; play it. Export VGM from DefleMask and compare register activity when useful.
- Build an SGDK test ROM that plays the `.xgm` / `.xgm2`; run in BlastEm / Genesis Plus GX.
- Compare MOD playback reference with OpenMPT or libxmp.

## Test data rules
- **Do not commit copyrighted MOD files.** Generate modules in code, or use files with an explicit
  free license and record the license next to the file in `tests/data/LICENSES.md`.
- Keep fixtures small (< 100 KB each). The corpus (`tests/data/corpus/`, [ADR 0046](adr/0046-mod-corpus.md)) is the
  exception: real songs, Public Domain or CC0 only, each listed with its checksum (`CorpusTests` checks it).
