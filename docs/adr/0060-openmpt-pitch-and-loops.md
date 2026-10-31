# ADR 0060: The tick player's pitch and loops follow OpenMPT (T7.6)

- **Status:** Accepted; amends [ADR 0033](0033-tick-player.md); supersedes parts of [ADR 0055](0055-notes-beyond-protracker-range.md); "the song model and the DMF keep ProTracker's tables" superseded by [ADR 0061](0061-dmf-pitch-from-tick-player.md)
- **Date:** 2026-10-31

## Context

Plan task T7.6 closes the MOD renderer's known differences from OpenMPT ([ADR 0054](0054-mod-renderer.md#known-differences)):
finetune of in-range notes (thought to be 2^(−finetune/96) in OpenMPT), the first pass of a loop, and a 1–3 ms offset
that wanders through `sharpojects_cantpleaseeveryone`. Each was to be measured against libopenmpt, then fixed or
recorded. The first two come from the tick player, a port of ProTracker 2's replayer ([ADR 0033](0033-tick-player.md)),
not from the mixer. [ADR 0055](0055-notes-beyond-protracker-range.md) had already moved songs with notes beyond
ProTracker's range to OpenMPT's rules, and said every other song stays ProTracker.

Everything below was measured with **libopenmpt 0.8.9** and probe MODs: one note, slide, arpeggio, vibrato or loop at a
time, rendered at 48–192 kHz, its pitch read tick by tick from the zero crossings of a looped sine (to about 0.05 cents),
or its sample position read from a ramp.

## Measurements

- **Finetune is not 2^(−finetune/96).** OpenMPT keeps a MOD's periods in **quarters**. A finetuned note is its finetune's
  C-1 period from ProTracker's tables, halved for each octave up, the quarters truncated: C-3 at +1 is 850 ÷ 4 = 212.5
  (ProTracker's table: 213), A-3 at −8 is 538 ÷ 4 = 134.5 (135). Finetune 0 plays ProTracker's table, except B-3,
  which plays 113¼. A song within ProTracker's range plays **no note below 113**. This rule matched all 576 notes (36 notes
  × 16 finetunes) exactly; ProTracker's tables are up to 7 cents off on most notes and up to 78 cents on the top notes of
  positive finetunes (B-3 at +7 is 108 in ProTracker, 113 in OpenMPT). In songs beyond the range the same rule holds over
  six octaves, to 0.05 cents; ADR 0055's 2^(−finetune/96) was up to 46 cents off.
- **Slides keep the quarter** (A-3 at +1: 126¼, 125¼, 124¼) and move by 4 quarters a unit, tone portamento too.
- **Slide limits depend on the finetune:** a slide up stops at the finetune's B-3 but not below 113 (113¼ at finetune 0,
  120 at −8, 113 at +1), a slide down at its C-1 (856 at 0, 907 at −8, 850 at +1). ProTracker's underflow (a slide past 0
  wraps to a very low note) is not copied. Beyond the range slides run from 14 to 3424, as ADR 0055 measured.
- **Arpeggio and tone portamento** play the note's own period by the same rule. An arpeggio's base is the first note of
  the finetune not above the current period (ProTracker's lookup), and OpenMPT plays that snapped base on the arpeggio's
  first tick too, so after a slide (209) the arpeggio is 202, 170, 135, then the slid 209 comes back. Within the range a
  note past B-3 reads the same finetune's notes again from C-1, 37 notes on (36 counts as C-1): A-3 + 12 at +1 is 535
  (ProTracker reads on into the next finetune's table: 532). Beyond the range the rule goes on past B-5 and stops at 14.
- **Glissando** snaps to the finetune's notes the same way, beyond the range too (ADR 0055 left it unsnapped there).
- **Vibrato is finer than ProTracker's.** OpenMPT uses a 64-step sine with peak 127 (0, 12, 25, 37, 49, …), and the
  period moves by value × depth ÷ 16 quarters, rounded toward 0; the ramp is 4 a step (−127 + 4 a step in the second
  half), the square ±127. This matched every tick of sine, ramp and square at several speeds and depths. ProTracker's
  depth-1 vibrato moves by 0 or 1 whole period; OpenMPT's by ¾, 1¼, 1¾. Vibrato is **not** limited: on B-3 it dips to 83½.
- **A loop that starts at 0 ends at its loop end the first time through.** ProTracker plays such a sample to its end once
  before looping; OpenMPT never plays past the loop end. A 15-sample (Ultimate Soundtracker) sample, which starts at its
  loop start, does the same.
- **`9xx` is ProTracker's in OpenMPT too:** the offset is added twice, and builds up on notes without a sample number.
  The tick player already did this.
- **The wandering offset was finetune.** In `sharpojects_cantpleaseeveryone` the renderer ran ahead of libopenmpt by
  about 1.3 ms a second within each pattern, back to 0 at each pattern start: a long sample playing 0.13 % (2 cents)
  fast. Every sample in the song has finetune +1.

## Decision

- **The tick player plays every song's pitch the OpenMPT way** (David, after the measurements): periods in quarters,
  the rules above for notes, slides and their limits, arpeggio, tone portamento, glissando and vibrato. This supersedes
  ADR 0055's "every other song is unchanged", its 2^(−finetune/96), its semitone arpeggio above B-3 and its unsnapped
  glissando.
- **Exact quarters, not whole periods** (David): rounding would leave up to 4 cents at the top notes.
- **A looping sample ends at its loop end on the first pass too,** in every song.
- **Paula's floor of 113 goes:** OpenMPT limits notes and slides where they are set and lets vibrato past them.
- A written period below every finetune-0 note of the range (108–112) plays B-3; ProTracker read its table's zero padding.
- Everything else stays ProTracker 2: the song's flow, effect memory, `9xx`, volume, tremolo.
- **The song model and the DMF keep ProTracker's tables** ([ADR 0030](0030-amiga-pitch-regions-finetune.md)) for now
  (David: a later task).

## Results

On the corpus, whole-song log-mel distance of the in-app MOD renderer to libopenmpt (the `mod-renderer` column,
[harness.md](../harness.md#in-app-mod-renderer)), before → after:

| Song | 44.1 kHz | 22.05 kHz | 8 kHz |
|---|---|---|---|
| `10kdub` | 0.35 → 0.35 | 0.20 → 0.20 | 0.85 → 0.85 |
| `8bit_castle` | 0.14 → 0.05 | 0.14 → 0.06 | 0.23 → 0.15 |
| `cancan` | 0.14 → 0.01 | 0.15 → 0.02 | 0.26 → 0.17 |
| `drozerix_-_ai_renaissance` | **0.85 → 0.09** | 0.75 → 0.06 | 0.62 → 0.17 |
| `drozerix_-_neon_techno` | 0.10 → 0.02 | 0.09 → 0.02 | 0.16 → 0.12 |
| `drozerix_-_stardust_jam` | 0.64 → 0.04 | 0.60 → 0.04 | 0.58 → 0.13 |
| `drozerix_-_the_hawks_claw` | 0.39 → 0.01 | 0.38 → 0.01 | 0.35 → 0.10 |
| `fx-poly1` | 0.46 → 0.09 | 0.56 → 0.07 | **0.94 → 0.11** |
| `hit` | 0.02 → 0.01 | 0.01 → 0.01 | 0.16 → 0.15 |
| `jam_-_heatwave_cruise` | 0.49 → 0.49 | 0.46 → 0.46 | 0.47 → 0.47 |
| `jam_-_latenightcoffee` | 0.63 → 0.62 | 0.62 → 0.62 | 0.62 → 0.62 |
| `jam_-_xlr8` | 0.02 → 0.02 | 0.02 → 0.02 | 0.11 → 0.11 |
| `k0w-rsnd` | 0.09 → 0.09 | 0.03 → 0.03 | 0.17 → 0.17 |
| `kc-dancinonamiga` | 0.02 → 0.02 | 0.02 → 0.02 | 0.12 → 0.12 |
| `musix-cute-october` | 0.02 → 0.02 | 0.02 → 0.02 | 0.12 → 0.12 |
| `musix-omg-plz-help` | 0.14 → 0.01 | 0.13 → 0.01 | 0.21 → 0.12 |
| `musix-rubbish` | 0.04 → 0.04 | 0.05 → 0.05 | 0.10 → 0.10 |
| `sharpojects_cantpleaseeveryone` | 0.54 → 0.06 | 0.43 → 0.04 | 0.46 → 0.29 |
| `soundtracker15` | 0.53 → 0.41 | **0.76 → 0.62** | 0.55 → 0.36 |

The worst song goes down at every rate: 0.85 → 0.62 dB at 44.1 kHz, 0.76 → 0.62 at 22.05 kHz, 0.94 → 0.85 at 8 kHz;
the mean from 0.30 to 0.13 dB at 44.1 kHz. No song got worse. `sharpojects_cantpleaseeveryone` now lines up with
libopenmpt sample for sample (lag 0, correlation 1.000 over its first 30 s). The genesis column hardly moves (11.56 →
11.56 dB mean): its distance is the chips', not the pitch's.

## Known differences

- **A small constant lead in the `jam_-_*` songs:** the renderer is 0.11 ms (`heatwave_cruise`) to 0.25 ms
  (`latenightcoffee`) early from the start, and stays so; those songs (Cxx and Dxx only) didn't move with this change and
  are now the worst at 44.1 kHz. Not the tick player's pitch; not investigated yet.
- **Vibrato and tremolo wave 3** is random in OpenMPT; the tick player plays it as a square, as ProTracker does. No
  corpus song uses `E4x` or `E7x`.
- **Tremolo** (`7xy`) wasn't measured and stays ProTracker's. No corpus song uses it.
- In a song beyond the range, OpenMPT keeps glissando's snapped period on the first tick of the next row; the tick player
  plays the unsnapped period there, as within the range.
- An arpeggio on a period above every note keeps the period on its first tick (not measured).

## Options considered

- **The tick player, every song** (chosen): one rule everywhere, so the MOD renderer, the Genesis renderer and
  `--render-wav` all match the reference the harness scores against.
- **Only in the MOD renderer** (a switch the renderer turns on): keeps the tick player a pure ProTracker port, but the
  Genesis renderer would aim at ProTracker's pitches while the harness judges it against OpenMPT's.
- **Whole periods** rounded from OpenMPT's: a smaller change, up to 4 cents off at the top.
- **2^(−finetune/96)**, as ADR 0054 assumed: up to 46 cents off at the top of six octaves.

## Consequences

- `src/Mod/ModTickPlayer.cs` keeps periods in quarters; `Playback` pitches can now fall between whole periods.
- ADR 0054's known differences are closed (this ADR), and harness.md's numbers are new.
- The Genesis renderer's and `--render-wav`'s pitches change with the tick player's.
- The DMF's finetune (`AmigaPitch`, `ModToSong`) still follows ProTracker's tables: a later task.
