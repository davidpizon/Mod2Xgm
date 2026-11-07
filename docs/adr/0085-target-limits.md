# ADR 0085: One rule book of target limits; each `--format` has its own

- **Status:** Accepted
- **Date:** 2026-11-06

## Context

Plan T9.1 asks for a target limits model: what each output can hold in FM, CH3, PSG and PCM voices, slots and bytes,
with `--reserve-sfx-channels` and `--max-pcm-bytes`. The capacity check (T9.2) and the allocator (T9.3) read it.

The limits were scattered and the docs disagreed: DMF slots were 144 in `PcmTargets` and [sample-capacity.md](../sample-capacity.md) but 255
in [multi-pass.md](../multi-pass.md); XGM2 slots were 122 with a **verify** marker; DMF's FM was "6, 5 if DAC used"; XGM2's FM6 was a **verify**; and
[ADR 0078](0078-pre-mix-budget-cap.md) used 128 KB for DMF's pre-mixes although the docs give DMF no byte budget.

## Decision

`TargetLimits` (`src/Optimizer/TargetLimits.cs`) holds what one conversion's output can hold, chosen by `--format`:

| Limit | DMF | XGM | XGM2 |
|---|---|---|---|
| FM channels | 5 | 5 | 5 |
| CH3 special | yes | no, until the probe ROM (T11.1) | yes |
| PSG | 3 tone + noise | same | same |
| PCM voices | 1 | 4 − reserved | 3 − reserved |
| PCM slots | 144 | 63 | **123** |
| PCM bytes | **no limit** | `--max-pcm-bytes`, at most 16,776,960 | same |

- **Each `--format` uses its own limits (David).** XGM's for `--format xgm`, XGM2's for `--format xgm2`, DMF's for
  `--format dmf`, which is the default.
- **DMF has no byte limit (David),** only its 144 slots: the most sound. `--max-pcm-bytes` keeps its one job on DMF from ADR 0078, stopping huge
  pre-mixes being made, which that ADR's numbers need.
- **Music never plays on FM6, on any target (David).** XGM's and XGM2's drivers mix PCM there, and SGDK's `xgm2.txt` says
  "5FM + 3PCM + 4PSG" at best. A DMF keeps to the same, which costs nothing: the simple mapper already plays notes on FM1–5 only.
- **A budget too big for the format is cut, with a conversion note (David):** XGM and XGM2 store the sample block's size
  as size / 256 in 16 bits, so 65,535 × 256 = 16,776,960 bytes. Not a usage error: be relaxed about input, loud about doubts.
- **XGM2 holds 123 samples, not 122 (implementation; checked against SGDK's source, commit `9a78a44`).** The 124 entries of the file's table are ids 1–124
  (`xgm2.txt`: `sampleIdTable[(id-1)*2]`), so the reserved id 0 takes no entry there; only the end entry does. `XGM.java`:
  "single XGM supports a maximum of 124-1 = 123 samples". It prints a warning at exactly 123 but keeps them all.
- **`PcmTargets` stays the one place for each chip driver's hard numbers;** `TargetLimits` reads them, and the CLI's own copy of the
  PCM channel counts is gone.

## Options considered

- **A DMF keeps the limits of the format it will be exported to**, through a new `--dmf-export <xgm|xgm2>` (David asked for
  export first, then dropped it): `--format dmf` doesn't say where the DMF goes next, so it needed the option, and costs counted as SGDK's tools store them
  (14,000 / 13,300 Hz). More to learn and carry, for a choice most runs don't need.
- **Every DMF fits both exports:** 63 slots and no CH3. Fewer pitches and worse layered parts for every DMF.
- **DMF budget of 128 KB at 2 bytes a point** (ADR 0078's count): holds half the sound-time of XGM's, so more songs get cut down.
- **A too-big budget is a usage error:** stricter than the project's rule on input.
- **XGM2's FM6 when no PCM plays:** the format's `$FD` might allow it, but nobody has tried it, and a game's sound effect would cut the voice off.

## Consequences

- **A big DMF may not export cleanly.** It can pass XGM's 63 slots, XGM2's 123 or their byte budgets, or use CH3 where XGM can't yet, and SGDK's tools
  would then drop samples (`xgm2tool` keeps the first 123; `xgmtool` lists 63). AGENTS.md asks every `.dmf` to survive the export, so a DMF that fits
  the export still does; the checks that hold a bigger one to that are left for T9.2 and T10.3.
- `PcmFitter`'s room marks and the pre-mixer read the same per-target numbers, so XGM2's slot cap moved with them. Nothing else in a conversion changes
  until T9.2; `--verbose` logs the limits.
- [ADR 0078](0078-pre-mix-budget-cap.md)'s cap is unchanged. When a DMF has no budget, it's a speed guard rather than "the allocator could never fit it".
