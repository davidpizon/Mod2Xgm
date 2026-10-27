# Decision log

Newest first. Each entry: date, decision, options considered, reason.

## 2026-10-27 — Nightly audit files issues for Qodana and NuGet findings
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

## 2026-10-27 — Qodana code scanning
- `.github/workflows/code_quality.yml` replicates the TotallyHot-ArcRouter Qodana workflow: Qodana Community for
  .NET in Docker, SARIF paths normalized to repository files, uploaded to GitHub code scanning (alongside CodeQL).
- No baseline yet (new project); add `.qodana/qodana.sarif.json` from a `main` run when findings accumulate.
- `QODANA_TOKEN` is optional for the Community linter.

## 2026-10-27 — --require-fm / --require-pcm
- Two new sample-list options pin a sample's rendering: `--require-fm` (always FM, never PCM/PSG) and
  `--require-pcm` (always digitized PCM, never FM/PSG). Hard constraints for the mapper and optimizer.
- A sample in both lists is a usage error (exit 1). So is a sample that is both ignored and required.
- Required PCM that overlaps beyond the PCM voices: the least important note is cut (noted), never moved to FM.
  Required PCM that can't fit the slots/bytes after PCM-preserving consolidation: exit 6.

## 2026-10-27 — MOD reader (T2.1, T2.2)
- `ModModule` is a faithful image of the file (no interpretation); sample lengths and loops in bytes.
- Pattern count: choose between "all 128 order entries" and "played orders" by which matches the file size.
- Truncated sample data and bad loops are repaired and reported as conversion notes; structural damage is exit 3.
- Inputs over 64 MB are rejected as not a MOD; unreadable files exit 2.
- 15-sample Soundtracker files are still rejected until T2.3.

## 2026-10-27 — Logging setup (T1.3)
- A `LoggingLevelSwitch` controls the console logger; `--verbose` raises it to Debug.
- `ConversionNotes` logs each note at Warning immediately and keeps the rendered text (string values unquoted)
  for the summary. Stages take the logger and notes as parameters for testability.

## 2026-10-27 — Exit codes and output checks (T1.2)
- Expected failures throw `ConversionException` carrying the exit code; `Program.Run` prints the message only.
- The output path never equals the input (exit 4 even with `--force`); output directories aren't created.

## 2026-10-27 — CLI parsing (T1.1)
- **System.CommandLine 2.0.13** added (MIT). Options are declared as text and converted/validated in
  `OptionsBinder`, which collects all errors; parse and validation errors exit 1.
- The library's default exception handler is off, so unexpected exceptions reach `Program.Run` and exit 5.
- `--reserve-sfx-channels` must be 0 for DMF.

## 2026-10-27 — FM fitting approach from the literature
- FM patch fitting (T8.2) uses a **hybrid search**: per-algorithm structure search (GA or pruned exhaustive over
  MULT/DT), then local search over levels and envelopes against the time-varying harmonic spectrum, selected
  by rendered audio. Based on Horner et al. 1993, Sound2Synth 2022 and DDX7 2022
  ([references/fm-matching.md](references/fm-matching.md)).
- MFCC distance will be tracked next to the log-mel distance in `src/Scoring`.
- No neural parameter estimator inside the converter for now.
- Papers are **not** copied into this public repository (copyright); citations and summaries only. Local copies
  go in the git-ignored `docs/references/papers/`.

## 2026-10-27 — Tests on Microsoft.Testing.Platform (PR #7)
- xunit.v3 4.x runs on Microsoft.Testing.Platform; the .NET 10 SDK's VSTest-based `dotnet test` rejects it
  ("Testing with VSTest target is no longer supported"). `global.json` now selects the MTP runner.
- Coverage moves from coverlet.collector (a VSTest data collector) to **Microsoft.Testing.Extensions.CodeCoverage**
  (Cobertura output; settings in `tests/coverage.runsettings`). Test-only, not shipped.

## 2026-10-27 — Code coverage is an agent requirement
- The GitHub **Code coverage** ruleset is disabled: `actions/upload-code-coverage` returned HTTP 404 (code
  coverage isn't available for this repository).
- Instead, **agents enforce it**: line coverage of `Mod2Xgm` ≥ 80 % and no drop > 10 points vs `main`, measured
  locally and stated in each PR description (AGENTS.md § Testing expectations).
- CI's `Code coverage` job reports the number (job summary, `Line coverage` annotation, artifact) and **fails
  below 80 %** (added the same day: "Require 80% code coverage"). The 10-point drop stays an agent check.

## 2026-10-27 — Project renamed Mod2Xgm
- Project, solution (`src/Mod2Xgm.slnx`), project file (`src/Mod2Xgm.csproj`), namespace and command name
  (`mod2xgm`) renamed from Mod2Dmf. GitHub repository: `davidpizon/Mod2Xgm`.
- Not renamed from here: the local folder `C:\git\Mod2Dmf` and the claude.ai Project name.

## 2026-10-27 — XGM2 extension
- `--format xgm2` writes **`.xgm2`** (was `.xgm` for both). XGM v1 keeps `.xgm`.
- The extension mismatch with SGDK (which expects `.xgm`) is **not a concern**; every XGM2 conversion prints
  `NOTE: SGDK expects XGM2 files to use the .XGM file extension.`
- Still open (unrelated to extension): rescomp documents only VGM input for `XGM2`; confirm SGDK can ingest
  our XGM2 data (T10.2).

## 2026-10-27 — XGM v1.01 spec saved
- SGDK `xgm.txt` (XGM v1.01) saved as `formats/xgm.txt`; `formats/xgm.md` filled from it.
- Settled: 63 samples (4-byte SID entries, empty = `$FFFF`/`$0001`), **8-bit signed** PCM at ~14 kHz,
  256-byte aligned address and size, FM6 replaced by the driver (music gets FM1–5), single-frame `$00` wait.
- CH3 special mode for XGM: expressible, but enabled only after a probe ROM confirms (register `$27` also
  controls the YM timers).

## 2026-10-27 — Multi-pass optimizer, SGDK compliance, pure C# rendering
- **Multi-pass optimizer adopted**, built after the first DMF release (simple mapper first). It fits FM/PSG/PCM
  candidates per instrument, allocates within target limits, renders in-app and refines. Decides by measured
  error and cost, not a fixed FM-first rule.
- **In-app rendering and scoring are pure C#:** own MOD renderer (validated vs openmpt123), C# port of ymfm
  (BSD-3) for YM2612, C# SN76489, PCM driver model. No native libraries for these.
- **Conversion time:** minutes are acceptable by default. `--quality fast|normal|best` (default normal, 5-min
  budget), `--time-budget`, `--seed`, fitted-patch cache (`--no-cache`).
- **SGDK compliance is the standard:** every output must play through SGDK; DMF must survive DefleMask VGM
  export → rescomp.
- **CH3 special mode** allowed for **XGM2** (spec'd in xgm2.txt) and **DMF** (system 0x42); not XGM v1 until
  xgm.txt confirms.
- **`--reserve-sfx-channels N`**, default 0 (music may use every PCM channel).

## 2026-10-27 — Libraries yes, external applications no
- External **libraries** (NuGet packages, native C/C++ libraries via P/Invoke) may be used in the converter,
  provided they work under Native AOT on win-x64, linux-x64, osx-arm64 and are permissively licensed
  (LGPL only as a dynamically loaded native library; GPL needs approval).
- External **applications** are used only for testing (tests, accuracy harness) — never during conversion.
- Replaces the earlier "no third-party dependencies without asking / never shell out" rule.

## 2026-10-27 — Report on every conversion
- Whenever a MOD file is converted, the MOD metadata and full sample table are read and printed —
  mandatory, every output format, no flag to suppress, printed before mapping so they appear even if
  conversion fails. `--info` became a dry run (full report + notes, no file written).

## 2026-10-27 — Implementation plan inputs
- Build order: **DMF first**, then XGM2, then XGM v1 ([implementation-plan.md](implementation-plan.md)).
- DMF mapping: simple mapper first (busiest percussive channel → DAC, rest FM from a patch library), then
  auto-hybrid chosen by the accuracy harness.
- **System.CommandLine** approved for CLI parsing; **xUnit v3** for tests.
- CI: GitHub Actions on Windows, Linux, macOS; AOT artifacts per OS.
- DMF spec gaps closed with the DefleMask manual and Furnace's DMF loader (reference only, GPL — no code copied).
- Harness tools available locally: openmpt123/libopenmpt, DefleMask WAV export, SGDK + BlastEm/Genesis Plus GX.
- Code is written by Claude Code on David's PC, one plan task per PR.

## 2026-10-27 — PCM budget and mid-song swapping confirmed
- Default PCM byte budget for XGM/XGM2: **128 KB per song** (proposed 2 MB, then 256 KB — both too large
  when a ROM holds several songs). Rationale: 3.125 % of a 4 MB cart, ≈ 9.9 s of 13.3 kHz PCM; budget
  pressure is handled by consolidation (lower rates, dedupe, merges). Overridable with `--max-pcm-bytes`.
- **Mid-song sound swapping confirmed:** when slots run out, a track switches to a substitute sound (other
  sample or FM/PSG approximation) for a section of orders, never playing a wrong pitch.

## 2026-10-27 — Goal: most accurate sound; report; capacity
- **Goal:** the most accurate *sounding* rendition of the MOD. Sound accuracy outranks size, editability and
  simplicity. Channel mapping is judged by this (measured with the accuracy harness).
- **Console report on every run:** MOD metadata, all sample/instrument names (incl. empty slots), and the
  output instrument/sample table.
- **Capacity warning** (conversion note, `Warning`) when samples exceed XGM/XGM2 (or DMF) capacity.
- **Consolidate by default** (dedupe, trim, pitch quantize, merge, mid-song sound swaps, voice stealing);
  `--no-consolidate` fails with exit 6 instead.
- **`--ignore-samples`** lets the user leave samples out.
- Note: a MOD has ≤ 31 samples, so capacity is exceeded through pitch variants (XGM/XGM2 can't pitch PCM),
  simultaneous voices, or bytes — not raw sample count.
- XGM2 single-track sample limit corrected to **122** (was wrongly given as 248).

## 2026-10-27 — Conversion notes and logging
- Lossy-conversion diagnostics are called **conversion notes** ("warning" is reserved for build warnings,
  which are errors). They do **not** fail the conversion.
- Logging uses **Serilog** + **Serilog.Sinks.Console**, configured in code (AOT-safe). Conversion notes are
  logged at `Warning` level to the console (stderr).

## 2026-10-27 — DMF target version
- Write DMF **file version 24 (`0x18`)**, system `0x02` (Genesis, 10 channels), per `formats/DMF_SPECS.txt`
  (DefleMask v1.0.0+ spec, saved locally).

## 2026-10-27 — Every warning is an error
- Build: `TreatWarningsAsErrors`, `MSBuildTreatWarningsAsErrors`, `IsAotCompatible`, `AnalysisLevel=latest`,
  `EnforceCodeStyleInBuild` in `src/Mod2Xgm.csproj`. Suppressions need explicit user approval.

## 2026-10-27 — Initial scope
- **XGM output is written natively**, no external `xgmtool`/`xgm2tool`. Reason: single self-contained
  AOT exe, no toolchain dependency.
- **Both XGM and XGM2**, selected by `--format xgm|xgm2`. (Extension changed — see "XGM2 extension" below.)
- **MOD input v1:** 4-channel 31-sample ProTracker (`M.K.`, `M!K!`, `FLT4`, `4CHN`) and 15-sample
  Soundtracker. 6/8+ channel MODs rejected for now.
- **XGM2 reference:** SGDK `bin/xgm2.txt`, format version `0x10`.
- **Channel mapping:** **open** — see [channel-mapping.md](channel-mapping.md).
- The fileformat.info "DMF" page (corion) documents X-Tracker's DDMF format, **not** DefleMask — not used.

## Open
- Channel mapping strategy and DMF DAC collision policy.
- DMF details the spec leaves out: effect codes, DAC sample selection, custom Hz encoding, sample rate codes (see `formats/dmf.md`).
- XGM v1 driver behavior not covered by the spec: CH3 special via `$27` (timer bits), music PCM priority.
- CLI defaults for `--region` and `--mapping`.
