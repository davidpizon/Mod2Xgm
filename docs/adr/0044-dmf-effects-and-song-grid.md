# ADR 0044: DMF effects and song grid (T5.5)

- **Status:** Accepted
- **Date:** 2026-10-28

## Context

Plan task T5.5: translate the song's MOD effects into DMF effects. The effects need a song grid to sit in (notes,
octaves, volumes, instruments, samples, the order list), and no plan task owned building it: ADR 0040 left it to "the
layout step", ADR 0043 to T5.6. DMF effects differ from MOD's in units (DefleMask slides step the FM chip's F-number,
not the Amiga period; volume is in decibel steps, not linear), in lifetime (a DMF effect keeps running until it is
stopped, a MOD effect lasts one row) and in what exists at all (no sample offset, no volume on the DAC, no pattern
loop, no song stop).

## Decision

All by David, 2026-10-28, unless noted.

- **Scope: effects and the song grid.** `DmfSongBuilder.Build(plan, song, timing, settings, notes)` (`src/Dmf`) builds the
  whole `DmfFile`; `DmfPatternTranslator` writes the cells. T5.6 calls it from the command line.
- **Parts kept as written:** one DMF pattern per order, numbered by matrix row (David chose this over writing the song
  out in playing order). `Bxx` → `0Bxx`, `Dxx` → `0Dxx`, `EEx` stretches its row with both tick times (exact). What DMF
  can't do is the best try plus a note: `Bxx`+`Dxx` starts at row 0, `Dxx` on the last order (ignored by DefleMask)
  jumps to the loop, `F00` jumps to the loop (DefleMask always loops), `E6x` plays its rows once. A loop to an order
  other than 0 is a `0Bxx` on the last row.
- **Stereo copies the Amiga:** FM and DAC notes get `08xy` for their MOD channel's side (channels 1 and 4 left, 2 and 3
  right), written when it changes. The PSG is mono.
- **The DAC has no volume** in DefleMask (Furnace: `noOPN2Vol`) nor in SGDK (`xgm2.h`: PCM priority picks which sample
  plays, fades touch FM and PSG only). So **each DAC sample is copied** once per loudness the song plays it at, in 3 dB
  steps (quieter than −45 dB is silent), and once per start point `9xx` (David). Copies are made on first use; past
  144 (12 banks × 12 notes) a note plays the nearest copy of its sample, or is left out, with a note. A looped sample on
  the DAC is played out as long as its longest note (0.5–10 s).
- **FM notes are placed at the pitch measured from the sample** (`SamplePitch`, YIN), because a MOD note name says how
  fast the sample plays, not how high it sounds (David). The cents left over go into `E5xx`. A sample with no clear pitch
  is placed two octaves above its name (ProTracker C-2 → C-4), with a note. Brings a small part of T8.1 forward.
- **PSG noise** gets one instrument per sample, whose volume macro follows the sample's loudness tick by tick (2 dB
  steps), so a hit fades like the recording; the noise rate (one of the three fixed ones) is picked by brightness.
- **Units** (from Furnace's DMF compatibility mode, read not copied, then measured: below): slide and portamento speeds
  in F-number steps measured halfway along the row's move; vibrato speed × (ticks − 1) / ticks (ProTracker skips a row's
  first tick, DefleMask doesn't), depth in 1/16 semitone; tremolo cycle 128 steps, about 0.65 dB per depth digit, down
  only; volume 0.75 dB per FM step and 2 dB per PSG step; volume slides reach the MOD's row-end level, and every slide
  row starts from the MOD's volume so a slide DefleMask can't keep up with lags one row at most. Per-tick counts
  (retrigger, cut, delay) scale with DMF ticks per MOD tick; when the DMF ticks faster than the MOD, `E0xx` slows
  arpeggio to match (**Verify**: not in DefleMask's effect list).
- **DefleMask's own rules, found with the effect probes:** a running effect is never written again with the same value
  (DefleMask stacks a re-written tremolo until the note is silent), and tone portamento is written on the note's row
  only, with one speed for the whole glide (DefleMask freezes a glide when `03xx` is written again on a row without a
  note). Furnace doesn't copy either quirk, so DefleMask's own export is the reference.
- **Effect columns:** each channel gets as many as its busiest row needs (1–4); song-wide effects (jumps, tick times) go
  to the first channel with room, PSG1–3 first (the simple mapper leaves them free). A cell needing more than four keeps
  the most important, with a note.
- **Conversion notes** for everything left out or approximated: effects a channel kind can't play (pitch effects on the
  DAC, the Amiga filter, glissando, invert loop, tremolo waveform), the DAC copies, notes moved by octaves, DAC notes
  played at their sample's most-used pitch (the simple mapper's choice, ADR 0040), and the measured or assumed pitch
  of every FM sample.
- **Effect probes** (`tests/golden/dmf-effect-probes/`): 11 MODs built in code, converted, and rendered by openmpt123
  (the MOD), Furnace and DefleMask (the DMF), as committed WAV files (22.05 kHz, mono except `panning`), with a listening
  checklist. Measured against the MOD: pitch within the meter's resolution (~20 cents) on slides, portamento, arpeggio,
  vibrato and jumps; volume steps and slides within 1–2 dB (the YM2612's DAC distortion adds up to 2 dB at the quietest
  levels); DefleMask plays 5.8 dB quieter overall than Furnace.

## Options considered

- **Writing the song out in playing order** (every jump, repeat and delay exact; one jump back at the end): David chose
  the parts as written, easier to read and edit in DefleMask.
- **Full volume on the DAC plus a note:** saves memory, but quiet hits come out too loud. Turning FM/PSG up instead only
  balances the whole mix.
- **A fixed octave shift for FM notes:** simple, but many instruments land an octave or two off.
- **Trusting Furnace's renders alone:** Furnace plays DMF tremolo and portamento differently from DefleMask (above); the
  DefleMask export caught both.

## Consequences

- `PlanCell` carries `Source` (the song channel) and `Sample` (the song sample); `SimpleMapper` fills both.
- New: `src/Dmf/DmfSongBuilder.cs`, `DmfPatternTranslator.cs`, `DmfUnits.cs`, `src/Mapping/SamplePitch.cs`; tests drive
  the real pipeline through `DmfConversion` (`tests/Mod2Xgm.Tests/Dmf/`).
- `docs/formats/dmf.md` documents the units and DefleMask's rules; the plan marks T5.5 done and gives T5.6 the
  command-line hookup.
- The FM release tail after `ECxx` (DefleMask releases the note; the MOD stops dead) and the YM2612 DAC's low-level floor
  are left to the patch library and the optimizer (M8, M9). DAC copies per pitch, instead of one pitch per sample, would
  be the next accuracy step for drums played at several pitches.
