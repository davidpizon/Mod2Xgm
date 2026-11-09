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
   used over two octaves can need 24 variants on its own. **No PCM volume either** (DMF's DAC and SGDK's drivers
   play a sample as stored), and **no sample offset**: the DMF writer makes a variant per 3 dB of loudness and per
   start point (`9xx`) a sample is played at ([ADR 0044](adr/0044-dmf-effects-and-song-grid.md)).
2. **Limited simultaneous PCM voices:** XGM 4, XGM2 3, DMF 1 (FM6 DAC). A 4-channel MOD that plays
   4 samples at once doesn't fit XGM2 or DMF.
3. **Byte budget (optional).** Variants multiply sample data, and music shares the cartridge with the rest of the
   game, but a Genesis song has no fixed budget (David, [ADR 0093](adr/0093-optional-pcm-budget.md)). With no
   `--max-pcm-bytes`, XGM and XGM2 hold what the format can (16,776,960 bytes). A game that shares the cartridge sets
   one, e.g. `--max-pcm-bytes 128K`: 3.125 % of a 4 MB cartridge (the largest without a bank-switching mapper), ≈ 9.9 s
   of PCM at 13.3 kHz 8-bit, ≈ 9.4 s at XGM's ~14 kHz, ≈ 19.7 s at XGM2 half speed (6.65 kHz).

## Limits per target

| Limit | DMF | XGM | XGM2 |
|---|---|---|---|
| Sample slots | **144** reachable as DAC notes (12 banks × 12; the file's 1-byte count allows 255) | **63** (per `xgm.txt`) | **123** (124-entry SID table − end entry; SGDK's `XGM.java`, [ADR 0085](adr/0085-target-limits.md)) |
| Simultaneous PCM | 1 | 4 − reserved SFX | 3 − reserved SFX |
| Pitch per sample | fixed: one rate per sample; the note picks one of 144 samples ([formats/dmf.md](formats/dmf.md#dac-samples-on-fm6)) | fixed ~14 kHz | fixed 13.3 kHz / 6.65 kHz |
| Byte budget | none | **none by default**: at most 16,776,960 bytes, what the format holds; `--max-pcm-bytes` sets a smaller one (a bigger one is cut, with a conversion note; [ADR 0093](adr/0093-optional-pcm-budget.md)) | same |

Each `--format` uses its own column. The limits live in one place, `TargetLimits` (`src/Optimizer/TargetLimits.cs`,
[ADR 0085](adr/0085-target-limits.md)), which also holds the FM, CH3 and PSG limits in [multi-pass.md](multi-pass.md#constraints-the-allocator-enforces).
XGM's and XGM2's limits never bind a DMF, so a big one can pass what they hold and would then lose sounds if exported to them.

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
  `empty` (no sound). `ignored` (from `--ignore-samples`) is added by the report.
- **Conversion notes** report everything odd: the song looping back; `F00`; a jump past the end; orders never reached;
  patterns stored but never played; notes delayed past their row; notes before a channel has a sample; an empty sample
  that is played; a sample with sound that is never played.

This is a row-level walk. Exact tick-by-tick playback is plan M3 (T3.4), which may refine the counts.

## Capacity check (always runs)

After mapping, `CapacityCheck` (`src/Optimizer/CapacityCheck.cs`, [ADR 0086](adr/0086-capacity-check.md)) computes the
**required** variant count, peak simultaneous PCM voices and PCM bytes, and compares them with the target's limits. If
any limit is exceeded it logs a **conversion note at `Warning` level**, listing the limits passed, e.g. with `--max-pcm-bytes 128K`:

```
WRN XGM2 capacity exceeded: needs 187 sample slots (limit 123), 4 simultaneous PCM voices (limit 3), 412 KB PCM (budget 128 KB)
WRN Consolidating samples to fit (use --no-consolidate to fail instead)
```

Only **recordings** count: samples the mapper plays as PCM, even if a clash moved their notes elsewhere. FM and PSG notes
store nothing. After the free steps 0–3 below:

- **Slots:** one copy per sample, starting pitch, 3 dB of loudness and start point. Held length sets a copy's size, not
  how many there are, because a shorter note can stop a copy early.
- **Voices:** the most recordings sounding at once, tick by tick. A kick still ringing when a snare starts counts as two.
- **Bytes:** every copy on the target, at full rate, as long as its longest note (at most 10 s of a loop).

## Consolidation (default)

With the multi-pass optimizer ([multi-pass.md](multi-pass.md)) these steps become moves the allocator can
make, chosen by measured error instead of a fixed order; the fixed order below is what the simple mapper uses
and the optimizer's starting point.

`--reserve-sfx-channels N` lowers the PCM voice limit by N (XGM: 4−N, XGM2: 3−N) so the game can play sound
effects through the same driver.

With `--mapping optimizer`, steps 4, 4b and 6 are made by `RecordingShrinker` (`src/Optimizer/RecordingShrinker.cs`,
plan T9.4a, [ADR 0091](adr/0091-shrinking-recordings.md)) before the allocator places notes: one move at a time, on the least
important sample first, until the recordings fit the slots and bytes, in the order merge, share pitch (10 cents), share loudness. A song whose
recordings fit gets no move. Step 5 (XGM2 half speed) is not a default move: David heard it as crunchy and it lost every
listening round to moving notes to FM/PSG. On XGM2, when recordings on both sides sound at once, the one DAC plays in both
speakers (`DacPanClash.Centre`, chosen by ear). What is left
over (too many voices at once, or still too big) the allocator handles by moving notes to FM/PSG or cutting them (steps 7–9).

By default the converter **must** do its best to fit. Steps run in order, cheapest-to-hear first, and stop
as soon as everything fits. Every step that changes the sound logs a conversion note naming the sample(s)
and what changed.

| # | Step | Audible cost |
|---|---|---|
| 0 | **Ignore** samples given with `--ignore-samples` (user's choice, not counted) | user-chosen |
| 1 | **Drop unused** samples and notes that are never triggered | none |
| 2 | **Deduplicate** byte-identical samples / variants (after conversion) | none |
| 3 | **Trim**: trailing silence, data past a loop end, minimal loop unrolling | none – tiny |
| 4 | **Share pitch**: copies a few cents apart (finetune, slides) play the most played one's copy; never far enough to sound like a wrong note (10 cents, [ADR 0091](adr/0091-shrinking-recordings.md)) | small |
| 4b | **Share loudness** (XGM/XGM2, which can't change a sample's volume): one copy per 6 dB instead of per 3 dB, so a note plays up to 3 dB off | small |
| 5 | **Lower rate** where content allows (XGM2 half speed 6.65 kHz for bass / low-pass content) — saves bytes; not used by default, crunchy by ear ([ADR 0091](adr/0091-shrinking-recordings.md)) | small – medium |
| 6 | **Merge near-duplicates**: samples whose similarity (normalized cross-correlation / spectral distance after resampling) is above a threshold share one slot | small – medium |
| 7 | **Swap sound shapes mid-song**: for the least important notes (see *Importance*), a track switches to a substitute sound for that section — another sample's variant at the right pitch, or an FM/PSG approximation — and switches back when slots allow | medium |
| 8 | **Voice stealing** when more PCM voices are needed than exist: first, where the overlap fits, its notes are **pre-mixed** into one sample ([ADR 0052](adr/0052-pre-mixed-pcm.md), [ADR 0094](adr/0094-pre-mixed-voice-steals.md): after the allocation, at most 4 notes, not turned down by more than 6 dB, a looped note going on as a tail); otherwise the least important simultaneous note moves to FM/PSG or is cut | small (pre-mix) – large |
| 9 | Last resort: drop the least important variants (notes fall silent) | large |

### Importance

Each (sample, pitch) variant and each note gets an **importance score** used by steps 6–9:
trigger count × audible duration × volume, boosted for percussive/attack-heavy samples (where substitution is
most noticeable) and for notes on downbeats. The lowest-scoring items are substituted first. `OptimizerMapper` computes it per
note: length up to 0.25 s × volume, up to 9× for a sample that hits, 1.5× on a downbeat ([ADR 0087](adr/0087-greedy-allocator.md)).

### Mid-song swapping (step 7) in detail

- Work per **section** (a run of orders). A track's instrument can be PCM in one section and a substitute in
  another.
- Substitute choice, best first: same sample at the nearest available pitch variant only if the pitch error
  is ≤ a threshold (proposed 0 — never play a wrong pitch); a different sample with the highest similarity;
  an FM patch fitted to the sample's spectrum/envelope; a PSG tone/noise.
- Each swap logs: `WRN Track 3, orders 12–15: sample 07 "strings" C-4 → FM approximation (slot limit)`.

### `--no-consolidate`

Turns steps 4–9 off. If the song still doesn't fit after steps 0–3, on any limit including voices, the converter prints
the capacity report, writes no file and exits with code **6**. So a DMF with two recordings sounding at once stops
there, as moving one to FM would change the song ([ADR 0086](adr/0086-capacity-check.md)).

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
  what they need. Raise `--max-pcm-bytes` or require fewer samples. (With `--mapping optimizer`; the simple mapper doesn't check it.)

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
