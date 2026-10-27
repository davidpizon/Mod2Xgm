# Mod2Xgm documentation

| Doc | What it covers |
|---|---|
| [implementation-plan.md](implementation-plan.md) | **Build order:** milestones M0–M12, tasks, acceptance criteria, risks |
| [architecture.md](architecture.md) | Conversion pipeline, layers, data model, project layout |
| [cli.md](cli.md) | Command-line contract: arguments, output naming, exit codes |
| [multi-pass.md](multi-pass.md) | Multi-pass optimizer: FM/PSG/PCM candidates, allocation, in-app render & refine |
| [sample-capacity.md](sample-capacity.md) | Capacity limits per target, capacity warning, sample consolidation, ignoring samples |
| [channel-mapping.md](channel-mapping.md) | Mapping options and the decision (simple mapper → multi-pass optimizer) |
| [formats/mod.md](formats/mod.md) | MOD input format (ProTracker 31-sample, Soundtracker 15-sample) |
| [formats/dmf.md](formats/dmf.md) | DefleMask DMF output (Genesis system) |
| [formats/DMF_SPECS.txt](formats/DMF_SPECS.txt) | Official DefleMask DMF spec (v1.0.0+), verbatim copy |
| [formats/xgm.md](formats/xgm.md) | SGDK XGM and XGM2 output |
| [formats/xgm.txt](formats/xgm.txt) | Official SGDK XGM v1.01 spec, verbatim copy |
| [genesis-audio.md](genesis-audio.md) | YM2612 / SN76489 facts the converter relies on |
| [testing.md](testing.md) | Test strategy and test data rules |
| [decisions.md](decisions.md) | Decision log |

## Goal

Produce the **most accurate *sounding*** rendition of the MOD on Genesis hardware. When trade-offs are
needed, sound accuracy wins over output size, editability in DefleMask, or implementation simplicity.
Accuracy is judged by listening and by the measurement harness in [testing.md](testing.md).

## Scope of v1

- **Input:** 4-channel MOD — ProTracker 31-sample (`M.K.`, `M!K!`, `FLT4`, `4CHN`) and original
  Soundtracker 15-sample (no tag). 6/8-channel MODs (`6CHN`, `8CHN`, `FLT8`, `CD81`, `xxCH`) are rejected
  with a clear message.
- **Console report:** MOD metadata, all sample/instrument names, and the output instrument/sample table
  are printed whenever a MOD file is converted — mandatory, every format (see [cli.md](cli.md#console-report)).
- **Capacity:** warns when a target can't hold the samples and consolidates by default
  ([sample-capacity.md](sample-capacity.md)); `--ignore-samples` lets the user drop samples.
- **Output:** `.dmf` (default), `.xgm` with `--format xgm`, or `.xgm2` with `--format xgm2`, written natively.
- **SGDK compliance:** every output must play through SGDK (XGM/XGM2 directly; DMF via DefleMask VGM export → rescomp).
- **Not in v1:** S3M/XM/IT input, VGM output, playback/preview, GUI.

## Glossary

- **Row / tick / speed / BPM** — tracker timing. A row lasts `speed` ticks; tick rate is `BPM × 2 / 5` Hz.
- **Order list / pattern matrix** — the sequence of patterns that makes up the song.
- **Period** — Amiga pitch value; lower period = higher pitch.
- **DAC** — YM2612 channel 6 in PCM mode; the Genesis's only hardware sample channel.
- **IR** — the format-neutral song model inside the converter.
