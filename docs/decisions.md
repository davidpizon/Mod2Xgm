# Decision log

Newest first. Each entry: date, decision, options considered, reason.

## 2026-10-27 — Tick player (T3.4)
- **A port of pt2-clone's ProTracker 2.3 replayer** (`src/pt2_replayer.c`, BSD-3-Clause), quirks and bugs included
  (David, over writing it from the docs or "simple first, exact later").
- **It reads the MOD's raw cells** (David, over playing the `Song`): the port works on the same numbers ProTracker did.
  It lives in `src/Mod`; the `Song` keeps feeding the DMF writer.
- **Output: the full picture of every channel on every tick** (David, over a list of changes): `Playback` in `src/Model`
  — pitch, volume, pan, the sample part that starts and the part that repeats (Paula's registers), plus the LED filter.
- **Once through, with the loop point marked** (David, over twice through or a fixed length). The end follows
  pt2-clone's MOD2WAV row-visit rule; `F00` ends without a loop.
- **Checked with hand-worked test songs now; the openmpt123 corpus comparison moves to the harness (T6.3)** (David):
  the corpus doesn't exist before M6.
- Decided while implementing: ProTracker's exact CIA tick rate (`709 379 / (1 773 447 / BPM + 1)`), BPM changes one
  tick late; NTSC input scales it by 6/5 (David's 60 ticks/s rule). Ultimate Soundtracker cells are turned into
  ProTracker commands as OpenMPT does and played by the same engine at speed 6 and the byte-471 tempo, with samples
  starting at their loop start (matching `ModToSong`). Not reproduced, with a note: `EFx` invert loop (it rewrites
  sample data while playing). Not emulated: `E8x` Karplus-Strong (off by default in pt2-clone too). Empty sample slots
  play silence (ProTracker would play whatever memory follows). Fitting ticks onto Genesis frames (`--output-region`)
  stays with the writers. `Song.LoopOrder` stays `null`: a loop can start mid-pattern, so the writers take the loop
  point from `Playback`.

## 2026-10-27 — `--player` (T3.3a)
- **One option picks whose rules the converter follows**, from the reader on (David, replacing T3.3's "option later"):
  `--player protracker|ust`. OpenMPT was chosen too, then left for a later task (T3.3b) once its rules turned out to be
  mostly per-file guesses (David). Name `--player` (David, over `--mod-player` and `--tracker`).
- **Default by format**: `ust` for 15-sample files, `protracker` for 31-sample files (David, over `protracker` for all
  and `openmpt`). **Every run prints the player and why** (David): an `Information` line right after report section 1.
- **Ultimate Soundtracker rules** follow OpenMPT's Soundtracker loader (`soundlib/Load_stk.cpp`, BSD-3-Clause, read
  only): loop starts in bytes; looped samples start at their loop start; byte 471 is the tempo (OpenMPT's timer
  formula, 0 and 0x78 → 125 BPM); no finetune; `1xy` arpeggio, `0xy` arpeggio from 03 up, `2xy` pitch bend (y up, else
  x down); `3xx`–`Fxx` ignored with one note, `Cxx` included. Under ProTracker rules a 15-sample file keeps T2.3's
  words-or-bytes loop guess.
- Decided while implementing: `Timing.Bpm` is a `double` (the tempo byte gives fractional BPM such as 121.14); the
  report's tempo line prints up to two decimals. The sample-usage scan drops effects 3–F under Ultimate Soundtracker
  rules. A tempo byte of 240 or more has no meaning in the formula: 125 BPM with a note. `ModToSong` takes the player
  from `ModModule.Player` instead of a parameter.

## 2026-10-27 — MOD → song, effect decoding (T3.3)
- **Scope: the whole `ModToSong` step** (David, over only an effect decoder): notes, instruments, samples and every
  cell's decoded effect. Order traversal, the loop point and timing stay in the tick player (T3.4), so `LoopOrder`
  is `null` and timing starts at speed 6 / 125 BPM.
- **ProTracker 2 decides** where players disagree (David, over OpenMPT or asking per case), e.g. zero parameters with
  no memory decode to no effect, and wave 3 ("random") plays square. **Rules are a setting** (`ModPlayer`, only
  `ProTracker2`); a command-line option is added when a real song needs another rule set (David, over adding
  `--mod-player` now or one switch per difference).
- **15-sample effects 1 and 2** are read as slides by default, with a conversion note; an Ultimate Soundtracker rule
  set is a future `ModPlayer` value (David: same approach as the rules setting; no guessing).
- Decided while implementing: `ModToSong` lives in `src/Mod`, not `src/Model` as architecture.md drew it, because the
  model may not reference MOD types. `8xx` (ignored by ProTracker 2) and `E8x` (unused) become `UnsupportedEffect`
  with a note; `Cxx` above 64 plays at 64; `Dxx` follows ProTracker's x × 10 + y and breaks to row 0 above row 63; a
  cell naming a sample above the module's count names no sample. Vibrato/tremolo depths are the table peak (255)
  times the depth, scaled as ProTracker does (period ÷ 128, volume ÷ 64). Each decoding change is one note per kind
  with a count and the first location. `ModToSong` isn't called by the conversion command yet; its first caller is
  the tick player or the DMF writer.

## 2026-10-27 — Amiga pitch: period → note + cents, finetune, PAL/NTSC clock (T3.2)
- **Built standalone** (`src/Model/AmigaPitch.cs`); `ModToSong` and the tick player call it in T3.3–T3.4 (David).
- **Two regions replace `--region`** (David): `--input-region` (the Amiga the MOD was made on; sets the Paula clock)
  and `--output-region` (the Genesis that plays the result), both `pal|ntsc`, both **default `pal`** (David, after
  first choosing NTSC: most MODs are PAL and openmpt123 assumes PAL). Options considered: always PAL with no option;
  the clock following one `--region`; a separate `--amiga-clock`. `--region` is **removed**, not aliased: nothing has
  been released (David).
- **The regions set tempo too** (David): `--input-region` is the source's real pitch **and** speed (50 ticks/s on a
  PAL Amiga, 60 on NTSC, at BPM 125; other BPMs scale the same way); `--output-region` is the console. Matching
  regions play one tick per frame; mismatched ones are adjusted so the output keeps the source's speed and pitch.
  Options considered: NTSC output always one tick per frame (20 % faster for PAL songs; David's first answer,
  replaced by this one); `--input-region` changing pitch only. Built in the tick player (T3.4) and the writers.
- **Finetune uses ProTracker 2's exact tables and lookup** (David, over the formula `period × 2^(−ft/96)`): matches
  what composers heard and what openmpt123 plays. Tables taken from pt2-clone `src/pt2_tables.c` (BSD-3-Clause,
  commit 554bf79), checked against the expected pitches by a test. Not Furnace (GPL).
- **Finetune is applied by the tick player** (David): cell notes stay as written (finetune 0) because a cell often
  doesn't name its instrument; `AmigaPitch.PeriodOf` recovers the period from a note's playback rate.
- Decided while implementing: clocks are the crystals ÷ 8 (3 546 895 / 3 579 545 Hz) instead of the doc's
  7 093 789.2 / 7 159 090.5 ÷ 2 (0.2 ppm apart); period 428 on PAL is exactly C-2 at 0 cents, so pitch names don't
  move with the clock but cents do (NTSC +15.9). ProTracker 2 plays period 0 for written periods below 113 (its
  tables' zero padding); Mod2Xgm keeps the written period and the caller adds a conversion note.

## 2026-10-27 — Song model (IR) types (T3.1)
- **Scope:** types only; `ModToSong` fills them in T3.2–T3.4 (David, over "also copy the easy parts now").
- **Sample sound:** `float[]` −1…+1 (David, over `short[]` and keeping `sbyte[]`): resampling and scoring work on
  it directly, and 8-bit input converts back exactly.
- **Pitch:** both a musical pitch (semitone + unrounded cents) and the exact playback rate in Hz (David, over either
  alone): writers place notes, renderers (T7.1) need period-exact pitch.
- **Effects:** one named record per kind (David, over a generic code + numbers), and the full ProTracker list now
  (David, over adding kinds in T3.3), so T3.3 may still adjust parameters. Units are neutral (volume 0–1, pan
  −1…+1, cents, ticks, rows); slides name their scale (`AmigaPeriod` or `Cents`) because an Amiga period slide can't
  be expressed exactly in cents; `null` parameters mean effect memory, resolved by the tick player (T3.4).
- Decided while implementing: 0-based numbering inside the IR; MOD `Cxx` becomes the cell's `Volume`, not an effect;
  the IR keeps every sample, ignored ones included (the budgeter applies `--ignore-samples`); the Genesis region
  stays an output option instead of a song field (architecture.md had listed it under `Timing`); no per-sample
  "source rate", since each note carries its exact rate. Invalid references throw `ArgumentException` (a reader bug,
  exit 5).
- "No MOD types leak" is enforced by a test that scans `src/Model` sources and checks every model member signature.

## 2026-10-27 — Console report sections 1–2 (T2.5)
- **Tempo line** reads the `Fxx` commands on the first row the song plays; defaults (speed 6, 125 BPM) fill in what
  that row doesn't set (David's choice over always printing the defaults or leaving the line out until M3).
- **Ignored samples** show `ignored` in the table now, and sample numbers the module doesn't have (16–31 in a
  15-sample file) are rejected with exit 1 after the report is printed (David). The check also covers
  `--require-fm` / `--require-pcm`, since cli.md gives them the same range rules.
- **No digit grouping** anywhere in the report (`182344`, not `182 344`) (David); cli.md's examples were updated.
- The doc's example table wasn't evenly aligned; the code uses one fixed column layout and the doc now shows its
  real output. Each line is logged as a property (`{ReportLine:l}`), so names with `{`/`}` print as stored.
- 15-sample files: the restart line notes that Ultimate Soundtracker reads that byte as the tempo; M3 decides.
- Golden files live in `tests/golden/`; with no NuGet access in the agent session they were generated from the
  report code itself and are checked by CI.

## 2026-10-27 — Sample usage scan (T2.4)
- A note counts when it really starts (period present, the channel's current sample); slides (`3xx`/`5xy`) and notes
  delayed past their row (`EDx`, x ≥ speed) don't; retrigger counts once. (David)
- The scan follows the song's flow now (`Bxx`, `Dxx`, `E6x`, `F00`) rather than counting every stored row (David's
  choice over the "count everything" option).
- Loops: once through; a jump back to played rows is walked one more time (David). A song that just ends is walked
  once (David).
- Details in [sample-capacity.md](sample-capacity.md#usage-scan-srcmodsampleusagecs-plan-t24).

## 2026-10-27 — XML documentation comments required
- David's rule: every class (record, struct, interface, enum) and every public method in `src/` and `tests/` has an
  XML doc comment, including internal types and test methods. Recorded in AGENTS.md (Code conventions).
- Not compiler-enforced yet: `CS1591` only covers publicly visible members, and the app's types are internal.

## 2026-10-27 — 15-sample Soundtracker detection (T2.3)
- Untagged files are read as 15-sample Soundtracker modules with **relaxed** checks (David's choice): reject only
  what can't be read (header under 600 bytes, song length outside 1–128, a played order ≥ 128, patterns cut off),
  and report **every sign of junk** as a conversion note. Rejections give the bytes found at offset 1080 and the
  reason the 15-sample reading failed.
- Standing rule from David: always log assumptions and potential bad conditions. The reader notes the 15-sample
  assumption, the ambiguous byte 471, ambiguous loop starts, and header/pattern anomalies in both layouts.
- Loop starts stored in bytes: detected only when the words reading can't fit; a conversion note every time.
- Other 31-sample tags (`N.T.`, `M&K!`, `FEST`, `LARD`, `NSMS`) stay rejected for now, with the tag and tracker named.
- Ultimate vs. later Soundtracker is decided in M3, not in the reader; the reader keeps the bytes as stored.

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

## 2026-10-27 — Weekly Dependabot scan
- `.github/dependabot.yml`: weekly (Mondays 06:00 Pacific) version updates for NuGet (central
  `Directory.Packages.props`) and GitHub Actions; test tooling, Serilog and actions grouped into single PRs.
- Security alerts and security updates are repository settings (Settings → Code security), enabled by David.

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
- 15-sample Soundtracker files were rejected until T2.3 (see above).

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
- CLI default for `--mapping`.
