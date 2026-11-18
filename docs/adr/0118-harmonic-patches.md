# ADR 0118: FM patches built from a looped tone's harmonics

- **Status:** Accepted
- **Date:** 2026-11-17

## Context

In `brun_jul` the bass from 1:00 on (sample 1, `akwf_0426.8svx`, a 128-frame single-cycle loop) sounded an octave
below the original. Its pitch was measured right (one 128-frame cycle), but its second harmonic is 10.6 dB louder than
its fundamental, so the ear hears the octave above. The simple mapper ([ADR 0040](0040-simple-mapper.md)) picks one of
five library patches by brightness; a slow waveform reads as dull and gets the Sine patch, which plays only the quiet
fundamental. A pure 49 Hz sine is all that was left.

The brightness rule only says how much high content a sample has, not where it is. Most looped samples in MODs are
single-cycle chip waveforms (AKWF, hand-drawn 32-byte loops), and their few harmonics are known exactly from the loop.
David chose to build each one's patch from its own harmonics (2026-11-17, plan T5.3c).

## Decision

- **`HarmonicPatch`** (`src/Mapping/HarmonicPatch.cs`) measures a tone loop at its measured pitch
  ([`SamplePitch`](0117-tail-and-noise-loops.md)): the loop must be a whole number of cycles (within 5 % of a cycle),
  and harmonic *k* of a loop of *c* cycles is then exactly DFT bin *k·c*. Harmonics 1–15 (the range of the MUL register)
  are measured; the four loudest are kept, dropping any more than 40 dB below the loudest.
- When those harmonics hold at least **90 %** of the loop's power (without its DC offset), the patch is four sine carriers
  (algorithm 7), each at its harmonic's multiple and its measured level (TL in 0.75 dB steps), scaled so their amplitudes
  add up to full scale and the channel can't clip. A lone fundamental is the library's Sine patch. The phases of the
  harmonics aren't kept: the ear hears levels, not phases, in a held tone.
- Otherwise (a sawtooth spreads its power over many harmonics; four hold about 86 %), the brightness rule picks a
  library patch as before.
- Only tone loops are measured; a tail after a pitched recording ([ADR 0117](0117-tail-and-noise-loops.md)) keeps the
  brightness rule.
- **Every choice is a conversion note**, after the routing note: `harmonics 1, 2, 3, 6 hold 100 % of its tone, so its FM
  patch sounds those harmonics at their levels`, `its fundamental holds 100 % of its tone, so it is played as a plain
  sine`, or `its four loudest harmonics (1, 2, 4, 6) hold only 87.9 % of its tone, so its FM patch is chosen by how
  bright it is`.
- `FmPatch` compares by its settings, operator by operator, so two patches built alike (two instruments on one sample,
  or two runs) are the same patch.

## Options considered

- **Chosen: additive patch from the measured harmonics.** Exact for the chip waveforms that make up most looped
  samples, cheap (a DFT of a short loop), and it leaves the octave where the ear puts it.
- **Play the Sine patch at the loudest harmonic.** Rejected: right octave, but the fundamental is gone and the bass
  loses its body; and it fixes only this one shape.
- **Leave it to the FM fitter (plan T8.2).** It searches patches against the sample, but only for the optimizer
  (`--quality` above normal, not built yet); the simple mapper is what converts today.
- **Fit modulators (FM proper) instead of four carriers.** Rejected for the simple mapper: four carriers already hold
  ≥ 90 % of the waveforms it is used for, and modulation indexes need a search. The fitter does that.

## Consequences

- On `brun_jul` the bass is at the original's octave at 1:00, 1:30 and 2:00 (the loudest note heard matches the
  original's: G2, D3, C3, where it was G1, D2, C2).
- On the corpus, the harness's `genesis` column (log-mel dB, lower is better) gets better on seven songs and worse on
  none, 7.31 dB in all (2026-11-17): `soundtracker15` 19.52 → 15.59, `10kdub` 11.75 → 10.55,
  `drozerix_-_ai_renaissance` 15.14 → 13.99, `fx-poly1` 6.66 → 5.92, `drozerix_-_the_hawks_claw` 11.55 → 11.42,
  `sharpojects_cantpleaseeveryone` 8.95 → 8.83, `musix-rubbish` 12.26 → 12.22.
- A sample played by a harmonic patch takes one FM instrument of its own in a DMF, named `Harmonic 1+2+3+6` and so on.
- The effect probes' sine lead keeps the library Sine patch; their descriptions gain the plain-sine note.
- Docs: [channel-mapping.md](../channel-mapping.md), [implementation-plan.md](../implementation-plan.md) (T5.3c).
