# ADR 0061: The DMF's notes take their pitch from the tick player (T7.10)

- **Status:** Accepted; supersedes the finetune parts of [ADR 0030](0030-amiga-pitch-regions-finetune.md) and the "song
  model and DMF keep ProTracker's tables" consequence of [ADR 0060](0060-openmpt-pitch-and-loops.md)
- **Date:** 2026-10-31

## Context

[ADR 0060](0060-openmpt-pitch-and-loops.md) (T7.6) moved the tick player's pitch to OpenMPT's measured rules: periods in
quarters, a finetuned note is its finetune's C-1 period halved for each octave up with the quarters truncated, finetune 0
plays ProTracker's table except B-3 = 113¼, no note below 113 in a song within ProTracker's range. The MOD renderer and
the Genesis renderer play the tick player's `Playback`, so they follow it. The DMF does not: David left it for a later
task, thought to be ProTracker's tables against OpenMPT's (up to about 7 cents).

Looking closer for this task, the gap was bigger. [ADR 0030](0030-amiga-pitch-regions-finetune.md) decided that cells
keep their note **as written** (finetune 0), "because a cell often doesn't name its instrument", and that the tick
player applies finetune. The song model's `AmigaPitch.ApplyFineTune` (ProTracker's lookup) was never called outside its
tests. The mapper copied the cells' notes into the plan, and the DMF writer placed FM notes and picked DAC rates from
them, so **the DMF ignored the instrument's finetune altogether**: a finetuned sample played up to 100 cents flat
(−8) or 87.5 cents sharp (+7). Only an `E5x` on a note's row was applied, as a shift relative to the instrument's
finetune. In the corpus, every sample of `sharpojects_cantpleaseeveryone` has finetune +1 (ADR 0060).

The plan had no task for it; David made it **T7.10** (M7, after T7.6).

## Decision

- **The plan's notes take their pitch from the tick player** (David, over tuning in the mapper or in `ModToSong`). The
  tick player keeps, for every written note, the pitch it plays it at the first time its row plays:
  `Playback.NotePitches`, keyed by `NotePosition` (order index, row, channel). That is OpenMPT's period for the note
  at the channel's finetune, or at the row's `E5x`; on a tone portamento row (`3xx`, `5xy`) it is the slide's target.
  Notes beyond ProTracker's range are snapped to the nearest note, as the tick player plays them
  ([ADR 0055](0055-notes-beyond-protracker-range.md)).
- `IChannelMapper.Map` takes the `Playback`. The simple mapper looks up each note by its order entry, row and channel
  and plans it at that pitch. A note the playback lacks (a row the song never plays, such as the rest of a pattern a
  `Dxx` leaves) is moved by its instrument's finetune in cents (`Instrument.FineTuneCents`), so it agrees with the notes
  that play: kept as written, it would split a DAC sample's pitch vote and be counted as off its sample's pitch.
- The DMF writer no longer adds an `E5x`'s shift to an FM note: it is in the note's pitch now. `E5x` is not reported
  as left out on FM or DAC notes.
- Cells keep their notes as written (ADR 0030 stands there). `Instrument.FineTuneCents` stays in the model as data.
- `AmigaPitch.ApplyFineTune` and `FineTunedPeriod` go: nothing used them, and ProTracker's lookup is no longer the rule.
  `AmigaPitch.Table` (ProTracker's 16 tables, which OpenMPT's rule starts from) stays.
- The conversion runs the tick player once, before mapping, and `--render-wav` reuses it. Its conversion notes (notes
  beyond ProTracker's range, `EFx`) are now part of every conversion, not only of `--render-wav`.

## Options considered

- **Pitch from the tick player** (chosen): one implementation of the tuning, so the DMF, the Genesis renderer and the
  MOD renderer can't drift apart again; the tick player already knows which instrument and finetune each channel
  carries at every row, following the song's flow.
- **Tune in the mapper**: the simple mapper also tracks each channel's instrument, so it could tune each note with
  OpenMPT's rule moved into `AmigaPitch`. A second copy of the rule, and of the instrument and `E5x` tracking; it would
  follow the order list rather than the song's flow.
- **Tune the cells in `ModToSong`**: a cell would need the instrument last named on its channel, which depends on how
  the pattern is reached (a pattern can be played from several orders, after a jump); wrong whenever the cell names no
  instrument and the path differs. Rejected in ADR 0030 for the same reason.

## Consequences

- DMF notes of finetuned samples now sound where libopenmpt plays them; FM notes move by the finetune (and by up to
  about 7 cents from ProTracker's tables), DAC samples' rates likewise. Results below.
- A row the song plays more than once with another finetune (an `E6x` loop that changes the instrument, a jump back)
  is planned at its first pitch: the plan has one pattern per order entry, not per pass. The Genesis renderer's FM
  follows the tick player's pitch on every pass; its DAC rates come from the plan, as in the DMF.
- The optimizer (M9) gets the same `Playback`; anything it plans takes the tuned pitch.
- Docs: [formats/mod.md#pitch](../formats/mod.md#pitch), [architecture.md](../architecture.md); ADR 0030's and ADR
  0060's Status lines.

## Results

On the corpus, whole-song log-mel distance (dB, lower is better) to libopenmpt for the DMF rendered by Furnace (the
`furnace` column) and by the in-app Genesis renderer (`genesis`), before → after ([harness.md](../harness.md)):

| Song | Finetunes | furnace | genesis |
|---|---|---|---|
| `10kdub` |  | 10.10 (same) | 11.74 (same) |
| `8bit_castle` |  | 13.63 (same) | 15.52 (same) |
| `cancan` | −1 | 10.17 (same) | 10.56 (same) |
| `drozerix_-_ai_renaissance` | −1, +7 | 13.83 → 13.80 | 16.43 (same) |
| `drozerix_-_neon_techno` | +2 | 11.76 (same) | 12.79 (same) |
| `drozerix_-_stardust_jam` | −4, +2 | 10.53 → 10.51 | 9.82 → 9.81 |
| `drozerix_-_the_hawks_claw` | −8 … +1 | 14.07 → 14.04 | 11.51 → 11.49 |
| `fx-poly1` | −8 | 6.74 → 6.43 | 6.72 → 6.64 |
| `hit` |  | 9.71 (same) | 10.34 (same) |
| `jam_-_heatwave_cruise` |  | 10.16 (same) | 11.45 (same) |
| `jam_-_latenightcoffee` |  | 8.24 (same) | 9.52 (same) |
| `jam_-_xlr8` |  | 11.59 (same) | 13.02 (same) |
| `k0w-rsnd` | −8 | 8.72 → 8.56 | 8.63 (same) |
| `kc-dancinonamiga` |  | 11.35 (same) | 11.79 (same) |
| `musix-cute-october` |  | 8.97 (same) | 9.85 (same) |
| `musix-omg-plz-help` | +1, +2 | 9.73 (same) | 9.14 (same) |
| `musix-rubbish` | −2 … +3 | 12.40 → 11.66 | 12.20 → 12.02 |
| `sharpojects_cantpleaseeveryone` | +1 | 11.82 → 11.84 | 9.04 → 8.96 |
| `soundtracker15` |  | 15.08 → 15.07 | 19.51 (same) |
| **Average** | | 10.98 → 10.91 | 11.56 → 11.54 |

Only songs with finetuned samples move, and `soundtracker15` (no finetune) by 0.01 dB: a few of its notes play at a
period other than the written one (7 cells written between table entries, such as 349, which the tick player snaps to
a note). The biggest gains are where finetunes are large or many: `musix-rubbish` (−0.74 dB in Furnace), `fx-poly1` (−0.31),
`k0w-rsnd` (−0.16). `sharpojects_cantpleaseeveryone`, every sample at +1, gets 0.02 dB worse in Furnace and better in
the Genesis renderer: its Furnace render ends 25 s before libopenmpt's and is 6 ms late, which dominates its score
(already so before; not this task). `cancan`, `drozerix_-_neon_techno` and `musix-omg-plz-help` don't move: their
finetuned samples play little or not at all. The Genesis renderer moves less because its FM already followed the tick
player; only its DAC rates, which come from the plan, changed. Measured with libopenmpt 0.8.9 and Furnace at 44.1 kHz.
