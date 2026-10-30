# Architecture Decision Records

Every design decision in Mod2Xgm is an ADR: one numbered file here, in the order decisions were made
([ADR 0037](0037-decisions-are-adrs.md)).

## Adding one

1. Copy [template.md](template.md) to `NNNN-short-title.md` with the next number.
2. Fill in Context, Decision (say who decided, e.g. "(David)"), Options considered and Consequences.
3. Add a row to the index below, in the same pull request as the change it records.
4. To change a decision, write a new ADR that supersedes it, and update only the **Status** line of the old one.

## Index

| ADR | Title | Date | Status |
|---|---|---|---|
| [0001](0001-initial-scope.md) | Initial scope | 2026-10-27 | Accepted; the output extension for XGM2 is superseded by [ADR 0012](0012-xgm2-extension.md) |
| [0002](0002-every-warning-is-an-error.md) | Every warning is an error | 2026-10-27 | Accepted |
| [0003](0003-dmf-target-version.md) | DMF target version | 2026-10-27 | Accepted |
| [0004](0004-conversion-notes-and-logging.md) | Conversion notes and logging | 2026-10-27 | Accepted |
| [0005](0005-goal-accurate-sound-report-capacity.md) | Goal: most accurate sound; report; capacity | 2026-10-27 | Accepted |
| [0006](0006-pcm-budget-and-mid-song-swapping.md) | PCM budget and mid-song swapping confirmed | 2026-10-27 | Accepted |
| [0007](0007-implementation-plan-inputs.md) | Implementation plan inputs | 2026-10-27 | Accepted |
| [0008](0008-report-on-every-conversion.md) | Report on every conversion | 2026-10-27 | Accepted |
| [0009](0009-libraries-yes-external-applications-no.md) | Libraries yes, external applications no | 2026-10-27 | Accepted; replaces the earlier "no third-party dependencies without asking / never shell out" rule |
| [0010](0010-multi-pass-optimizer-sgdk-compliance-pure-csharp.md) | Multi-pass optimizer, SGDK compliance, pure C# rendering | 2026-10-27 | Accepted |
| [0011](0011-xgm-v1-spec-saved.md) | XGM v1.01 spec saved | 2026-10-27 | Accepted |
| [0012](0012-xgm2-extension.md) | XGM2 extension | 2026-10-27 | Accepted; supersedes the extension part of [ADR 0001](0001-initial-scope.md) |
| [0013](0013-project-renamed-mod2xgm.md) | Project renamed Mod2Xgm | 2026-10-27 | Accepted |
| [0014](0014-code-coverage-is-an-agent-requirement.md) | Code coverage is an agent requirement | 2026-10-27 | Accepted |
| [0015](0015-tests-on-microsoft-testing-platform.md) | Tests on Microsoft.Testing.Platform (PR #7) | 2026-10-27 | Accepted |
| [0016](0016-fm-fitting-approach.md) | FM fitting approach from the literature | 2026-10-27 | Accepted |
| [0017](0017-cli-parsing.md) | CLI parsing (T1.1) | 2026-10-27 | Accepted |
| [0018](0018-exit-codes-and-output-checks.md) | Exit codes and output checks (T1.2) | 2026-10-27 | Accepted |
| [0019](0019-logging-setup.md) | Logging setup (T1.3) | 2026-10-27 | Accepted |
| [0020](0020-mod-reader.md) | MOD reader (T2.1, T2.2) | 2026-10-27 | Accepted; the 15-sample rejection is superseded by [ADR 0025](0025-fifteen-sample-soundtracker-detection.md) |
| [0021](0021-require-fm-require-pcm.md) | --require-fm / --require-pcm | 2026-10-27 | Superseded by [ADR 0041](0041-require-dac-require-psg.md) |
| [0022](0022-weekly-dependabot-scan.md) | Weekly Dependabot scan | 2026-10-27 | Accepted |
| [0023](0023-qodana-code-scanning.md) | Qodana code scanning | 2026-10-27 | Accepted; the baseline plan continues in [ADR 0024](0024-nightly-audit.md) |
| [0024](0024-nightly-audit.md) | Nightly audit files issues for Qodana and NuGet findings | 2026-10-27 | Accepted |
| [0025](0025-fifteen-sample-soundtracker-detection.md) | 15-sample Soundtracker detection (T2.3) | 2026-10-27 | Accepted; supersedes the 15-sample rejection in [ADR 0020](0020-mod-reader.md) |
| [0026](0026-xml-documentation-comments-required.md) | XML documentation comments required | 2026-10-27 | Accepted |
| [0027](0027-sample-usage-scan.md) | Sample usage scan (T2.4) | 2026-10-27 | Accepted |
| [0028](0028-console-report-sections-1-2.md) | Console report sections 1–2 (T2.5) | 2026-10-27 | Accepted |
| [0029](0029-song-model-types.md) | Song model (IR) types (T3.1) | 2026-10-27 | Accepted |
| [0030](0030-amiga-pitch-regions-finetune.md) | Amiga pitch: period → note + cents, finetune, PAL/NTSC clock (T3.2) | 2026-10-27 | Accepted |
| [0031](0031-mod-to-song-effect-decoding.md) | MOD → song, effect decoding (T3.3) | 2026-10-27 | Accepted; the "option later" for player rules and the 15-sample effect rule are superseded by [ADR 0032](0032-player-option.md) |
| [0032](0032-player-option.md) | `--player` (T3.3a) | 2026-10-27 | Accepted; supersedes parts of [ADR 0031](0031-mod-to-song-effect-decoding.md) |
| [0033](0033-tick-player.md) | Tick player (T3.4) | 2026-10-27 | Accepted |
| [0034](0034-dmf-spec-gaps-closed.md) | DMF spec gaps closed (T4.1) | 2026-10-28 | Accepted; confirmed in DefleMask by [ADR 0036](0036-dmf-probe-files.md) |
| [0035](0035-test-only-dmf-reader.md) | Test-only DMF reader (T4.2) | 2026-10-28 | Accepted |
| [0036](0036-dmf-probe-files.md) | DMF probe files (T4.3) | 2026-10-28 | Accepted |
| [0037](0037-decisions-are-adrs.md) | Decisions are recorded as ADRs | 2026-10-28 | Accepted |
| [0038](0038-sample-processor.md) | Sample processor and `--resampler` (T5.1) | 2026-10-28 | Accepted |
| [0039](0039-dmf-writer.md) | DMF writer and its song model (T5.2) | 2026-10-28 | Accepted |
| [0040](0040-simple-mapper.md) | Simple mapper, `--no-dac` and the Genesis plan (T5.3) | 2026-10-28 | Accepted |
| [0041](0041-require-dac-require-psg.md) | `--require-pcm` becomes `--require-dac`; add `--require-psg` | 2026-10-28 | Accepted; supersedes [ADR 0021](0021-require-fm-require-pcm.md) |
| [0042](0042-scoring-metrics.md) | Scoring metrics (T6.2) | 2026-10-28 | Accepted; the onset weight is superseded by [ADR 0049](0049-simple-mapper-baseline.md) |
| [0043](0043-dmf-timing.md) | DMF timing: tick rate, tick times and speed changes (T5.4) | 2026-10-28 | Accepted |
| [0044](0044-dmf-effects-and-song-grid.md) | DMF effects and song grid (T5.5) | 2026-10-28 | Accepted |
| [0045](0045-command-line-writes-dmf.md) | The command line writes the DMF (T5.6) | 2026-10-28 | Accepted |
| [0046](0046-mod-corpus.md) | The MOD corpus (T6.1) | 2026-10-29 | Accepted |
| [0047](0047-accuracy-harness.md) | The accuracy harness: libopenmpt reference, Furnace and DefleMask renders (T6.3) | 2026-10-29 | Accepted |
| [0048](0048-midi-input.md) | MIDI input (M13) | 2026-10-30 | Accepted |
| [0049](0049-simple-mapper-baseline.md) | Simple-mapper baseline and score calibration (T6.4) | 2026-10-30 | Accepted; changes the onset weight of [ADR 0042](0042-scoring-metrics.md) |
| [0050](0050-voice-levels.md) | Voice levels: FM and PSG noise as loud as their samples | 2026-10-30 | Accepted |
| [0051](0051-instrument-balance-metric.md) | Instrument balance metric: per-sample stems, checked against listening | 2026-10-30 | Accepted |

## Open questions

Not decided yet; each becomes an ADR when it is.

- Channel mapping strategy and DMF DAC collision policy.
- XGM v1 driver behavior not covered by the spec: CH3 special via `$27` (timer bits), music PCM priority.
- CLI default for `--mapping`.
