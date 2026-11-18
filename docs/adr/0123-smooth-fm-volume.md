# ADR 0123: A big volume jump on a sounding FM note is spread over ticks (XGM and XGM2)

- **Status:** Proposed
- **Date:** 2026-11-17

## Context

The player writes a sounding FM note's carrier TLs once per tick, and the YM2612 applies a TL change within one sample.
A MOD volume step of 12 dB therefore becomes a 12 dB step in the waveform, which is heard as a click.

`brun_jul`'s melody swaps volumes between two channels every row. One channel jumps to full with a new note. The other
drops from 64 to 16 (`C10`, −12 dB) without a new note, while its note is still loud. At 5.28 s the right channel goes
from 4946 to 1352 in one sample. OpenMPT ramps volume changes (its declick), so the original doesn't click there. David
heard the clicks on the melody notes after the soft attack (T5.3f, ADR 0121) made no difference (2026-11-17, plan T5.3g).

XGM and XGM2 are frame-quantized: a register write happens on a frame (one tick at 50 Hz), so no ramp shorter than a tick
can be stored in the file.

## Decision

- **Cap the step.** On XGM and XGM2, a sounding FM note's level moves at most 8 TL steps (6 dB) a tick. A bigger jump in
  the MOD's volume is spread over the ticks after it: a 12 dB drop becomes two 6 dB steps, 20 ms apart.
- **New notes start at their own level.** A note that starts on this tick is keyed on at its target level at once, as the
  key-on starts the envelope from silence anyway.
- **Quiet steps go straight.** When both levels are 48 dB or more below full (64 TL steps), the level goes straight to its
  target: the step is too quiet to hear, and a fade to silence still ends on time.
- **The DMF is unchanged.** DefleMask sets a channel's volume at once, so the DMF and its render keep the instant step.
  The harness's `genesis` column renders the DMF, so it doesn't measure this change; the A/B clips do.
- No conversion note: the change follows OpenMPT's own smoothing, it doesn't drop anything.

## Options considered

- **Chosen: at most 6 dB a tick.** Halves the size of the click on brun_jul's 12 dB drops and delays the end level by one
  tick (20 ms), well inside a row (120 ms).
- **4 steps (3 dB) a tick.** Smaller steps, but a 12 dB drop takes four ticks (80 ms), most of a row: the rhythm of the
  volume swaps blurs.
- **Ramp inside a tick.** Not storable: XGM and XGM2 write registers once a frame.
- **Retrigger the note at the new level.** Key-off/key-on resets the phase, which clicks too.

## Consequences

- XGM, XGM2 and the WAV render of them follow a big volume drop one tick late; the DMF doesn't.
- Tremolo deeper than 6 dB a tick is flattened a little. Rare in MODs.
