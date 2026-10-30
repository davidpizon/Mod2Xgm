# ADR 0054: In-app MOD renderer (T7.1)

- **Status:** Accepted
- **Date:** 2026-10-30

## Context

Plan task T7.1: the multi-pass optimizer ([multi-pass.md](../multi-pass.md)) has to score thousands of candidates
against the original MOD, inside the converter, with no external program (AGENTS.md, hard constraint 2). It needs its
own rendering of the MOD as the target. The acceptance is a score against libopenmpt (openmpt123's engine, the
harness's reference, [ADR 0047](0047-accuracy-harness.md)) "below a tight threshold on the corpus".

There are two ways for a MOD to sound "right": like a real Amiga (Paula's hard left/right stereo, no interpolation, the
A500's fixed low-pass and the LED filter) or like OpenMPT's clean default. The harness's reference is the clean one, and
it is the sound most people know the songs by.

## Decision

- **The renderer sounds like OpenMPT's default, not like an Amiga** (David). `src/Render/ModRenderer.cs` mixes the
  tick player's `Playback` ([ADR 0033](0033-tick-player.md)) to stereo. The reference stays libopenmpt's default
  render.
- **The constants were measured from libopenmpt 0.8.9** with probe MODs, not taken from memory:

  | What | Value |
  |---|---|
  | Interpolation | 8-tap Kaiser-windowed sinc, 4096 phases, taps at −3 … +4 (`src/Render/SincTable.cs`) |
  | Normal filter | β 9.6377, cutoff 0.97 of the sample's Nyquist frequency |
  | Played more than 1.1875× the output rate | β 8.5, cutoff 0.5 (OpenMPT's "1.3×" table) |
  | Played more than 1.5× the output rate | β 7.25, cutoff 0.415, fitted to libopenmpt's response within 0.3 dB |
  | Channel gain | 0.5 |
  | Panning | MOD channels at ±0.5 (75 % / 25 %), linear pan law |
  | Volume ramps | up over 363 µs, down over 952 µs |
  | New note | the old voice fades out over the down ramp while the new one starts |
  | Sample end | the last value is held and decays by 1/256 per output sample (OpenMPT's click removal) |
  | LED filter (`E0x`) | ignored, as OpenMPT does by default |

- **The check runs in the time of libopenmpt's audio.** Its ticks are whole samples of the output rate
  (`floor(rate × 2.5 / BPM)`), a little shorter than a CIA tick, and a tempo change starts **one tick late**, as on an
  Amiga. The harness gives the renderer those lengths (`ModRenderOptions.TickDurations`), so the score measures the sound
  and not the clock. Without them the renderer uses the tick player's own (Amiga) times.
- **libopenmpt's audio and its song time disagree on tempo changes** (measured on the corpus). Its row times
  (`openmpt_module_set_position_order_row`, used for the timeline check) apply a change on the tick that read it; its
  rendered audio applies it a tick later. A song that sets its tempo on its first row starts with one tick at 125 BPM:
  `musix-omg-plz-help` (96 BPM) is 266 samples later at 44.1 kHz than its row times say, `musix-cute-october`
  (160 BPM) 193 samples earlier. Timed the song-time way, those songs scored up to 1.59 dB; timed the audio way,
  0.02–0.14 dB. The timeline check keeps the song-time rule, since that is what it compares.
- **The renderer's score is not aligned.** Because it keeps the reference's time, the harness scores it with alignment
  off (`CandidateRender.InReferenceTime`). On a song that repeats, the alignment could match a bar with the wrong
  repeat (`musix-cute-october`: 743 ms off at 22.05 kHz, 4.33 dB). It also moves in steps of 256 samples, so it rounded
  the first-tick offset away at 44.1 kHz and hid it.
- **The bar is 1.25 dB of whole-song log-mel distance on every corpus song** (David: measure, then pick). After the
  fixes the worst song scored 0.85 dB at 44.1 kHz (`drozerix_-_ai_renaissance`), 0.76 dB at 22.05 kHz
  (`soundtracker15`) and 0.94 dB at 8 kHz (`fx-poly1`); the bar was picked when the worst was 1.02 dB. The harness
  fails a run when any song is over the bar (`ModRendererCheck`, [harness.md](../harness.md#in-app-mod-renderer)).
- **Notes above ProTracker's range play as OpenMPT plays them**, a tick-player change recorded in
  [ADR 0055](0055-notes-beyond-protracker-range.md). David heard the ProTracker behaviour (a stuck tone, a missing
  melody in `10kdub`) and called it wrong.

## Known differences

The first two come from the tick player, which follows ProTracker 2, not from the mixer. All are small; plan task
T7.6 closes them.

- **Finetune of in-range notes.** ProTracker reads a finetuned note from its period tables, OpenMPT computes it as
  2^(−finetune/96) of the note's period. They differ by up to about 6 cents.
- **The first pass of a loop.** When a sample's loop ends before the sample does, ProTracker plays to the end of the
  sample once before looping; OpenMPT jumps back at the loop end. Pitch and loudness agree; the waveform differs from
  the first loop on.
- **A wandering offset in one song.** `sharpojects_cantpleaseeveryone` (speed 3, tempo changes and jumps) runs 1–3 ms
  early against libopenmpt's audio, by an amount that changes through the song, though every row time matches. Its
  cause is not known yet; T7.6 looks for it.

## Options considered

- **OpenMPT's clean default, measured** (chosen): the reference the harness already uses, the sound listeners know,
  and a tight bar is possible.
- **A real Amiga** (hard L/R, no interpolation, A500 filter, LED filter): its own sound, but every score against the
  clean reference would carry the difference, and no reference render of it exists to check against.
- **Let the harness pick the reference player's settings** (render libopenmpt the Amiga way): the clean sound is what
  the user wants the Genesis to match.
- **Copy OpenMPT's filters from memory:** the remembered values for the strongest filter (β 2.7625, cutoff 0.425) did
  not match libopenmpt's measured response (−40 dB where libopenmpt has −21 dB). Measuring fixed scores at 8 kHz and
  22.05 kHz.

One early probe seemed to show libopenmpt going silent above about 2.08× the output rate. A wider probe (periods 113 to
300 at 8 to 22.05 kHz, looped, one-shot and short samples) showed it never does: the silence was a bug in the
measuring script. Nothing is silenced.

## Consequences

- `src/Render/` now holds `ModRenderer`, `SincTable` and `StereoAudio` (moved from the harness, so the renderer can
  return it).
- The harness has a `mod-renderer` column, a report section and a non-zero exit when the renderer is over the bar.
- The optimizer (M8–M9) can render the target MOD in-process: the same sound the harness's reference has, at any rate.
- Plan task T7.6 closes the known differences.
