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
| Sample slots | 255 (1-byte count); **144** reachable as DAC notes (12 banks × 12) | **63** (per `xgm.txt`) | **122** (124-entry SID table − entry #0 − end-marker entry; mml2vgm says 123 — **verify**) |
| Simultaneous PCM | 1 | 4 − reserved SFX | 3 − reserved SFX |
| Pitch per sample | fixed: one rate per sample; the note picks one of 144 samples ([formats/dmf.md](formats/dmf.md#dac-samples-on-fm6)) | fixed ~14 kHz | fixed 13.3 kHz / 6.65 kHz |
| Byte budget | — | default **128 KB**, `--max-pcm-bytes` | same |

Limits live as named constants in one place (`src/Xgm/XgmLimits.cs`, `src/Dmf/DmfLimits.cs`).

## Usage scan (`src/Mod/SampleUsage.cs`, plan T2.4)

Before anything is dropped or merged, the converter counts how often each sample is played. The counts are the
**Notes** column of the sample table ([cli.md](cli.md#console-report)) and feed *Importance* below. Decided by David,
2026-10-27:

- **A note counts when it really starts.** A cell with a period plays the channel's current sample: the last sample
  number seen on that channel, even in an earlier row. A sample number without a period only selects the sample.
- **Not counted:** tone portamento (`3xx`, `5xy`), which slides the playing sound to the new pitch, and a note delayed
  by `EDx` with x ≥ the current speed, which never plays. Retrigger `E9x` counts once.
- **The song's flow is followed:** `Bxx` (position jump), `Dxx` (pattern break, two decimal digits; past row 63 means
  row 0), `E6x` (pattern loop, per channel, every repeat counted) and `F00` (stop). `Fxx` below `0x20` sets the speed
  used for `EDx`. A jump past the last order ends the song.
- **Loops:** a song that just reaches its end is walked once. If it jumps back to rows it already played, the repeating
  part is walked **one more time**, then the walk stops.
- **Status:** `used` (holds sound and is played), `unused` (holds sound, never played: dropped by consolidation step 1),
  `empty` (no sound). `ignored` (from `--ignore-samples`) is added by T5.6.
- **Conversion notes** report everything odd: the song looping back; `F00`; a jump past the end; orders never reached;
  patterns stored but never played; notes delayed past their row; notes before a channel has a sample; an empty sample
  that is played; a sample with sound that is never played.

This is a row-level walk. Exact tick-by-tick playback is plan M3 (T3.4), which may refine the counts.

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

## Required FM, DAC and PSG samples

The user can pin a sample's rendering. All three lists are hard constraints for the mapper and the optimizer.

**`--require-fm <list>`**: the sample is always played by an **FM** channel (a fitted or library patch). It never
uses PCM, so it costs no PCM slots, voices or bytes, and it's never moved to PSG.

**`--require-dac <list>`**: the sample is always **digitized PCM**. It is never replaced by FM or PSG: consolidation
steps 7 (mid-song swap) and 8 (move to FM/PSG) skip it, and step 6 (merge near-duplicates) doesn't replace its data
with another sample's. PCM-preserving steps still apply: unused data, dedupe, trim, pitch quantize, half rate.
- **Too many at once:** when more required-PCM notes overlap than there are PCM voices (DMF has only one), the least
  important overlapping note is cut, and a conversion note says so. It is never moved to FM.
- **Too big for the target:** if the required-PCM samples alone still exceed the PCM slots or `--max-pcm-bytes` after
  the PCM-preserving steps, the conversion fails with **exit 6**. The capacity report names the required samples and
  what they need. Raise `--max-pcm-bytes` or require fewer samples.

**`--require-psg <list>`**: the sample is always played by the **PSG**, so it costs no PCM slots, voices or bytes and
is never replaced by FM or the DAC. The PSG's squares and noise can't copy a recording, so this is a rendering
choice, not a faithful one. The simple mapper uses the PSG **noise** channel (the three tone channels are left to the
optimizer); when two required-PSG or noise notes start on the same row, the louder keeps the channel and a required
loser is dropped with a conversion note, never moved to FM.

**Rules:**
- A sample can't be in two of the lists, or in a list and `--ignore-samples`. The command line rejects that (exit 1).
- A required sample that is an empty slot, or never played, gets a conversion note and the requirement is ignored.
- Numbers above the module's sample count are rejected after the MOD is read, as for `--ignore-samples`.

## Ignoring samples

`--ignore-samples <list>` (see [cli.md](cli.md)) removes MOD samples before mapping. Notes that trigger an
ignored sample are dropped (the track stays silent until its next non-ignored note). Ignored samples don't
count toward any limit. The startup report marks them `ignored`.
