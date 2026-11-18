# ADR 0121: FM notes start with a 1 ms attack, not a step

- **Status:** Proposed
- **Date:** 2026-11-17

## Context

David, listening to brun_jul on the A/B page (side F): "Why do the FM notes start with a slight click when they hit?"

Every patch in `FmPatchLibrary`, and the harmonic patches built from a looped tone's harmonics ([ADR 0118](0118-harmonic-patches.md)),
used **attack rate 31**. On the YM2612 that isn't a fast attack but no attack at all: on key-on the envelope jumps from
silence to full level within one sample, and every operator's phase starts again at 0. In the render, the lead coming in on top
of brun_jul's bass at 1:56.96 changes the waveform's slope from about −150 to +1100 per sample in one sample: a kink, heard as
a click.

The MOD's own samples start just as abruptly (AKWF single-cycle waves, at half level within about 8 frames), but they are
not heard that way. OpenMPT, the harness's reference, ramps every note in over a fraction of a millisecond (its declick),
and on an Amiga the output filter rounds the edge. The Genesis does neither, so the step reaches the speaker.

## Decision

**Notes that start at once start with attack rate 27** (`FmPatchLibrary.SoftAttackRate`), about 1 ms to full level:

- every library operator whose attack was 31 (the default of `FmPatchLibrary.Op`); the patches with a slower attack of their
  own (Brass, Strings) keep it;
- the carriers of every harmonic patch (`HarmonicPatch`).

In the test render of brun_jul, rate 27 turns the one-sample step into a ramp of about 40 samples (0.9 ms at 44.1 kHz), and from
there the waveform matches the rate-31 render sample for sample: the note lands on the same tick, only its first millisecond
is softer.

## Options considered

- **Attack rate 27 for every instant attack (chosen).** One constant, the same on DMF, XGM and XGM2, no cost in bytes or
  channels; the onset stays well under the 5–10 ms where an attack starts to sound slow.
- **Rate 28 or 29.** A shorter ramp (about 0.5 ms) that still clicks a little on low, loud notes; 27 was measured first and
  removed the step entirely.
- **Measure each sample's own attack and fit the rate to it.** More faithful for samples with a slow attack, but the samples
  that click here have none to measure: the softening is the reference player's, not the sample's.
- **Leave rate 31.** It is what the hardware does with a step, and the click is what David heard.

## Consequences

- The simple mapper's FM notes, and the optimizer's starting points (the library patches its FM fit starts from), start over
  about 1 ms. The DMF effect probes (`tests/golden/dmf-effect-probes`) change only in their attack-rate bytes; their WAV
  renders are from before this ADR, about 1 ms of each onset apart.
- The optimizer's local search (`FmLocalSearch`) can still step a fitted patch's attack up to 31. Whether to cap it at
  `SoftAttackRate` too follows David's verdict on this change.
- To be checked by ear on the brun_jul A/B page (side G against F); status Proposed until then.
