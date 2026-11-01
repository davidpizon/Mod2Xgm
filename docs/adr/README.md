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
| [0030](0030-amiga-pitch-regions-finetune.md) | Amiga pitch: period → note + cents, finetune, PAL/NTSC clock (T3.2) | 2026-10-27 | Accepted; finetune parts superseded by [ADR 0060](0060-openmpt-pitch-and-loops.md) and [ADR 0061](0061-dmf-pitch-from-tick-player.md) |
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
| [0052](0052-pre-mixed-pcm.md) | Pre-mixed PCM for repeated overlaps (T8.5) | 2026-10-30 | Accepted |
| [0053](0053-r128-loudness-in-the-harness.md) | EBU R128 loudness in the harness (R128Net), checked against listening | 2026-10-30 | Proposed |
| [0054](0054-mod-renderer.md) | In-app MOD renderer: OpenMPT's clean sound, measured (T7.1) | 2026-10-30 | Accepted; known differences closed by [ADR 0060](0060-openmpt-pitch-and-loops.md) |
| [0055](0055-notes-beyond-protracker-range.md) | Notes beyond ProTracker's range play as in OpenMPT | 2026-10-30 | Accepted; amends [ADR 0033](0033-tick-player.md); parts superseded by [ADR 0060](0060-openmpt-pitch-and-loops.md) |
| [0056](0056-ym3438-model.md) | YM3438 model: a bit-exact ymfm port, checked against Nuked-OPN2 (T7.2) | 2026-10-30 | Accepted; known differences closed by [ADR 0064](0064-ym3438-chip-differences.md) |
| [0057](0057-sn76489-model.md) | SN76489 model: a bit-exact port of MAME's Sega PSG, checked against Nuked-PSG (T7.3) | 2026-10-30 | Accepted; known differences closed or recorded by [ADR 0065](0065-sn76489-chip-differences.md) |
| [0058](0058-pcm-driver-models.md) | PCM driver models: XGM, XGM2 and the DMF DAC as DAC register streams, from the drivers' source (T7.4) | 2026-10-30 | Accepted; checked against SGDK's drivers by [ADR 0066](0066-pcm-drivers-checked-against-sgdk.md) |
| [0059](0059-genesis-renderer.md) | Genesis renderer: the plan played on the chip models, with Furnace's levels (T7.5) | 2026-10-31 | Accepted |
| [0060](0060-openmpt-pitch-and-loops.md) | The tick player's pitch and loops follow OpenMPT, measured (T7.6) | 2026-10-31 | Accepted; amends 0033, supersedes parts of 0055; one consequence superseded by [ADR 0061](0061-dmf-pitch-from-tick-player.md); `jam_-_*` lead closed by [ADR 0062](0062-one-word-samples.md) |
| [0061](0061-dmf-pitch-from-tick-player.md) | The DMF's notes take their pitch from the tick player (T7.10) | 2026-10-31 | Accepted; supersedes parts of 0030 and 0060 |
| [0062](0062-one-word-samples.md) | A sample 1 word long is read as OpenMPT reads it: empty, its bytes left to the next sample | 2026-10-31 | Accepted; amends 0060 |
| [0063](0063-multichannel-mod-xm-it-input.md) | Multichannel MOD, XM and IT input (M14–M16): OpenMPT's playback rules ported, NNA emulated, sample options as for MOD | 2026-10-31 | Accepted |
| [0064](0064-ym3438-chip-differences.md) | The YM3438 model follows the chip where ymfm differs, as Nuked-OPN2 has it (T7.7) | 2026-10-31 | Accepted; closes the known differences of 0056 |
| [0065](0065-sn76489-chip-differences.md) | The SN76489 model's counters and noise follow the chip where MAME differs, as Nuked-PSG has them; MAME's volume levels and power-on latch kept (T7.8) | 2026-10-31 | Accepted; closes or records the known differences of 0057; volume levels superseded by [ADR 0067](0067-psg-volume-levels.md) |
| [0066](0066-pcm-drivers-checked-against-sgdk.md) | The PCM driver models follow SGDK's real XGM and XGM2 drivers, run on an emulated Z80 (T7.9) | 2026-10-31 | Accepted; closes the known differences of 0058 |
| [0067](0067-psg-volume-levels.md) | The PSG plays Nuked-PSG's volume levels, measured from its output, and the converter picks PSG volumes from them (T7.8) | 2026-10-31 | Accepted; supersedes decision 1 of 0065 |
| [0068](0068-sample-analysis.md) | Sample analysis: three scores, pitch stability, envelope, loop and a spectrum per pitch region (T8.1) | 2026-10-31 | Accepted |
| [0069](0069-fm-patch-fitting.md) | FM patch fitting: both structure searches, local search on the grid, an LFO twin per fit (T8.2) | 2026-10-31 | Accepted |

## Open questions

Not decided yet; each becomes an ADR when it is.

- Channel mapping strategy and DMF DAC collision policy.
- XGM v1 driver behavior not covered by the spec: CH3 special via `$27` (timer bits), music PCM priority.
- CLI default for `--mapping`.
