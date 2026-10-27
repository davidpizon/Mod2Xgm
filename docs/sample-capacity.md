# Sample capacity and consolidation

> Status: **requirement** (2026-10-27); mid-song sound swapping confirmed by the user. Thresholds and algorithms are **proposed** and will be tuned with
> the listening/measurement harness in [testing.md](testing.md).

## Goal

The guiding rule for the whole app applies here: produce the **most accurate *sounding*** rendition of the
MOD. When the target can't hold everything, degrade in the order a listener notices **least**.

## Why capacity runs out even though a MOD has ≤ 31 samples

A MOD has at most 31 samples, below every target's raw sample-count limit. Capacity is still exceeded
because of how the Genesis targets play PCM:

1. **No pitch shifting on XGM/XGM2.** PCM plays at a fixed rate, so every distinct
   *(sample, pitch)* actually played needs its own pre-resampled copy (a **variant**). One melodic sample
   used over two octaves can need 24 variants on its own.
2. **Limited simultaneous PCM voices:** XGM 4, XGM2 3, DMF 1 (FM6 DAC). A 4-channel MOD that plays
   4 samples at once doesn't fit XGM2 or DMF.
3. **Byte budget.** Variants multiply sample data, and music shares the cartridge with the rest of the game.
   Default budget **128 KB** per song: 3.125 % of a 4 MB cartridge (the largest without a bank-switching
   mapper), leaving room for several songs plus the game. ≈ 9.9 s of PCM at 13.3 kHz 8-bit, ≈ 9.4 s at
   XGM's ~14 kHz, ≈ 19.7 s at XGM2 half speed (6.65 kHz). Raise or lower it with
   `--max-pcm-bytes`.

## Limits per target

| Limit | DMF | XGM | XGM2 |
|---|---|---|---|
| Sample slots | 255 (1-byte count) | **63** (per `xgm.txt`) | **122** (124-entry SID table − entry #0 − end-marker entry; mml2vgm says 123 — **verify**) |
| Simultaneous PCM | 1 | 4 − reserved SFX | 3 − reserved SFX |
| Pitch per sample | via DAC note/rate (**verify**) | fixed ~14 kHz | fixed 13.3 kHz / 6.65 kHz |
| Byte budget | — | default **128 KB**, `--max-pcm-bytes` | same |

Limits live as named constants in one place (`src/Xgm/XgmLimits.cs`, `src/Dmf/DmfLimits.cs`).

## Capacity check (always runs)

After mapping, the converter computes the **required** variant count, peak simultaneous PCM voices and
PCM bytes, and compares them with the target's limits. If any limit is exceeded it logs a **conversion
note at `Warning` level** before doing anything about it, e.g.:

```
WRN XGM2 capacity exceeded: needs 187 sample slots (limit 122), 4 simultaneous PCM voices (limit 3), 412 KB PCM (budget 128 KB)
WRN Consolidating samples to fit (use --no-consolidate to fail instead)
```

## Consolidation (default)

With the multi-pass optimizer ([multi-pass.md](multi-pass.md)) these steps become moves the allocator can
make, chosen by measured error instead of a fixed order; the fixed order below is what the simple mapper uses
and the optimizer's starting point.

`--reserve-sfx-channels N` lowers the PCM voice limit by N (XGM: 4−N, XGM2: 3−N) so the game can play sound
effects through the same driver.

By default the converter **must** do its best to fit. Steps run in order, cheapest-to-hear first, and stop
as soon as everything fits. Every step that changes the sound logs a conversion note naming the sample(s)
and what changed.

| # | Step | Audible cost |
|---|---|---|
| 0 | **Ignore** samples given with `--ignore-samples` (user's choice, not counted) | user-chosen |
| 1 | **Drop unused** samples and notes that are never triggered | none |
| 2 | **Deduplicate** byte-identical samples / variants (after conversion) | none |
| 3 | **Trim**: trailing silence, data past a loop end, minimal loop unrolling | none – tiny |
| 4 | **Quantize pitch**: merge variants that differ by fine pitch only (finetune, slides) into the nearest semitone; slides become stepped | small |
| 5 | **Lower rate** where content allows (XGM2 half speed 6.65 kHz for bass / low-pass content) — saves bytes | small |
| 6 | **Merge near-duplicates**: samples whose similarity (normalized cross-correlation / spectral distance after resampling) is above a threshold share one slot | small – medium |
| 7 | **Swap sound shapes mid-song**: for the least important notes (see *Importance*), a track switches to a substitute sound for that section — another sample's variant at the right pitch, or an FM/PSG approximation — and switches back when slots allow | medium |
| 8 | **Voice stealing** when more PCM voices are needed than exist: the least important simultaneous note moves to FM/PSG or is cut | medium – large |
| 9 | Last resort: drop the least important variants (notes fall silent) | large |

### Importance

Each (sample, pitch) variant and each note gets an **importance score** used by steps 6–9:
trigger count × audible duration × volume, boosted for percussive/attack-heavy samples (where substitution is
most noticeable) and for notes on downbeats. The lowest-scoring items are substituted first.

### Mid-song swapping (step 7) in detail

- Work per **section** (a run of orders). A track's instrument can be PCM in one section and a substitute in
  another.
- Substitute choice, best first: same sample at the nearest available pitch variant only if the pitch error
  is ≤ a threshold (proposed 0 — never play a wrong pitch); a different sample with the highest similarity;
  an FM patch fitted to the sample's spectrum/envelope; a PSG tone/noise.
- Each swap logs: `WRN Track 3, orders 12–15: sample 07 "strings" C-4 → FM approximation (slot limit)`.

### `--no-consolidate`

Turns steps 4–9 off. If the song still doesn't fit after steps 0–3, the converter prints the capacity
report and exits with code **6**.

## Ignoring samples

`--ignore-samples <list>` (see [cli.md](cli.md)) removes MOD samples before mapping. Notes that trigger an
ignored sample are dropped (the track stays silent until its next non-ignored note). Ignored samples don't
count toward any limit. The startup report marks them `ignored`.
